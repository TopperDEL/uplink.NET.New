using System.Runtime.InteropServices;
using NativeCallTrace = uplink.NET.Diagnostics.UplinkDiagnosticsSession.NativeCallTrace;
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
        string bucketName, string key, byte[] objectData)
        => CreateUploadOpAsync(bucketName, key, objectData,
            new UploadOptions(), null, startImmediately: true);

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData, bool startImmediately)
        => CreateUploadOpAsync(bucketName, key, objectData,
            new UploadOptions(), null, startImmediately);

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData, UploadOptions uploadOptions)
        => CreateUploadOpAsync(bucketName, key, objectData,
            uploadOptions, null, startImmediately: true);

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, bool startImmediately)
        => CreateUploadOpAsync(bucketName, key, objectData,
            uploadOptions, null, startImmediately);

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData, CustomMetadata customMetadata)
        => CreateUploadOpAsync(bucketName, key, objectData,
            new UploadOptions(), customMetadata, startImmediately: true);

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData, CustomMetadata customMetadata, bool startImmediately)
        => CreateUploadOpAsync(bucketName, key, objectData,
            new UploadOptions(), customMetadata, startImmediately);

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, CustomMetadata customMetadata)
        => CreateUploadOpAsync(bucketName, key, objectData,
            uploadOptions, customMetadata, startImmediately: true);

    public Task<UploadOperation> UploadObjectAsync(
        string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, CustomMetadata customMetadata, bool startImmediately)
        => CreateUploadOpAsync(bucketName, key, objectData,
            uploadOptions, customMetadata, startImmediately);

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

    // Internal helper that all upload overloads funnel into
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

    // ── Chunked upload ─────────────────────────────────────────────────────────

    public unsafe Task<ChunkedUploadOperation> UploadObjectChunkedAsync(
        string bucketName, string objectKey,
        UploadOptions? uploadOptions, CustomMetadata? customMetadata)
    {
        var projectLease = _access.AcquireProjectLease();
        using var trace = _access.Trace("uplink_upload_object", ("bucket", bucketName), ("key", objectKey));
        var opts = new UplinkInterop.UplinkUploadOptions
        {
            expires = UplinkInterop.DateTimeToUnix(uploadOptions?.Expires)
        };

        UplinkInterop.UplinkUploadResult uploadResult;
        uploadResult = UplinkInterop.uplink_upload_object(
            projectLease.Handle, bucketName, objectKey, &opts);

        if (uploadResult.error != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref uploadResult.error);
            trace?.NativeError(msg, code);
            UplinkInterop.uplink_free_upload_result(uploadResult);
            projectLease.Dispose();
            throw new Exception($"Failed to begin upload: {msg}");
        }

        var uploadHandle = uploadResult.upload;

        if (uploadHandle == nint.Zero)
        {
            trace?.Fail("Native library returned a null upload handle without an error.");
            UplinkInterop.uplink_free_upload_result(uploadResult);
            projectLease.Dispose();
            throw new Exception("Failed to begin upload: native library returned a null upload handle without an error.");
        }

        try
        {
            // Set custom metadata if supplied
            if (customMetadata?.Entries.Count > 0)
            {
                using var metadataTrace = _access.Trace("uplink_upload_set_custom_metadata", ("bucket", bucketName), ("key", objectKey));
                SetCustomMetadataNative(uploadHandle, customMetadata, metadataTrace);
            }

            trace?.Success();
            return Task.FromResult(new ChunkedUploadOperation(uploadHandle, objectKey, projectLease, _access));
        }
        catch
        {
            UplinkInterop.FreeUploadHandle(uploadHandle);
            projectLease.Dispose();
            throw;
        }
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public Task<ObjectList> ListObjectsAsync(string bucketName)
        => ListObjectsAsync(bucketName, new ListObjectsOptions());

    public unsafe Task<ObjectList> ListObjectsAsync(
        string bucketName, ListObjectsOptions opts)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            using var trace = _access.Trace("uplink_list_objects", ("bucket", bucketName), ("prefix", opts.Prefix ?? string.Empty));
            try
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
                        recursive = opts.Recursive,
                        system    = opts.System,
                        custom    = opts.Custom
                    };

                    nint iterator = UplinkInterop.uplink_list_objects(
                        projectLease.Handle, bucketPtr, &nativeOpts);

                    var list = new ObjectList();
                    try
                    {
                        if (iterator == nint.Zero)
                        {
                            trace?.Fail("Native library returned a null object iterator without an error.");
                            throw new ObjectListException("Native library returned a null object iterator without an error.");
                        }

                        while (UplinkInterop.uplink_object_iterator_next(iterator))
                        {
                            nint objPtr = UplinkInterop.uplink_object_iterator_item(iterator);
                            list.Items.Add(UplinkInterop.MarshalObject(objPtr));
                        }

                        nint errPtr = UplinkInterop.uplink_object_iterator_err(iterator);
                        if (errPtr != nint.Zero)
                        {
                            var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                            trace?.NativeError(msg, code);
                            throw new ObjectListException(msg);
                        }

                        trace?.Success();
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
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    // ── Stat ──────────────────────────────────────────────────────────────────

    public Task<StorjObject> GetObjectAsync(string bucketName, string key)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            using var trace = _access.Trace("uplink_stat_object", ("bucket", bucketName), ("key", key));
            try
            {
                var result = UplinkInterop.uplink_stat_object(projectLease.Handle, bucketName, key);
                try
                {
                    if (result.error != nint.Zero)
                    {
                        var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                        trace?.NativeError(msg, code);
                        throw new ObjectNotFoundException(key, msg);
                    }

                    if (result.object_ == nint.Zero)
                    {
                        trace?.Fail("Native library returned a null object result without an error.");
                        throw new ObjectNotFoundException(key, "Native library returned a null object result without an error.");
                    }

                    trace?.Success();
                    return UplinkInterop.MarshalObject(result.object_);
                }
                finally
                {
                    UplinkInterop.uplink_free_object_result(result);
                }
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    public Task<DownloadStream> GetObjectAsStream(
        string bucketName,
        string key)
        => GetObjectAsStream(bucketName, key, new DownloadOptions());

    public Task<DownloadStream> GetObjectAsStream(
        string bucketName,
        string key,
        DownloadOptions downloadOptions)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            var handle = nint.Zero;
            var leaseTransferred = false;
            try
            {
                handle = OpenDownloadHandle(projectLease.Handle, bucketName, key, downloadOptions);
                var length = GetDownloadLength(handle, downloadOptions, bucketName, key);
                var stream = new DownloadStream(handle, length, projectLease, _access);
                leaseTransferred = true;
                handle = nint.Zero;
                return stream;
            }
            finally
            {
                if (handle != nint.Zero)
                    UplinkInterop.FreeDownloadHandle(handle);

                if (!leaseTransferred)
                    projectLease.Dispose();
            }
        });
    }

    // ── Download ──────────────────────────────────────────────────────────────

    public Task<DownloadOperation> DownloadObjectAsync(
        string bucketName, string key, bool startImmediately)
        => DownloadObjectAsync(bucketName, key, new DownloadOptions(), startImmediately);

    public Task<DownloadOperation> DownloadObjectAsync(
        string bucketName, string key,
        DownloadOptions downloadOptions, bool startImmediately)
    {
        var op = new DownloadOperation(
            _access, bucketName, key, downloadOptions);

        if (startImmediately)
            op.StartDownloadAsync();

        return Task.FromResult(op);
    }

    public Task<StorjObject> CopyObjectAsync(
        string sourceBucketName,
        string sourceKey,
        string destinationBucketName,
        string destinationKey)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            using var trace = _access.Trace(
                "uplink_copy_object",
                ("sourceBucket", sourceBucketName),
                ("sourceKey", sourceKey),
                ("destinationBucket", destinationBucketName),
                ("destinationKey", destinationKey));
            try
            {
                var result = UplinkInterop.uplink_copy_object(
                    projectLease.Handle,
                    sourceBucketName,
                    sourceKey,
                    destinationBucketName,
                    destinationKey,
                    nint.Zero);

                try
                {
                    if (result.error != nint.Zero)
                    {
                        var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                        trace?.NativeError(msg, code);
                        throw new IOException($"Failed to copy Storj object: {msg}");
                    }

                    if (result.object_ == nint.Zero)
                    {
                        trace?.Fail("Native library returned a null copied object result without an error.");
                        throw new IOException("Failed to copy Storj object: native library returned a null object result without an error.");
                    }

                    trace?.Success();
                    return UplinkInterop.MarshalObject(result.object_);
                }
                finally
                {
                    UplinkInterop.uplink_free_object_result(result);
                }
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    public Task MoveObjectAsync(
        string sourceBucketName,
        string sourceKey,
        string destinationBucketName,
        string destinationKey)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            using var trace = _access.Trace(
                "uplink_move_object",
                ("sourceBucket", sourceBucketName),
                ("sourceKey", sourceKey),
                ("destinationBucket", destinationBucketName),
                ("destinationKey", destinationKey));
            try
            {
                var errPtr = UplinkInterop.uplink_move_object(
                    projectLease.Handle,
                    sourceBucketName,
                    sourceKey,
                    destinationBucketName,
                    destinationKey,
                    nint.Zero);

                if (errPtr != nint.Zero)
                {
                    var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref errPtr);
                    trace?.NativeError(msg, code);
                    throw new IOException($"Failed to move Storj object: {msg}");
                }

                trace?.Success();
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public Task DeleteObjectAsync(string bucketName, string key)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            using var trace = _access.Trace("uplink_delete_object", ("bucket", bucketName), ("key", key));
            try
            {
                var result = UplinkInterop.uplink_delete_object(projectLease.Handle, bucketName, key);
                try
                {
                    if (result.error != nint.Zero)
                    {
                        var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                        trace?.NativeError(msg, code);
                        throw new ObjectNotFoundException(key, msg);
                    }

                    trace?.Success();
                }
                finally
                {
                    UplinkInterop.uplink_free_object_result(result);
                }
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static unsafe void SetCustomMetadataNative(
        nint uploadHandle, CustomMetadata metadata, NativeCallTrace? trace = null)
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
            {
                var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                trace?.NativeError(msg, code);
                throw new IOException($"Failed to set custom metadata: {msg}");
            }
        }

        foreach (var e in entries)
        {
            Marshal.FreeCoTaskMem(e.key);
            Marshal.FreeCoTaskMem(e.value);
        }
    }

    private unsafe nint OpenDownloadHandle(
        nint projectHandle,
        string bucketName,
        string key,
        DownloadOptions downloadOptions)
    {
        using var trace = _access.Trace("uplink_download_object", ("bucket", bucketName), ("key", key));
        var opts = new UplinkInterop.UplinkDownloadOptions
        {
            offset = downloadOptions.Offset,
            length = downloadOptions.Length
        };

        var result = UplinkInterop.uplink_download_object(
            projectHandle,
            bucketName,
            key,
            &opts);

        if (result.error != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
            trace?.NativeError(msg, code);
            UplinkInterop.uplink_free_download_result(result);
            throw new ObjectNotFoundException(key, msg);
        }

        if (result.download == nint.Zero)
        {
            trace?.Fail("Native library returned a null download handle without an error.");
            UplinkInterop.uplink_free_download_result(result);
            throw new IOException("Failed to open Storj download stream: native library returned a null download handle without an error.");
        }

        trace?.Success();
        return result.download;
    }

    private long GetDownloadLength(
        nint downloadHandle,
        DownloadOptions downloadOptions,
        string bucketName,
        string key)
    {
        using var trace = _access.Trace("uplink_download_info", ("bucket", bucketName), ("key", key));
        var infoResult = UplinkInterop.uplink_download_info(downloadHandle);
        try
        {
            if (infoResult.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref infoResult.error);
                trace?.NativeError(msg, code);
                throw new IOException($"Failed to inspect Storj download stream: {msg}");
            }

            if (infoResult.object_ == nint.Zero)
            {
                trace?.Success("Native library returned no object metadata; falling back to unknown length.");
                return 0;
            }

            var contentLength = UplinkInterop.MarshalObject(infoResult.object_).ContentLength;
            trace?.Success();
            return CalculateDownloadLength(contentLength, downloadOptions);
        }
        finally
        {
            UplinkInterop.uplink_free_object_result(infoResult);
        }
    }

    private static long CalculateDownloadLength(
        long contentLength,
        DownloadOptions downloadOptions)
    {
        var offset = Math.Max(0, downloadOptions.Offset);
        if (contentLength <= offset)
            return 0;

        var remainingLength = contentLength - offset;
        if (downloadOptions.Length < 0)
            return remainingLength;

        return Math.Min(remainingLength, downloadOptions.Length);
    }
}
