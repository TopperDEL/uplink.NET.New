using System.Runtime.InteropServices;
using uplink.NET.Exceptions;
using uplink.NET.Interfaces;
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
        Access access, string bucketName, string key, byte[] objectData)
        => CreateUploadOpAsync(access, bucketName, key, objectData,
            new UploadOptions(), null, startImmediately: true);

    public Task<UploadOperation> UploadObjectAsync(
        Access access, string bucketName, string key, byte[] objectData, bool startImmediately)
        => CreateUploadOpAsync(access, bucketName, key, objectData,
            new UploadOptions(), null, startImmediately);

    public Task<UploadOperation> UploadObjectAsync(
        Access access, string bucketName, string key, byte[] objectData, UploadOptions uploadOptions)
        => CreateUploadOpAsync(access, bucketName, key, objectData,
            uploadOptions, null, startImmediately: true);

    public Task<UploadOperation> UploadObjectAsync(
        Access access, string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, bool startImmediately)
        => CreateUploadOpAsync(access, bucketName, key, objectData,
            uploadOptions, null, startImmediately);

    public Task<UploadOperation> UploadObjectAsync(
        Access access, string bucketName, string key, byte[] objectData, CustomMetadata customMetadata)
        => CreateUploadOpAsync(access, bucketName, key, objectData,
            new UploadOptions(), customMetadata, startImmediately: true);

    public Task<UploadOperation> UploadObjectAsync(
        Access access, string bucketName, string key, byte[] objectData, CustomMetadata customMetadata, bool startImmediately)
        => CreateUploadOpAsync(access, bucketName, key, objectData,
            new UploadOptions(), customMetadata, startImmediately);

    public Task<UploadOperation> UploadObjectAsync(
        Access access, string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, CustomMetadata customMetadata)
        => CreateUploadOpAsync(access, bucketName, key, objectData,
            uploadOptions, customMetadata, startImmediately: true);

    public Task<UploadOperation> UploadObjectAsync(
        Access access, string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, CustomMetadata customMetadata, bool startImmediately)
        => CreateUploadOpAsync(access, bucketName, key, objectData,
            uploadOptions, customMetadata, startImmediately);

    public async Task<UploadOperation> UploadObjectAsync(
        Access access, string bucketName, string key, Stream stream,
        UploadOptions? uploadOptions, CustomMetadata? customMetadata, bool startImmediately)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms).ConfigureAwait(false);
        return await CreateUploadOpAsync(
            access, bucketName, key, ms.ToArray(),
            uploadOptions ?? new UploadOptions(),
            customMetadata,
            startImmediately).ConfigureAwait(false);
    }

    // Internal helper that all upload overloads funnel into
    private static Task<UploadOperation> CreateUploadOpAsync(
        Access access, string bucketName, string key, byte[] objectData,
        UploadOptions? uploadOptions, CustomMetadata? customMetadata, bool startImmediately)
    {
        var op = new UploadOperation(
            access._projectHandle,
            bucketName,
            key,
            objectData,
            new UploadOperation.UplinkOptions(UplinkInterop.DateTimeToUnix(uploadOptions?.Expires)),
            customMetadata);

        if (startImmediately)
            op.StartUploadAsync();

        return Task.FromResult(op);
    }

    // ── Chunked upload ─────────────────────────────────────────────────────────

    public unsafe Task<ChunkedUploadOperation> UploadObjectChunkedAsync(
        Access access, string bucketName, string key,
        UploadOptions? uploadOptions, CustomMetadata? customMetadata)
    {
        var opts = new UplinkInterop.UplinkUploadOptions
        {
            expires = UplinkInterop.DateTimeToUnix(uploadOptions?.Expires)
        };

        UplinkInterop.UplinkUploadResult uploadResult;
        uploadResult = UplinkInterop.uplink_upload_object(
            access._projectHandle, bucketName, key, &opts);

        if (uploadResult.error != nint.Zero)
        {
            var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref uploadResult.error);
            UplinkInterop.uplink_free_upload_result(uploadResult);
            throw new Exception($"Failed to begin upload: {msg}");
        }

        var uploadHandle = uploadResult.upload;

        // Set custom metadata if supplied
        if (customMetadata?.Entries.Count > 0)
            SetCustomMetadataNative(uploadHandle, customMetadata);

        return Task.FromResult(new ChunkedUploadOperation(uploadHandle, key));
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public Task<ObjectList> ListObjectsAsync(Access access, string bucketName)
        => ListObjectsAsync(access, bucketName, new ListObjectsOptions());

    public unsafe Task<ObjectList> ListObjectsAsync(
        Access access, string bucketName, ListObjectsOptions opts)
    {
        return Task.Run(() =>
        {
            var prefix  = Marshal.StringToCoTaskMemUTF8(opts.Prefix ?? string.Empty);
            var cursor  = Marshal.StringToCoTaskMemUTF8(opts.Cursor ?? string.Empty);
            var bucketPtr = Marshal.StringToCoTaskMemUTF8(bucketName);
            try
            {
                var nativeOpts = new UplinkInterop.UplinkListObjectsOptions
                {
                    prefix    = prefix,
                    cursor    = cursor,
                    delimiter = opts.Delimiter == '\0' ? (byte)0 : (byte)opts.Delimiter,
                    recursive = opts.Recursive,
                    system    = opts.System,
                    custom    = opts.Custom
                };

                nint iterator = UplinkInterop.uplink_list_objects(
                    access._projectHandle, bucketPtr, &nativeOpts);

                var list = new ObjectList();
                try
                {
                    while (UplinkInterop.uplink_object_iterator_next(iterator))
                    {
                        nint objPtr = UplinkInterop.uplink_object_iterator_item(iterator);
                        list.Items.Add(UplinkInterop.MarshalObject(objPtr));
                    }

                    nint errPtr = UplinkInterop.uplink_object_iterator_err(iterator);
                    if (errPtr != nint.Zero)
                    {
                        var (msg, _) = UplinkInterop.ConsumeError(errPtr);
                        throw new ObjectListException(msg);
                    }
                }
                finally
                {
                    UplinkInterop.uplink_free_object_iterator(iterator);
                }

                return list;
            }
            finally
            {
                Marshal.FreeCoTaskMem(prefix);
                Marshal.FreeCoTaskMem(cursor);
                Marshal.FreeCoTaskMem(bucketPtr);
            }
        });
    }

    // ── Stat ──────────────────────────────────────────────────────────────────

    public Task<StorjObject> GetObjectAsync(Access access, string bucketName, string key)
    {
        return Task.Run(() =>
        {
            var result = UplinkInterop.uplink_stat_object(access._projectHandle, bucketName, key);
            try
            {
                if (result.error != nint.Zero)
                {
                    var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                    throw new ObjectNotFoundException(key, msg);
                }
                return UplinkInterop.MarshalObject(result.object_);
            }
            finally
            {
                UplinkInterop.uplink_free_object_result(result);
            }
        });
    }

    // ── Download ──────────────────────────────────────────────────────────────

    public Task<DownloadOperation> DownloadObjectAsync(
        Access access, string bucketName, string key, bool startImmediately)
        => DownloadObjectAsync(access, bucketName, key, new DownloadOptions(), startImmediately);

    public Task<DownloadOperation> DownloadObjectAsync(
        Access access, string bucketName, string key,
        DownloadOptions downloadOptions, bool startImmediately)
    {
        var op = new DownloadOperation(
            access._projectHandle, bucketName, key, downloadOptions);

        if (startImmediately)
            op.StartDownloadAsync();

        return Task.FromResult(op);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public Task DeleteObjectAsync(Access access, string bucketName, string key)
    {
        return Task.Run(() =>
        {
            var result = UplinkInterop.uplink_delete_object(access._projectHandle, bucketName, key);
            try
            {
                if (result.error != nint.Zero)
                {
                    var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                    throw new ObjectNotFoundException(key, msg);
                }
            }
            finally
            {
                UplinkInterop.uplink_free_object_result(result);
            }
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static unsafe void SetCustomMetadataNative(
        nint uploadHandle, CustomMetadata metadata)
    {
        var entries = metadata.Entries
            .Select(kv => new UplinkInterop.UplinkCustomMetadataEntry
            {
                key          = Marshal.StringToCoTaskMemUTF8(kv.Key),
                key_length   = (nuint)System.Text.Encoding.UTF8.GetByteCount(kv.Key),
                value        = Marshal.StringToCoTaskMemUTF8(kv.Value),
                value_length = (nuint)System.Text.Encoding.UTF8.GetByteCount(kv.Value)
            })
            .ToArray();

        fixed (UplinkInterop.UplinkCustomMetadataEntry* entriesPtr = entries)
        {
            var nativeMeta = new UplinkInterop.UplinkCustomMetadata
            {
                entries = (nint)entriesPtr,
                count   = (nuint)entries.Length
            };
            var errPtr = UplinkInterop.uplink_upload_set_custom_metadata(uploadHandle, nativeMeta);
            if (errPtr != nint.Zero)
                UplinkInterop.uplink_free_error(errPtr);
        }

        foreach (var e in entries)
        {
            Marshal.FreeCoTaskMem(e.key);
            Marshal.FreeCoTaskMem(e.value);
        }
    }
}
