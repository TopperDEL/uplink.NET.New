using uplink.NET.Exceptions;
using uplink.NET.Interfaces;
using uplink.NET.Ipc;
using uplink.NET.Models;
using uplink.NET.Native;

namespace uplink.NET.Services;

public class ObjectService : IObjectService
{
    private readonly Access _access;

    public ObjectService(Access access)
    {
        _access = access ?? throw new ArgumentNullException(nameof(access));
    }

    // ── Upload overloads ──────────────────────────────────────────────────────

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData)
        => CreateUploadOpAsync(bucketName, key, objectData, new UploadOptions(), null, startImmediately: true);

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData, bool startImmediately)
        => CreateUploadOpAsync(bucketName, key, objectData, new UploadOptions(), null, startImmediately);

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData, UploadOptions uploadOptions)
        => CreateUploadOpAsync(bucketName, key, objectData, uploadOptions, null, startImmediately: true);

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, bool startImmediately)
        => CreateUploadOpAsync(bucketName, key, objectData, uploadOptions, null, startImmediately);

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData, CustomMetadata customMetadata)
        => CreateUploadOpAsync(bucketName, key, objectData, new UploadOptions(), customMetadata, startImmediately: true);

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData, CustomMetadata customMetadata, bool startImmediately)
        => CreateUploadOpAsync(bucketName, key, objectData, new UploadOptions(), customMetadata, startImmediately);

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, CustomMetadata customMetadata)
        => CreateUploadOpAsync(bucketName, key, objectData, uploadOptions, customMetadata, startImmediately: true);

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, CustomMetadata customMetadata, bool startImmediately)
        => CreateUploadOpAsync(bucketName, key, objectData, uploadOptions, customMetadata, startImmediately);

    public async Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, Stream stream,
        UploadOptions? uploadOptions, CustomMetadata? customMetadata, bool startImmediately)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms).ConfigureAwait(false);
        return await CreateUploadOpAsync(
            bucketName, key, ms.ToArray(),
            uploadOptions ?? new UploadOptions(),
            customMetadata,
            startImmediately).ConfigureAwait(false);
    }

    private Task<UploadOperation> CreateUploadOpAsync(
        string bucketName, string key, byte[] objectData,
        UploadOptions? uploadOptions, CustomMetadata? customMetadata, bool startImmediately)
    {
        var op = new UploadOperation(
            _access,
            bucketName,
            key,
            objectData,
            new UploadOperation.UplinkOptions(UplinkInterop.DateTimeToUnix(uploadOptions?.Expires)),
            customMetadata);

        if (startImmediately)
            op.StartUploadAsync();

        return Task.FromResult(op);
    }

    // ── Chunked upload ────────────────────────────────────────────────────────

    public async Task<ChunkedUploadOperation> UploadObjectChunkedAsync(
        string bucketName, string objectKey,
        UploadOptions? uploadOptions, CustomMetadata? customMetadata)
    {
        var projectLease = _access.AcquireProjectLease();
        try
        {
            var beginResult = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
            {
                ["op"]         = "upload_begin",
                ["project_id"] = projectLease.Handle,
                ["bucket"]     = bucketName,
                ["key"]        = objectKey,
                ["expires"]    = UplinkInterop.DateTimeToUnix(uploadOptions?.Expires)
            }).ConfigureAwait(false);

            if (beginResult.IsError)
            {
                projectLease.Dispose();
                throw new Exception($"Failed to begin chunked upload: {beginResult.ErrorMessage}");
            }

            long uploadId = beginResult.Data.GetProperty("upload_id").GetInt64();

            if (customMetadata?.Entries.Count > 0)
            {
                var metaResult = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
                {
                    ["op"]        = "upload_set_metadata",
                    ["upload_id"] = uploadId,
                    ["entries"]   = customMetadata.Entries
                        .Select(kv => (object?)new Dictionary<string, object?> { ["key"] = kv.Key, ["value"] = kv.Value })
                        .ToArray()
                }).ConfigureAwait(false);

                if (metaResult.IsError)
                {
                    projectLease.Dispose();
                    throw new IOException($"Failed to set custom metadata: {metaResult.ErrorMessage}");
                }
            }

            return new ChunkedUploadOperation(uploadId, objectKey, projectLease, _access);
        }
        catch
        {
            projectLease.Dispose();
            throw;
        }
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public Task<ObjectList> ListObjectsAsync(string bucketName)
        => ListObjectsAsync(bucketName, new ListObjectsOptions());

    public async Task<ObjectList> ListObjectsAsync(string bucketName, ListObjectsOptions opts)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]          = "object_list",
            ["project_id"]  = projectLease.Handle,
            ["bucket"]      = bucketName,
            ["prefix"]      = opts.Prefix ?? string.Empty,
            ["cursor"]      = opts.Cursor ?? string.Empty,
            ["recursive"]   = opts.Recursive,
            ["system_meta"] = opts.System,
            ["custom_meta"] = opts.Custom
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new ObjectListException(result.ErrorMessage!);

        var list = new ObjectList();
        if (result.Data.TryGetProperty("objects", out var objectsElem))
        {
            foreach (var o in objectsElem.EnumerateArray())
                list.Items.Add(ParseStorjObject(o));
        }

        return list;
    }

    // ── Stat ──────────────────────────────────────────────────────────────────

    public async Task<StorjObject> GetObjectAsync(string bucketName, string key)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]         = "object_stat",
            ["project_id"] = projectLease.Handle,
            ["bucket"]     = bucketName,
            ["key"]        = key
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new ObjectNotFoundException(key, result.ErrorMessage!);

        return ParseStorjObject(result.Data);
    }

    public Task<DownloadStream> GetObjectAsStream(string bucketName, string key)
        => GetObjectAsStream(bucketName, key, new DownloadOptions());

    public async Task<DownloadStream> GetObjectAsStream(
        string bucketName, string key, DownloadOptions downloadOptions)
    {
        var projectLease = _access.AcquireProjectLease();
        try
        {
            var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
            {
                ["op"]         = "download_begin",
                ["project_id"] = projectLease.Handle,
                ["bucket"]     = bucketName,
                ["key"]        = key,
                ["offset"]     = downloadOptions.Offset,
                ["length"]     = downloadOptions.Length
            }).ConfigureAwait(false);

            if (result.IsError)
            {
                projectLease.Dispose();
                throw new ObjectNotFoundException(key, result.ErrorMessage!);
            }

            long downloadId = result.Data.GetProperty("download_id").GetInt64();
            long totalBytes = result.Data.GetProperty("total_bytes").GetInt64();

            return new DownloadStream(downloadId, totalBytes, projectLease, _access);
        }
        catch
        {
            projectLease.Dispose();
            throw;
        }
    }

    // ── Download ──────────────────────────────────────────────────────────────

    public Task<DownloadOperation> DownloadObjectAsync(
        string bucketName, string key, bool startImmediately)
        => DownloadObjectAsync(bucketName, key, new DownloadOptions(), startImmediately);

    public Task<DownloadOperation> DownloadObjectAsync(
        string bucketName, string key,
        DownloadOptions downloadOptions, bool startImmediately)
    {
        var op = new DownloadOperation(_access, bucketName, key, downloadOptions);
        if (startImmediately)
            op.StartDownloadAsync();
        return Task.FromResult(op);
    }

    public async Task<StorjObject> CopyObjectAsync(
        string sourceBucketName, string sourceKey,
        string destinationBucketName, string destinationKey)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]         = "object_copy",
            ["project_id"] = projectLease.Handle,
            ["src_bucket"] = sourceBucketName,
            ["src_key"]    = sourceKey,
            ["dst_bucket"] = destinationBucketName,
            ["dst_key"]    = destinationKey
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new IOException($"Failed to copy Storj object: {result.ErrorMessage}");

        return ParseStorjObject(result.Data);
    }

    public async Task MoveObjectAsync(
        string sourceBucketName, string sourceKey,
        string destinationBucketName, string destinationKey)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]         = "object_move",
            ["project_id"] = projectLease.Handle,
            ["src_bucket"] = sourceBucketName,
            ["src_key"]    = sourceKey,
            ["dst_bucket"] = destinationBucketName,
            ["dst_key"]    = destinationKey
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new IOException($"Failed to move Storj object: {result.ErrorMessage}");
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task DeleteObjectAsync(string bucketName, string key)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]         = "object_delete",
            ["project_id"] = projectLease.Handle,
            ["bucket"]     = bucketName,
            ["key"]        = key
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new ObjectNotFoundException(key, result.ErrorMessage!);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static StorjObject ParseStorjObject(System.Text.Json.JsonElement e)
    {
        var obj = new StorjObject
        {
            Key      = e.TryGetProperty("obj_key",       out var k)  ? k.GetString()  ?? string.Empty : string.Empty,
            IsPrefix = e.TryGetProperty("obj_is_prefix", out var ip) && ip.GetBoolean()
        };

        var created       = e.TryGetProperty("obj_created",        out var c)  ? c.GetInt64()  : 0L;
        var expires       = e.TryGetProperty("obj_expires",        out var ex) ? ex.GetInt64() : 0L;
        var contentLength = e.TryGetProperty("obj_content_length", out var cl) ? cl.GetInt64() : 0L;

        obj.SystemMetadata = new SystemMetadata
        {
            Created       = created  == 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime,
            Expires       = expires  == 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime,
            ContentLength = contentLength
        };

        if (e.TryGetProperty("obj_custom_metadata", out var cm) && cm.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var meta = new CustomMetadata();
            foreach (var entry in cm.EnumerateArray())
            {
                var entryKey   = entry.TryGetProperty("key",   out var ek) ? ek.GetString() ?? string.Empty : string.Empty;
                var entryValue = entry.TryGetProperty("value", out var ev) ? ev.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrEmpty(entryKey))
                    meta.Entries[entryKey] = entryValue;
            }
            if (meta.Entries.Count > 0)
                obj.CustomMetadata = meta;
        }

        return obj;
    }
}
