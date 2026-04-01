using System.Text.Json;
using SQLite;
using uplink.NET.Interfaces;
using uplink.NET.Models;

namespace uplink.NET.Services;

/// <summary>
/// Persists pending uploads in a local SQLite database and processes them
/// sequentially in the background.
/// </summary>
public class UploadQueueService : IUploadQueueService, IDisposable
{
    private const int PartSize = 5 * 1024 * 1024; // 5 MB per multipart part

    private readonly SQLiteAsyncConnection _db;
    private readonly ObjectService _objectService;

    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private bool _disposed;

    public bool UploadInProgress { get; private set; }

    public event UploadQueueChangedEventHandler? UploadQueueChangedEvent;

    /// <param name="databasePath">Full path to the SQLite database file.</param>
    /// <param name="objectService">Object service used to perform actual uploads.</param>
    public UploadQueueService(string databasePath, ObjectService objectService)
    {
        _db            = new SQLiteAsyncConnection(databasePath);
        _objectService = objectService ?? throw new ArgumentNullException(nameof(objectService));
        InitDatabaseAsync().GetAwaiter().GetResult();
    }

    private async Task InitDatabaseAsync()
    {
        await _db.CreateTableAsync<UploadQueueEntry>().ConfigureAwait(false);
        await _db.CreateTableAsync<UploadQueueEntryData>().ConfigureAwait(false);
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
        => await _db.Table<UploadQueueEntry>()
            .Where(e => !e.Failed)
            .ToListAsync()
            .ConfigureAwait(false);

    public Task<int> GetOpenUploadCountAsync()
        => _db.Table<UploadQueueEntry>()
            .Where(e => !e.Failed)
            .CountAsync();

    // ── Cancel / retry ────────────────────────────────────────────────────────

    public async Task CancelUploadAsync(string key)
    {
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

            await Task.Delay(2000, ct).ConfigureAwait(false);
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

            var access = new Access(entry.AccessGrant);
            CustomMetadata? meta = DeserializeMetadata(entry.CustomMetadataJson);

            var uploadOp = await _objectService.UploadObjectAsync(
                access,
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
        _db.CloseAsync().GetAwaiter().GetResult();
    }
}
