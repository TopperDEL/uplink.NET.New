using System.Text.Json;
using SQLite;
using uplink.NET.Interfaces;
using uplink.NET.Models;

namespace uplink.NET.Services;

/// <summary>
/// Persists pending uploads in a local SQLite database and processes them
/// sequentially in the background.
/// </summary>
public class UploadQueueService : IUploadQueueService, IDisposable, IAsyncDisposable
{
    private const int PartSize          = 5 * 1024 * 1024; // 5 MB per multipart part
    private const int PollingIntervalMs = 2_000;            // poll interval for the background loop
    private const string DiagnosticsEnvironmentVariableName = "UPLINK_NET_ENABLE_DIAGNOSTICS";
    private static readonly bool DiagnosticsEnabled =
        string.Equals(Environment.GetEnvironmentVariable(DiagnosticsEnvironmentVariableName), "1", StringComparison.Ordinal) ||
        string.Equals(Environment.GetEnvironmentVariable(DiagnosticsEnvironmentVariableName), "true", StringComparison.OrdinalIgnoreCase);

    private readonly SQLiteAsyncConnection _db;
    private readonly object _processingSync = new();
    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private bool _initialized;
    private bool _disposed;
    private bool _restartScheduled;

    public bool UploadInProgress { get; private set; }

    public event UploadQueueChangedEventHandler? UploadQueueChangedEvent;

    /// <param name="databasePath">Full path to the SQLite database file.</param>
    public UploadQueueService(string databasePath)
    {
        _db = new SQLiteAsyncConnection(databasePath);
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _db.CreateTableAsync<UploadQueueEntry>().ConfigureAwait(false);
        await _db.CreateTableAsync<UploadQueueEntryData>().ConfigureAwait(false);
        _initialized = true;
    }

    // ── Add to queue ──────────────────────────────────────────────────────────

    public Task AddObjectToUploadQueueAsync(
        string bucketName, string key, string accessGrant,
        byte[] objectData, string identifier)
        => AddObjectToUploadQueueAsync(bucketName, key, accessGrant,
            objectData, identifier, new CustomMetadata());

    public async Task AddObjectToUploadQueueAsync(
        string bucketName, string key, string accessGrant,
        byte[] objectData, string identifier, CustomMetadata customMetadata)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var entry = new UploadQueueEntry
        {
            BucketName         = bucketName,
            Key                = key,
            AccessGrant        = accessGrant,
            Identifier         = identifier,
            TotalBytes         = objectData.Length,
            CustomMetadataJson = SerializeMetadata(customMetadata)
        };

        await _db.InsertAsync(entry).ConfigureAwait(false);

        var data = new UploadQueueEntryData
        {
            UploadQueueEntryId = entry.Id,
            Bytes              = objectData
        };
        await _db.InsertAsync(data).ConfigureAwait(false);

        UploadQueueChangedEvent?.Invoke(QueueChangeType.EntryAdded, entry);
    }

    public Task AddObjectToUploadQueueAsync(
        string bucketName, string key, string accessGrant,
        Stream stream, string identifier)
        => AddObjectToUploadQueueAsync(bucketName, key, accessGrant,
            stream, identifier, new CustomMetadata());

    public async Task AddObjectToUploadQueueAsync(
        string bucketName, string key, string accessGrant,
        Stream stream, string identifier, CustomMetadata customMetadata)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms).ConfigureAwait(false);
        await AddObjectToUploadQueueAsync(
            bucketName, key, accessGrant,
            ms.ToArray(), identifier, customMetadata).ConfigureAwait(false);
    }

    // ── Query ─────────────────────────────────────────────────────────────────

    public async Task<List<UploadQueueEntry>> GetAwaitingUploadsAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        return await _db.Table<UploadQueueEntry>()
            .Where(e => !e.Failed)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<int> GetOpenUploadCountAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        return await _db.Table<UploadQueueEntry>()
            .Where(e => !e.Failed)
            .CountAsync()
            .ConfigureAwait(false);
    }

    // ── Cancel / retry ────────────────────────────────────────────────────────

    public async Task CancelUploadAsync(string key)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var entry = await _db.Table<UploadQueueEntry>()
            .Where(e => e.Key == key)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (entry == null) return;

        await _db.DeleteAsync(entry).ConfigureAwait(false);
        await _db.Table<UploadQueueEntryData>()
            .Where(d => d.UploadQueueEntryId == entry.Id)
            .DeleteAsync()
            .ConfigureAwait(false);

        UploadQueueChangedEvent?.Invoke(QueueChangeType.EntryRemoved, entry);
    }

    public async Task RetryAsync(string key)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var entry = await _db.Table<UploadQueueEntry>()
            .Where(e => e.Key == key)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (entry == null) return;

        entry.Failed        = false;
        entry.FailedMessage = string.Empty;
        await _db.UpdateAsync(entry).ConfigureAwait(false);
        UploadQueueChangedEvent?.Invoke(QueueChangeType.EntryUpdated, entry);
    }

    // ── Background processing ─────────────────────────────────────────────────

    public void ProcessQueueInBackground()
    {
        lock (_processingSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_processingTask == null || _processingTask.IsCompleted)
            {
                StartProcessingLoopNoLock();
                return;
            }

            if (_cts?.IsCancellationRequested != true || _restartScheduled)
                return;

            _restartScheduled = true;
            LogDiagnostics("Queue restart requested while the previous processing loop is still stopping.");
            _processingTask.ContinueWith(
                _ =>
                {
                    lock (_processingSync)
                    {
                        if (_disposed || !_restartScheduled)
                            return;

                        _restartScheduled = false;
                        StartProcessingLoopNoLock();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    public void StopQueueInBackground()
    {
        lock (_processingSync)
        {
            LogDiagnostics("Queue stop requested.");
            _cts?.Cancel();
        }
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        LogDiagnostics("Queue processing loop started.");
        try
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var entries = await _db.Table<UploadQueueEntry>()
                    .Where(e => !e.Failed)
                    .ToListAsync()
                    .ConfigureAwait(false);

                foreach (var entry in entries)
                {
                    if (ct.IsCancellationRequested) break;
                    await ProcessEntryAsync(entry).ConfigureAwait(false);
                }

                await Task.Delay(PollingIntervalMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogDiagnostics("Queue processing loop observed cancellation.");
        }
        finally
        {
            LogDiagnostics("Queue processing loop stopped.");
        }
    }

    private async Task ProcessEntryAsync(UploadQueueEntry entry)
    {
        UploadInProgress = true;
        LogDiagnostics($"Processing upload queue entry '{entry.Key}'.");
        try
        {
            var data = await _db.Table<UploadQueueEntryData>()
                .Where(d => d.UploadQueueEntryId == entry.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (data == null)
            {
                await MarkFailedAsync(entry, "No data found").ConfigureAwait(false);
                return;
            }

            using var access = new Access(entry.AccessGrant);
            var objectService = new ObjectService(access);
            CustomMetadata? meta = DeserializeMetadata(entry.CustomMetadataJson);

            var uploadOp = await objectService.UploadObjectAsync(
                entry.BucketName,
                entry.Key,
                data.Bytes,
                new UploadOptions(),
                meta!,
                startImmediately: false).ConfigureAwait(false);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            uploadOp.UploadOperationEnded += op =>
            {
                if (op.Completed)
                    tcs.TrySetResult(true);
                else
                    tcs.TrySetResult(false);
            };

            var uploadTask = uploadOp.StartUploadAsync() ?? Task.CompletedTask;
            bool success = await tcs.Task.ConfigureAwait(false);
            await uploadTask.ConfigureAwait(false);

            if (success)
            {
                await _db.DeleteAsync(entry).ConfigureAwait(false);
                await _db.DeleteAsync(data).ConfigureAwait(false);
                UploadQueueChangedEvent?.Invoke(QueueChangeType.EntryRemoved, entry);
                LogDiagnostics($"Upload queue entry '{entry.Key}' completed successfully.");
            }
            else
            {
                await MarkFailedAsync(entry, uploadOp.ErrorMessage ?? "Upload failed")
                    .ConfigureAwait(false);
                LogDiagnostics($"Upload queue entry '{entry.Key}' failed: {uploadOp.ErrorMessage ?? "Upload failed"}");
            }
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(entry, ex.Message).ConfigureAwait(false);
            LogDiagnostics($"Upload queue entry '{entry.Key}' threw an exception: {ex.Message}");
        }
        finally
        {
            UploadInProgress = false;
        }
    }

    private async Task MarkFailedAsync(UploadQueueEntry entry, string message)
    {
        entry.Failed        = true;
        entry.FailedMessage = message;
        await _db.UpdateAsync(entry).ConfigureAwait(false);
        UploadQueueChangedEvent?.Invoke(QueueChangeType.EntryUpdated, entry);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SerializeMetadata(CustomMetadata? meta)
    {
        if (meta == null || meta.Entries.Count == 0) return string.Empty;
        return JsonSerializer.Serialize(meta.Entries);
    }

    private static CustomMetadata? DeserializeMetadata(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (dict == null) return null;
        return new CustomMetadata { Entries = dict };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_processingSync)
        {
            _restartScheduled = false;
            _cts?.Cancel();
        }
        try
        {
            _processingTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
        catch (AggregateException)
        {
        }
        catch (Exception)
        {
            // Disposal is best-effort; avoid rethrowing teardown failures
            // (for example ObjectDisposedException while shutdown races complete)
            // after cancellation has already been requested.
        }
        _cts?.Dispose();
        _cts = null;
        _processingTask = null;
        // Best effort synchronous close; prefer DisposeAsync when possible.
        try { _db.CloseAsync().GetAwaiter().GetResult(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_processingSync)
        {
            _restartScheduled = false;
            _cts?.Cancel();
        }
        if (_processingTask != null)
        {
            try { await _processingTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
        _processingTask = null;
        await _db.CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void StartProcessingLoopNoLock()
    {
        _restartScheduled = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _processingTask = Task.Run(() => ProcessLoopAsync(_cts.Token));
        LogDiagnostics("Queue processing loop scheduled.");
    }

    private static void LogDiagnostics(string message)
    {
        if (!DiagnosticsEnabled)
            return;

        Console.WriteLine($"[UploadQueueService {DateTime.UtcNow:O}] {message}");
    }
}
