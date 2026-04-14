using System.Runtime.InteropServices;
using uplink.NET.Exceptions;
using uplink.NET.Interfaces;
using uplink.NET.Models;
using uplink.NET.Native;

namespace uplink.NET.Services;

public class MultipartUploadService : IMultipartUploadService
{
    private const int PartWriteChunkSize = 80 * 1024;

    private readonly Access _access;

    public MultipartUploadService(Access access)
    {
        _access = access ?? throw new ArgumentNullException(nameof(access));
    }

    public unsafe Task<UploadInfo> BeginUploadAsync(
        string bucketName, string objectKey, UploadOptions uploadOptions)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            using var trace = _access.Trace("uplink_begin_upload", ("bucket", bucketName), ("key", objectKey));
            try
            {
                var opts = new UplinkInterop.UplinkUploadOptions
                {
                    expires = UplinkInterop.DateTimeToUnix(uploadOptions?.Expires)
                };

                var result = UplinkInterop.uplink_begin_upload(
                    projectLease.Handle, bucketName, objectKey, &opts);
                try
                {
                    if (result.error != nint.Zero)
                    {
                        var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                        trace?.NativeError(msg, code);
                        throw new MultipartUploadFailedException(msg);
                    }

                    if (result.info == nint.Zero)
                    {
                        trace?.Fail("Native library returned a null upload info result without an error.");
                        throw new MultipartUploadFailedException("Native library returned a null upload info result without an error.");
                    }

                    trace?.Success();
                    return UplinkInterop.MarshalUploadInfo(result.info);
                }
                finally
                {
                    UplinkInterop.uplink_free_upload_info_result(result);
                }
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    public unsafe Task<CommitUploadResult> CommitUploadAsync(
        string bucketName, string objectKey, string uploadId,
        CommitUploadOptions commitUploadOptions)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            using var trace = _access.Trace("uplink_commit_upload", ("bucket", bucketName), ("key", objectKey), ("uploadId", uploadId));
            try
            {
                // Build native custom metadata
                UplinkInterop.UplinkCustomMetadataEntry[]? entries = null;
                GCHandle entriesPin = default;
                nint entriesPtr = nint.Zero;

                if (commitUploadOptions?.CustomMetadata?.Entries.Count > 0)
                {
                    entries = commitUploadOptions.CustomMetadata.Entries
                        .Select(kv => new UplinkInterop.UplinkCustomMetadataEntry
                        {
                            key          = Marshal.StringToCoTaskMemUTF8(kv.Key),
                            key_length   = (nuint)System.Text.Encoding.UTF8.GetByteCount(kv.Key),
                            value        = Marshal.StringToCoTaskMemUTF8(kv.Value),
                            value_length = (nuint)System.Text.Encoding.UTF8.GetByteCount(kv.Value)
                        })
                        .ToArray();

                    entriesPin = GCHandle.Alloc(entries, GCHandleType.Pinned);
                    entriesPtr = entriesPin.AddrOfPinnedObject();
                }

                var nativeMeta = new UplinkInterop.UplinkCustomMetadata
                {
                    entries = entriesPtr,
                    count   = entries != null ? (nuint)entries.Length : 0
                };
                var nativeOpts = new UplinkInterop.UplinkCommitUploadOptions
                {
                    custom_metadata = nativeMeta
                };

                UplinkInterop.UplinkCommitUploadResult result;
                try
                {
                    result = UplinkInterop.uplink_commit_upload(
                        projectLease.Handle, bucketName, objectKey, uploadId, &nativeOpts);
                }
                finally
                {
                    if (entriesPin.IsAllocated) entriesPin.Free();
                    if (entries != null)
                        foreach (var e in entries)
                        {
                            Marshal.FreeCoTaskMem(e.key);
                            Marshal.FreeCoTaskMem(e.value);
                        }
                }

                try
                {
                    var commitResult = new CommitUploadResult();
                    if (result.error != nint.Zero)
                    {
                        var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                        trace?.NativeError(msg, code);
                        commitResult.Error = msg;
                    }
                    else if (result.object_ != nint.Zero)
                    {
                        commitResult.Object = UplinkInterop.MarshalObject(result.object_);
                        trace?.Success();
                    }
                    else
                    {
                        trace?.Fail("Native library returned neither an error nor an object result.");
                        commitResult.Error = "Native library returned neither an error nor an object result.";
                    }
                    return commitResult;
                }
                finally
                {
                    UplinkInterop.uplink_free_commit_upload_result(result);
                }
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    public Task AbortUploadAsync(string bucketName, string objectKey, string uploadId)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            using var trace = _access.Trace("uplink_abort_upload", ("bucket", bucketName), ("key", objectKey), ("uploadId", uploadId));
            try
            {
                var errPtr = UplinkInterop.uplink_abort_upload(
                    projectLease.Handle, bucketName, objectKey, uploadId);
                if (errPtr != nint.Zero)
                {
                    var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                    trace?.NativeError(msg, code);
                    throw new AbortUploadFailedException(msg);
                }

                trace?.Success();
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    public unsafe Task<PartUploadResult> UploadPartAsync(
        string bucketName, string objectKey, string uploadId,
        uint partNumber, byte[] partBytes)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            using var trace = _access.Trace("uplink_upload_part", ("bucket", bucketName), ("key", objectKey), ("uploadId", uploadId), ("partNumber", partNumber));
            try
            {
                var partResult = UplinkInterop.uplink_upload_part(
                    projectLease.Handle, bucketName, objectKey, uploadId, partNumber);
                nint partHandle;
                try
                {
                    if (partResult.error != nint.Zero)
                    {
                        var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref partResult.error);
                        trace?.NativeError(msg, code);
                        throw new MultipartUploadFailedException(msg);
                    }

                    partHandle = partResult.part_upload;
                    if (partHandle == nint.Zero)
                    {
                        trace?.Fail("Native library returned a null multipart upload handle without an error.");
                        throw new MultipartUploadFailedException("Native library returned a null multipart upload handle without an error.");
                    }

                    partResult.part_upload = nint.Zero;
                }
                finally
                {
                    UplinkInterop.uplink_free_part_upload_result(partResult);
                }

                var uploadResult = new PartUploadResult();
                try
                {
                    var totalBytesWritten = 0;
                    while (totalBytesWritten < partBytes.Length)
                    {
                        var bytesRemaining = partBytes.Length - totalBytesWritten;
                        var bytesToWrite = Math.Min(PartWriteChunkSize, bytesRemaining);

                        var writeResult = UplinkInterop.WithPinnedBuffer(
                            partBytes, totalBytesWritten, bytesToWrite,
                            (ptr, len) => UplinkInterop.uplink_part_upload_write(partHandle, (void*)ptr, len));
                        try
                        {
                            if (writeResult.error != nint.Zero)
                            {
                                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref writeResult.error);
                                trace?.NativeError(msg, code);
                                uploadResult.Error = msg;
                                return uploadResult;
                            }

                            var bytesWritten = (int)(nuint)writeResult.bytes_written;
                            if (bytesWritten == 0)
                            {
                                trace?.Fail("Multipart upload part write stalled: 0 bytes written without error.");
                                uploadResult.Error = "Multipart upload part write stalled: 0 bytes written without error. This may indicate a connection issue or native upload buffer problem.";
                                return uploadResult;
                            }

                            totalBytesWritten += bytesWritten;
                        }
                        finally
                        {
                            UplinkInterop.uplink_free_write_result(writeResult);
                        }
                    }

                    uploadResult.BytesWritten = (uint)totalBytesWritten;

                    var commitErr = UplinkInterop.uplink_part_upload_commit(partHandle);
                    if (commitErr != nint.Zero)
                    {
                        var (msg, code) = UplinkInterop.ConsumeError(commitErr);
                        trace?.NativeError(msg, code);
                        uploadResult.Error = msg;
                    }
                    else
                    {
                        trace?.Success();
                    }

                    return uploadResult;
                }
                finally
                {
                    UplinkInterop.FreePartUploadHandle(partHandle);
                }
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    public Task UploadPartSetETagAsync(PartUpload partUpload, string eTag)
    {
        return Task.Run(() =>
        {
            using var trace = _access.Trace("uplink_part_upload_set_etag", ("partHandle", partUpload.Handle));
            var errPtr = UplinkInterop.uplink_part_upload_set_etag(partUpload.Handle, eTag);
            if (errPtr != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                trace?.NativeError(msg, code);
                throw new SetETagFailedException(msg);
            }

            trace?.Success();
        });
    }

    public Task<PartResult> GetPartUploadInfoAsync(PartUpload partUpload)
    {
        return Task.Run(() =>
        {
            using var trace = _access.Trace("uplink_part_upload_info", ("partHandle", partUpload.Handle));
            var result = UplinkInterop.uplink_part_upload_info(partUpload.Handle);
            try
            {
                if (result.error != nint.Zero)
                {
                    var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                    trace?.NativeError(msg, code);
                    throw new MultipartUploadFailedException(msg);
                }

                if (result.part == nint.Zero)
                {
                    trace?.Fail("Native library returned a null part info result without an error.");
                    throw new MultipartUploadFailedException("Native library returned a null part info result without an error.");
                }

                trace?.Success();
                return UplinkInterop.MarshalPart(result.part);
            }
            finally
            {
                UplinkInterop.uplink_free_part_result(result);
            }
        });
    }

    public unsafe Task<UploadsList> ListUploadsAsync(
        string bucketName, ListUploadOptions listUploadOptions)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            using var trace = _access.Trace("uplink_list_uploads", ("bucket", bucketName), ("prefix", listUploadOptions.Prefix ?? string.Empty));
            var prefix = Marshal.StringToCoTaskMemUTF8(listUploadOptions.Prefix ?? string.Empty);
            var cursor = Marshal.StringToCoTaskMemUTF8(listUploadOptions.Cursor ?? string.Empty);
            try
            {
                var nativeOpts = new UplinkInterop.UplinkListUploadsOptions
                {
                    prefix = prefix,
                    cursor = cursor
                };

                nint iterator = UplinkInterop.uplink_list_uploads(
                    projectLease.Handle, bucketName, &nativeOpts);

                var list = new UploadsList();
                try
                {
                    if (iterator == nint.Zero)
                    {
                        trace?.Fail("Native library returned a null upload iterator without an error.");
                        throw new MultipartUploadFailedException("Native library returned a null upload iterator without an error.");
                    }

                    while (UplinkInterop.uplink_upload_iterator_next(iterator))
                    {
                        nint infoPtr = UplinkInterop.uplink_upload_iterator_item(iterator);
                        try
                        {
                            list.Items.Add(UplinkInterop.MarshalUploadInfo(infoPtr));
                        }
                        finally
                        {
                            if (infoPtr != nint.Zero)
                                UplinkInterop.uplink_free_upload_info(infoPtr);
                        }
                    }

                    nint errPtr = UplinkInterop.uplink_upload_iterator_err(iterator);
                    if (errPtr != nint.Zero)
                    {
                        var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                        trace?.NativeError(msg, code);
                        throw new MultipartUploadFailedException(msg);
                    }

                    trace?.Success();
                }
                finally
                {
                    UplinkInterop.uplink_free_upload_iterator(iterator);
                }

                return list;
            }
            finally
            {
                Marshal.FreeCoTaskMem(prefix);
                Marshal.FreeCoTaskMem(cursor);
                projectLease.Dispose();
            }
        });
    }

    public unsafe Task<UploadPartsList> ListUploadPartsAsync(
        string bucketName, string objectKey, string uploadId,
        ListUploadPartsOptions listUploadPartOptions)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            using var trace = _access.Trace("uplink_list_upload_parts", ("bucket", bucketName), ("key", objectKey), ("uploadId", uploadId));
            var nativeOpts = new UplinkInterop.UplinkListUploadPartsOptions
            {
                cursor = ResolvePartCursor(listUploadPartOptions)
            };

            nint iterator = UplinkInterop.uplink_list_upload_parts(
                projectLease.Handle, bucketName, objectKey, uploadId, &nativeOpts);

            var list = new UploadPartsList();
            try
            {
                if (iterator == nint.Zero)
                {
                    trace?.Fail("Native library returned a null upload part iterator without an error.");
                    throw new MultipartUploadFailedException("Native library returned a null upload part iterator without an error.");
                }

                while (UplinkInterop.uplink_part_iterator_next(iterator))
                {
                    nint partPtr = UplinkInterop.uplink_part_iterator_item(iterator);
                    try
                    {
                        list.Items.Add(UplinkInterop.MarshalPart(partPtr));
                    }
                    finally
                    {
                        if (partPtr != nint.Zero)
                            UplinkInterop.uplink_free_part(partPtr);
                    }
                }

                nint errPtr = UplinkInterop.uplink_part_iterator_err(iterator);
                if (errPtr != nint.Zero)
                {
                    var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                    trace?.NativeError(msg, code);
                    throw new MultipartUploadFailedException(msg);
                }

                trace?.Success();
            }
            finally
            {
                UplinkInterop.uplink_free_part_iterator(iterator);
                projectLease.Dispose();
            }

            return list;
        });
    }

    private static uint ResolvePartCursor(ListUploadPartsOptions listUploadPartOptions)
    {
        if (listUploadPartOptions.CursorPartNumber != 0)
            return listUploadPartOptions.CursorPartNumber;

        return !string.IsNullOrWhiteSpace(listUploadPartOptions.Cursor) &&
               uint.TryParse(listUploadPartOptions.Cursor, out var parsedCursor)
            ? parsedCursor
            : 0;
    }
}
