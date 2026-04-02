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

    private readonly SQLiteAsyncConnection _db;
    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private bool _initialized;
    private bool _disposed;

    public bool UploadInProgress { get; private set; }

    public event UploadQueueChangedEventHandler? UploadQueueChangedEvent;

    /// <param name="databasePath">Full path to the SQLite database file.</param>
    /// <param name="objectService">Object service used to perform actual uploads.</param>
    public UploadQueueService(string databasePath, ObjectService objectService)
    {
        _db = new SQLiteAsyncConnection(databasePath);
        _ = objectService ?? throw new ArgumentNullException(nameof(objectService));
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
        if (_processingTask != null && !_processingTask.IsCompleted)
            return;

        _cts = new CancellationTokenSource();
        _processingTask = Task.Run(() => ProcessLoopAsync(_cts.Token));
    }

    public void StopQueueInBackground()
    {
        _cts?.Cancel();
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
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

    private async Task ProcessEntryAsync(UploadQueueEntry entry)
    {
        UploadInProgress = true;
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

            var tcs = new TaskCompletionSource<bool>();
            uploadOp.UploadOperationEnded += op =>
            {
                if (op.Completed)
                    tcs.TrySetResult(true);
                else
                    tcs.TrySetResult(false);
            };
            _ = uploadOp.StartUploadAsync();
            bool success = await tcs.Task.ConfigureAwait(false);

            if (success)
            {
                await _db.DeleteAsync(entry).ConfigureAwait(false);
                await _db.DeleteAsync(data).ConfigureAwait(false);
                UploadQueueChangedEvent?.Invoke(QueueChangeType.EntryRemoved, entry);
            }
            else
            {
                await MarkFailedAsync(entry, uploadOp.ErrorMessage ?? "Upload failed")
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(entry, ex.Message).ConfigureAwait(false);
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
        _cts?.Cancel();
        // Best effort synchronous close; prefer DisposeAsync when possible.
        try { _db.CloseAsync().GetAwaiter().GetResult(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        await _db.CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
