using System.Runtime.InteropServices;

namespace uplink.NET.Worker.Native;

/// <summary>
/// P/Invoke declarations for the storj/uplink-c native library.
/// Opaque native handles are represented as nint pointers to uplink-c wrapper structs.
/// Pointer fields in result/object structs are represented as nint.
/// </summary>
internal static unsafe partial class UplinkInterop
{
    private const string LibName = "storj_uplink";
    internal const int EndOfFileErrorCode = -1;

    // ── Error ────────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkError
    {
        // uplink-c defines UplinkError as { int32_t code; char* message; }.
        public int  code;
        public nint message; // char*
    }

    // ── Config ───────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkConfig
    {
        public nint user_agent;               // char*
        public int  dial_timeout_milliseconds;
        public nint temp_directory;           // char* – targets uplink-c >= 1.0 (see uplink.h UplinkConfig)
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkPermission
    {
        public byte allow_download;
        public byte allow_upload;
        public byte allow_list;
        public byte allow_delete;
        public long not_before; // int64_t unix timestamp, 0 = disabled
        public long not_after;  // int64_t unix timestamp, 0 = disabled
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkSharePrefix
    {
        public nint bucket; // char*
        public nint prefix; // char*
    }

    // ── Bucket ───────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkBucket
    {
        public nint name;    // char*
        public long created; // int64_t
    }

    // ── System metadata ──────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkSystemMetadata
    {
        public long created;        // int64_t
        public long expires;        // int64_t
        public long content_length; // int64_t
    }

    // ── Custom metadata ──────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkCustomMetadataEntry
    {
        public nint  key;          // char*
        public nuint key_length;   // size_t
        public nint  value;        // char*
        public nuint value_length; // size_t
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkCustomMetadata
    {
        public nint  entries; // UplinkCustomMetadataEntry*
        public nuint count;   // size_t
    }

    // ── Object ───────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkObject
    {
        public nint                  key;       // char*
        [MarshalAs(UnmanagedType.I1)]
        public bool                  is_prefix;
        public UplinkSystemMetadata  system;
        public UplinkCustomMetadata  custom;
    }

    // ── Upload info (multipart) ───────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkUploadInfo
    {
        public nint  upload_id; // char*
        public nint  key;       // char*
        [MarshalAs(UnmanagedType.I1)]
        public bool  is_prefix;
        public UplinkSystemMetadata system;
        public UplinkCustomMetadata custom;
    }

    // ── Part ─────────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkPart
    {
        public uint  part_number;
        public nuint size;
        public long  modified;
        public nint  etag;        // char*
        public nuint etag_length; // size_t
    }

    // ── Options ──────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkUploadOptions
    {
        public long expires; // int64_t unix timestamp, 0 = no expiry
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkDownloadOptions
    {
        public long offset; // int64_t
        public long length; // int64_t, -1 = all
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkListBucketsOptions
    {
        public nint cursor; // char*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkListObjectsOptions
    {
        public nint  prefix;    // char*
        public nint  cursor;    // char*
        [MarshalAs(UnmanagedType.I1)] public bool recursive;
        [MarshalAs(UnmanagedType.I1)] public bool system;
        [MarshalAs(UnmanagedType.I1)] public bool custom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkCommitUploadOptions
    {
        public UplinkCustomMetadata custom_metadata;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkListUploadsOptions
    {
        public nint prefix; // char*
        public nint cursor; // char*
        [MarshalAs(UnmanagedType.I1)] public bool recursive;
        [MarshalAs(UnmanagedType.I1)] public bool system;
        [MarshalAs(UnmanagedType.I1)] public bool custom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkListUploadPartsOptions
    {
        public uint cursor;
    }

    // ── Result types ─────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkAccessResult
    {
        public nint access; // UplinkAccess* (pointer to handle struct)
        public nint error;  // UplinkError*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkProjectResult
    {
        public nint project; // UplinkProject*
        public nint error;   // UplinkError*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkBucketResult
    {
        public nint bucket; // UplinkBucket*
        public nint error;  // UplinkError*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkUploadResult
    {
        public nint upload; // UplinkUpload*
        public nint error;  // UplinkError*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkDownloadResult
    {
        public nint download; // UplinkDownload*
        public nint error;    // UplinkError*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkWriteResult
    {
        public nuint bytes_written; // size_t
        public nint  error;         // UplinkError*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkReadResult
    {
        public nuint bytes_read; // size_t
        public nint  error;      // UplinkError*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkStringResult
    {
        public nint stringValue; // char*
        public nint error; // UplinkError*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkObjectResult
    {
        public nint object_; // UplinkObject* (field named 'object' in C, reserved word in C#)
        public nint error;   // UplinkError*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkUploadInfoResult
    {
        public nint info;  // UplinkUploadInfo*
        public nint error; // UplinkError*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkCommitUploadResult
    {
        public nint object_; // UplinkObject*
        public nint error;   // UplinkError*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkPartUploadResult
    {
        public nint part_upload; // UplinkPartUpload*
        public nint error;       // UplinkError*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkPartResult
    {
        public nint part;  // UplinkPart*
        public nint error; // UplinkError*
    }

    // =========================================================================
    // P/Invoke declarations
    // =========================================================================

    // ── Access ────────────────────────────────────────────────────────────────
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkAccessResult uplink_parse_access(string serialized);

    [LibraryImport(LibName)]
    internal static partial void uplink_free_access_result(UplinkAccessResult result);

    [LibraryImport(LibName)]
    internal static partial UplinkStringResult uplink_access_serialize(nint access);

    [LibraryImport(LibName)]
    internal static partial UplinkAccessResult uplink_access_share(
        nint access,
        UplinkPermission permission,
        UplinkSharePrefix* prefixes,
        nint prefixes_count);

    [LibraryImport(LibName)]
    internal static partial void uplink_free_string_result(UplinkStringResult result);

    // ── Project ───────────────────────────────────────────────────────────────
    [LibraryImport(LibName)]
    internal static partial UplinkProjectResult uplink_config_open_project(UplinkConfig config, nint access);

    [LibraryImport(LibName)]
    internal static partial UplinkProjectResult uplink_open_project(nint access);

    [LibraryImport(LibName)]
    internal static partial nint uplink_close_project(nint project); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial nint uplink_revoke_access(nint project, nint access); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial void uplink_free_project_result(UplinkProjectResult result);

    // ── Bucket ────────────────────────────────────────────────────────────────
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkBucketResult uplink_create_bucket(nint project, string name);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkBucketResult uplink_ensure_bucket(nint project, string name);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkBucketResult uplink_stat_bucket(nint project, string name);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkBucketResult uplink_delete_bucket(nint project, string name);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkBucketResult uplink_delete_bucket_with_objects(nint project, string name);

    [LibraryImport(LibName)]
    internal static partial nint uplink_list_buckets(nint project, UplinkListBucketsOptions* options); // returns UplinkBucketIterator*

    [LibraryImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool uplink_bucket_iterator_next(nint iterator);

    [LibraryImport(LibName)]
    internal static partial nint uplink_bucket_iterator_item(nint iterator); // returns UplinkBucket*

    [LibraryImport(LibName)]
    internal static partial nint uplink_bucket_iterator_err(nint iterator); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial void uplink_free_bucket_iterator(nint iterator);

    [LibraryImport(LibName)]
    internal static partial void uplink_free_bucket_result(UplinkBucketResult result);

    // ── Upload ────────────────────────────────────────────────────────────────
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkUploadResult uplink_upload_object(nint project, string bucket, string key, UplinkUploadOptions* options);

    [LibraryImport(LibName)]
    internal static partial UplinkWriteResult uplink_upload_write(nint upload, void* bytes, nuint length);

    [LibraryImport(LibName)]
    internal static partial nint uplink_upload_commit(nint upload); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial nint uplink_upload_abort(nint upload); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial nint uplink_upload_set_custom_metadata(nint upload, UplinkCustomMetadata metadata); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial UplinkObjectResult uplink_upload_info(nint upload);

    [LibraryImport(LibName)]
    internal static partial void uplink_free_upload_result(UplinkUploadResult result);

    // ── Download ──────────────────────────────────────────────────────────────
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkDownloadResult uplink_download_object(nint project, string bucket, string key, UplinkDownloadOptions* options);

    [LibraryImport(LibName)]
    internal static partial UplinkReadResult uplink_download_read(nint download, void* bytes, nuint length);

    [LibraryImport(LibName)]
    internal static partial nint uplink_close_download(nint download); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial UplinkObjectResult uplink_download_info(nint download);

    [LibraryImport(LibName)]
    internal static partial void uplink_free_download_result(UplinkDownloadResult result);

    // ── Object ────────────────────────────────────────────────────────────────
    [LibraryImport(LibName)]
    internal static partial nint uplink_list_objects(nint project, nint bucket, UplinkListObjectsOptions* options); // returns UplinkObjectIterator*

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint uplink_list_objects_utf8(nint project, string bucket, UplinkListObjectsOptions* options); // helper overload

    [LibraryImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool uplink_object_iterator_next(nint iterator);

    [LibraryImport(LibName)]
    internal static partial nint uplink_object_iterator_item(nint iterator); // returns UplinkObject*

    [LibraryImport(LibName)]
    internal static partial nint uplink_object_iterator_err(nint iterator); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial void uplink_free_object_iterator(nint iterator);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkObjectResult uplink_stat_object(nint project, string bucket, string key);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkObjectResult uplink_delete_object(nint project, string bucket, string key);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkObjectResult uplink_copy_object(
        nint project,
        string old_bucket_name,
        string old_object_key,
        string new_bucket_name,
        string new_object_key,
        nint options);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint uplink_move_object(
        nint project,
        string old_bucket_name,
        string old_object_key,
        string new_bucket_name,
        string new_object_key,
        nint options);

    [LibraryImport(LibName)]
    internal static partial void uplink_free_object_result(UplinkObjectResult result);

    // ── Multipart upload ──────────────────────────────────────────────────────
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkUploadInfoResult uplink_begin_upload(nint project, string bucket, string key, UplinkUploadOptions* options);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkCommitUploadResult uplink_commit_upload(nint project, string bucket, string key, string upload_id, UplinkCommitUploadOptions* options);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint uplink_abort_upload(nint project, string bucket, string key, string upload_id); // returns UplinkError*

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkPartUploadResult uplink_upload_part(nint project, string bucket, string key, string upload_id, uint part_number);

    [LibraryImport(LibName)]
    internal static partial UplinkWriteResult uplink_part_upload_write(nint part_upload, void* bytes, nuint length);

    [LibraryImport(LibName)]
    internal static partial nint uplink_part_upload_commit(nint part_upload); // returns UplinkError*

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint uplink_part_upload_set_etag(nint part_upload, string etag); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial UplinkPartResult uplink_part_upload_info(nint part_upload);

    [LibraryImport(LibName)]
    internal static partial void uplink_free_upload_info_result(UplinkUploadInfoResult result);

    [LibraryImport(LibName)]
    internal static partial void uplink_free_commit_upload_result(UplinkCommitUploadResult result);

    [LibraryImport(LibName)]
    internal static partial void uplink_free_part_upload_result(UplinkPartUploadResult result);

    [LibraryImport(LibName)]
    internal static partial void uplink_free_part_result(UplinkPartResult result);

    // ── Upload iterator ──────────────────────────────────────────────────────
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint uplink_list_uploads(nint project, string bucket, UplinkListUploadsOptions* options); // returns UplinkUploadIterator*

    [LibraryImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool uplink_upload_iterator_next(nint iterator);

    [LibraryImport(LibName)]
    internal static partial nint uplink_upload_iterator_item(nint iterator); // returns UplinkUploadInfo*

    [LibraryImport(LibName)]
    internal static partial nint uplink_upload_iterator_err(nint iterator); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial void uplink_free_upload_iterator(nint iterator);

    // ── Part iterator ────────────────────────────────────────────────────────
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint uplink_list_upload_parts(nint project, string bucket, string key, string upload_id, UplinkListUploadPartsOptions* options); // returns UplinkPartIterator*

    [LibraryImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool uplink_part_iterator_next(nint iterator);

    [LibraryImport(LibName)]
    internal static partial nint uplink_part_iterator_item(nint iterator); // returns UplinkPart*

    [LibraryImport(LibName)]
    internal static partial nint uplink_part_iterator_err(nint iterator); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial void uplink_free_part_iterator(nint iterator);

    // ── Error ─────────────────────────────────────────────────────────────────
    [LibraryImport(LibName)]
    internal static partial void uplink_free_error(nint error); // UplinkError*

    [LibraryImport(LibName)]
    internal static partial void uplink_free_write_result(UplinkWriteResult result);

    [LibraryImport(LibName)]
    internal static partial void uplink_free_read_result(UplinkReadResult result);

    // ── Helpers ───────────────────────────────────────────────────────────────
    /// <summary>Reads error message and code from a native UplinkError*, then frees it.</summary>
    internal static (string message, int code) ConsumeError(nint errorPtr)
    {
        if (errorPtr == nint.Zero)
            return (string.Empty, 0);

        var err = *(UplinkError*)errorPtr;
        string msg = err.message != nint.Zero
            ? Marshal.PtrToStringUTF8(err.message) ?? string.Empty
            : string.Empty;
        int code = err.code;
        uplink_free_error(errorPtr);
        return (msg, code);
    }

    internal static (string message, int code) ConsumeErrorAndClear(ref nint errorPtr)
    {
        var result = ConsumeError(errorPtr);
        errorPtr = nint.Zero;
        return result;
    }

    internal static void FreeProjectHandle(nint project)
    {
        if (project != nint.Zero)
        {
            // Close the project first: this signals the Go runtime to shut down the project's
            // goroutines and close network connections.  Without this step the goroutines keep
            // running against a freed handle and can panic, killing the worker process.
            var errPtr = uplink_close_project(project);
            if (errPtr != nint.Zero)
                uplink_free_error(errPtr); // ignore close errors; just release the error memory
            uplink_free_project_result(new UplinkProjectResult { project = project, error = nint.Zero });
        }
    }

    internal static void FreeAccessHandle(nint access)
    {
        if (access != nint.Zero)
            uplink_free_access_result(new UplinkAccessResult { access = access, error = nint.Zero });
    }

    internal static void FreeUploadHandle(nint upload)
    {
        if (upload != nint.Zero)
            uplink_free_upload_result(new UplinkUploadResult { upload = upload, error = nint.Zero });
    }

    internal static void FreeDownloadHandle(nint download)
    {
        if (download != nint.Zero)
            uplink_free_download_result(new UplinkDownloadResult { download = download, error = nint.Zero });
    }

    internal static void FreePartUploadHandle(nint partUpload)
    {
        if (partUpload != nint.Zero)
            uplink_free_part_upload_result(new UplinkPartUploadResult { part_upload = partUpload, error = nint.Zero });
    }

    /// <summary>Reads a UTF-8 string from a native char*.</summary>
    internal static string PtrToString(nint ptr) =>
        ptr == nint.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;

    /// <summary>Converts a Unix epoch (int64) to DateTime UTC.</summary>
    internal static DateTime UnixToDateTime(long unixSeconds) =>
        unixSeconds == 0
            ? DateTime.MinValue
            : DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;

    /// <summary>Converts a nullable DateTime to a Unix timestamp (0 = no expiry).</summary>
    internal static long DateTimeToUnix(DateTime? dt) =>
        dt.HasValue ? new DateTimeOffset(dt.Value.ToUniversalTime()).ToUnixTimeSeconds() : 0;

    /// <summary>Marshals a native UplinkBucket* to a (name, created) tuple.</summary>
    internal static (string name, long created) MarshalBucket(nint bucketPtr)
    {
        if (bucketPtr == nint.Zero)
            return (string.Empty, 0);
        var b = *(UplinkBucket*)bucketPtr;
        return (PtrToString(b.name), b.created);
    }

    /// <summary>Marshals a native UplinkObject* to a worker-local object descriptor.</summary>
    internal static WorkerObject MarshalObject(nint objectPtr)
    {
        if (objectPtr == nint.Zero)
            return new WorkerObject();
        var o = *(UplinkObject*)objectPtr;
        var obj = new WorkerObject
        {
            Key           = PtrToString(o.key),
            IsPrefix      = o.is_prefix,
            Created       = o.system.created,
            Expires       = o.system.expires,
            ContentLength = o.system.content_length
        };
        if (o.custom.count > 0 && o.custom.entries != nint.Zero)
        {
            var count = (int)(uint)o.custom.count;
            var ep = (UplinkCustomMetadataEntry*)o.custom.entries;
            for (int i = 0; i < count; i++)
            {
                var k = PtrToString(ep[i].key);
                var v = PtrToString(ep[i].value);
                if (!string.IsNullOrEmpty(k))
                    obj.CustomMetadata[k] = v;
            }
        }
        return obj;
    }

    /// <summary>Marshals a native UplinkUploadInfo* to a (uploadId, key) tuple.</summary>
    internal static (string uploadId, string key) MarshalUploadInfo(nint infoPtr)
    {
        if (infoPtr == nint.Zero)
            return (string.Empty, string.Empty);
        var i = *(UplinkUploadInfo*)infoPtr;
        return (PtrToString(i.upload_id), PtrToString(i.key));
    }

    /// <summary>Marshals a native UplinkPart* to a worker-local part descriptor.</summary>
    internal static WorkerPart MarshalPart(nint partPtr)
    {
        if (partPtr == nint.Zero)
            return new WorkerPart();
        var p = *(UplinkPart*)partPtr;
        return new WorkerPart
        {
            PartNumber = p.part_number,
            Size       = checked((long)p.size),
            Modified   = p.modified,
            ETag       = PtrToString(p.etag)
        };
    }

    /// <summary>
    /// Pins a managed byte array and invokes an action with the pinned pointer.
    /// </summary>
    internal static T WithPinnedBuffer<T>(byte[] buffer, int offset, int count, Func<nint, nuint, T> action)
    {
        if (buffer == null || count == 0)
            return action(nint.Zero, 0);
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            nint ptr = handle.AddrOfPinnedObject() + offset;
            return action(ptr, (nuint)count);
        }
        finally
        {
            handle.Free();
        }
    }
}
