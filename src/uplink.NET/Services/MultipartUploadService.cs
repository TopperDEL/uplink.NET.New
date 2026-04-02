using System.Runtime.InteropServices;
using uplink.NET.Exceptions;
using uplink.NET.Interfaces;
using uplink.NET.Models;
using uplink.NET.Native;

namespace uplink.NET.Services;

public class MultipartUploadService : IMultipartUploadService
{
    private readonly Access _access;

    public MultipartUploadService(Access access)
    {
        _access = access ?? throw new ArgumentNullException(nameof(access));
    }

    public unsafe Task<UploadInfo> BeginUploadAsync(
        string bucketName, string objectKey, UploadOptions uploadOptions)
    {
        return Task.Run(() =>
        {
            var opts = new UplinkInterop.UplinkUploadOptions
            {
                expires = UplinkInterop.DateTimeToUnix(uploadOptions?.Expires)
            };

            var result = UplinkInterop.uplink_begin_upload(
                _access._projectHandle, bucketName, objectKey, &opts);
            try
            {
                if (result.error != nint.Zero)
                {
                    var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                    throw new MultipartUploadFailedException(msg);
                }
                return UplinkInterop.MarshalUploadInfo(result.info);
            }
            finally
            {
                UplinkInterop.uplink_free_upload_info_result(result);
            }
        });
    }

    public unsafe Task<CommitUploadResult> CommitUploadAsync(
        string bucketName, string objectKey, string uploadId,
        CommitUploadOptions commitUploadOptions)
    {
        return Task.Run(() =>
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
                    _access._projectHandle, bucketName, objectKey, uploadId, &nativeOpts);
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
                    var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                    commitResult.Error = msg;
                }
                else if (result.object_ != nint.Zero)
                {
                    commitResult.Object = UplinkInterop.MarshalObject(result.object_);
                }
                return commitResult;
            }
            finally
            {
                UplinkInterop.uplink_free_commit_upload_result(result);
            }
        });
    }

    public Task AbortUploadAsync(string bucketName, string objectKey, string uploadId)
    {
        return Task.Run(() =>
        {
            var errPtr = UplinkInterop.uplink_abort_upload(
                _access._projectHandle, bucketName, objectKey, uploadId);
            if (errPtr != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeError(errPtr);
                throw new AbortUploadFailedException(msg);
            }
        });
    }

    public unsafe Task<PartUploadResult> UploadPartAsync(
        string bucketName, string objectKey, string uploadId,
        uint partNumber, byte[] partBytes)
    {
        return Task.Run(() =>
        {
            var partResult = UplinkInterop.uplink_upload_part(
                _access._projectHandle, bucketName, objectKey, uploadId, partNumber);

            if (partResult.error != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref partResult.error);
                UplinkInterop.uplink_free_part_upload_result(partResult);
                throw new MultipartUploadFailedException(msg);
            }

            var partHandle = partResult.part_upload;
            var uploadResult = new PartUploadResult();
            try
            {
                var writeResult = UplinkInterop.WithPinnedBuffer(
                    partBytes, 0, partBytes.Length,
                    (ptr, len) => UplinkInterop.uplink_part_upload_write(partHandle, (void*)ptr, len));

                if (writeResult.error != nint.Zero)
                {
                    var (msg, _) = UplinkInterop.ConsumeError(writeResult.error);
                    uploadResult.Error = msg;
                    return uploadResult;
                }

                uploadResult.BytesWritten = (uint)(nuint)writeResult.bytes_written;

                var commitErr = UplinkInterop.uplink_part_upload_commit(partHandle);
                if (commitErr != nint.Zero)
                {
                    var (msg, _) = UplinkInterop.ConsumeError(commitErr);
                    uploadResult.Error = msg;
                }

                return uploadResult;
            }
            finally
            {
                UplinkInterop.FreePartUploadHandle(partHandle);
            }
        });
    }

    public Task UploadPartSetETagAsync(PartUpload partUpload, string eTag)
    {
        return Task.Run(() =>
        {
            var errPtr = UplinkInterop.uplink_part_upload_set_etag(partUpload.Handle, eTag);
            if (errPtr != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeError(errPtr);
                throw new SetETagFailedException(msg);
            }
        });
    }

    public Task<PartResult> GetPartUploadInfoAsync(PartUpload partUpload)
    {
        return Task.Run(() =>
        {
            var result = UplinkInterop.uplink_part_upload_info(partUpload.Handle);
            try
            {
                if (result.error != nint.Zero)
                {
                    var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                    throw new MultipartUploadFailedException(msg);
                }
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
        return Task.Run(() =>
        {
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
                    _access._projectHandle, bucketName, &nativeOpts);

                var list = new UploadsList();
                try
                {
                    while (UplinkInterop.uplink_upload_iterator_next(iterator))
                    {
                        nint infoPtr = UplinkInterop.uplink_upload_iterator_item(iterator);
                        list.Items.Add(UplinkInterop.MarshalUploadInfo(infoPtr));
                    }

                    nint errPtr = UplinkInterop.uplink_upload_iterator_err(iterator);
                    if (errPtr != nint.Zero)
                    {
                        var (msg, _) = UplinkInterop.ConsumeError(errPtr);
                        throw new MultipartUploadFailedException(msg);
                    }
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
            }
        });
    }

    public unsafe Task<UploadPartsList> ListUploadPartsAsync(
        string bucketName, string objectKey, string uploadId,
        ListUploadPartsOptions listUploadPartOptions)
    {
        return Task.Run(() =>
        {
            var nativeOpts = new UplinkInterop.UplinkListUploadPartsOptions
            {
                cursor = ResolvePartCursor(listUploadPartOptions)
            };

            nint iterator = UplinkInterop.uplink_list_upload_parts(
                _access._projectHandle, bucketName, objectKey, uploadId, &nativeOpts);

            var list = new UploadPartsList();
            try
            {
                while (UplinkInterop.uplink_part_iterator_next(iterator))
                {
                    nint partPtr = UplinkInterop.uplink_part_iterator_item(iterator);
                    list.Items.Add(UplinkInterop.MarshalPart(partPtr));
                }

                nint errPtr = UplinkInterop.uplink_part_iterator_err(iterator);
                if (errPtr != nint.Zero)
                {
                    var (msg, _) = UplinkInterop.ConsumeError(errPtr);
                    throw new MultipartUploadFailedException(msg);
                }
            }
            finally
            {
                UplinkInterop.uplink_free_part_iterator(iterator);
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
