using System.Runtime.InteropServices;

namespace uplink.NET.Native;

/// <summary>
/// P/Invoke declarations for the storj/uplink-c native library.
/// All handle types in C (uint64_t _handle) are represented as UplinkHandle.
/// Pointer fields in result/object structs are represented as nint.
/// </summary>
internal static unsafe partial class UplinkInterop
{
    private const string LibName = "storj_uplink";

    // ── Opaque handle (all C handle structs contain a single uint64_t) ──────
    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkHandle { public ulong _handle; }

    // ── Error ────────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkError
    {
        public nint message; // char*
        public uint code;
    }

    // ── Config ───────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkConfig
    {
        public nint user_agent;               // char*
        public int  dial_timeout_milliseconds;
        public nint temp_directory;           // char* – targets uplink-c >= 1.0 (see uplink.h UplinkConfig)
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
        public long  size;
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
        public byte  delimiter; // char
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
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkListUploadPartsOptions
    {
        public nint  cursor;             // char*
        public uint  cursor_part_number;
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
        public UplinkHandle project; // UplinkProject by value
        public nint         error;   // UplinkError*
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
        public UplinkHandle upload; // UplinkUpload by value
        public nint         error;  // UplinkError*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UplinkDownloadResult
    {
        public UplinkHandle download; // UplinkDownload by value
        public nint         error;    // UplinkError*
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
        public UplinkHandle part_upload; // UplinkPartUpload by value
        public nint         error;       // UplinkError*
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

    // ── Project ───────────────────────────────────────────────────────────────
    [LibraryImport(LibName)]
    internal static partial UplinkProjectResult uplink_config_open_project(UplinkConfig config, UplinkHandle access);

    [LibraryImport(LibName)]
    internal static partial UplinkProjectResult uplink_open_project(UplinkHandle access);

    [LibraryImport(LibName)]
    internal static partial nint uplink_close_project(UplinkHandle project); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial void uplink_free_project_result(UplinkProjectResult result);

    // ── Bucket ────────────────────────────────────────────────────────────────
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkBucketResult uplink_create_bucket(UplinkHandle project, string name);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkBucketResult uplink_ensure_bucket(UplinkHandle project, string name);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkBucketResult uplink_stat_bucket(UplinkHandle project, string name);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkBucketResult uplink_delete_bucket(UplinkHandle project, string name);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkBucketResult uplink_delete_bucket_with_objects(UplinkHandle project, string name);

    [LibraryImport(LibName)]
    internal static partial nint uplink_list_buckets(UplinkHandle project, UplinkListBucketsOptions* options); // returns UplinkBucketIterator*

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
    internal static partial UplinkUploadResult uplink_upload_object(UplinkHandle project, string bucket, string key, UplinkUploadOptions* options);

    [LibraryImport(LibName)]
    internal static partial UplinkWriteResult uplink_upload_write(UplinkHandle upload, void* bytes, nuint length);

    [LibraryImport(LibName)]
    internal static partial nint uplink_upload_commit(UplinkHandle upload); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial nint uplink_upload_abort(UplinkHandle upload); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial nint uplink_upload_set_custom_metadata(UplinkHandle upload, UplinkCustomMetadata metadata); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial UplinkObjectResult uplink_upload_info(UplinkHandle upload);

    [LibraryImport(LibName)]
    internal static partial void uplink_free_upload_result(UplinkUploadResult result);

    // ── Download ──────────────────────────────────────────────────────────────
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkDownloadResult uplink_download_object(UplinkHandle project, string bucket, string key, UplinkDownloadOptions* options);

    [LibraryImport(LibName)]
    internal static partial UplinkReadResult uplink_download_read(UplinkHandle download, void* bytes, nuint length);

    [LibraryImport(LibName)]
    internal static partial nint uplink_close_download(UplinkHandle download); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial UplinkObjectResult uplink_download_info(UplinkHandle download);

    [LibraryImport(LibName)]
    internal static partial void uplink_free_download_result(UplinkDownloadResult result);

    // ── Object ────────────────────────────────────────────────────────────────
    [LibraryImport(LibName)]
    internal static partial nint uplink_list_objects(UplinkHandle project, nint bucket, UplinkListObjectsOptions* options); // returns UplinkObjectIterator*

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint uplink_list_objects_utf8(UplinkHandle project, string bucket, UplinkListObjectsOptions* options); // helper overload

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
    internal static partial UplinkObjectResult uplink_stat_object(UplinkHandle project, string bucket, string key);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkObjectResult uplink_delete_object(UplinkHandle project, string bucket, string key);

    [LibraryImport(LibName)]
    internal static partial void uplink_free_object_result(UplinkObjectResult result);

    // ── Multipart upload ──────────────────────────────────────────────────────
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkUploadInfoResult uplink_begin_upload(UplinkHandle project, string bucket, string key, UplinkUploadOptions* options);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkCommitUploadResult uplink_commit_upload(UplinkHandle project, string bucket, string key, string upload_id, UplinkCommitUploadOptions* options);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint uplink_abort_upload(UplinkHandle project, string bucket, string key, string upload_id); // returns UplinkError*

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial UplinkPartUploadResult uplink_upload_part(UplinkHandle project, string bucket, string key, string upload_id, uint part_number);

    [LibraryImport(LibName)]
    internal static partial UplinkWriteResult uplink_part_upload_write(UplinkHandle part_upload, void* bytes, nuint length);

    [LibraryImport(LibName)]
    internal static partial nint uplink_part_upload_commit(UplinkHandle part_upload); // returns UplinkError*

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint uplink_part_upload_set_etag(UplinkHandle part_upload, string etag); // returns UplinkError*

    [LibraryImport(LibName)]
    internal static partial UplinkPartResult uplink_part_upload_info(UplinkHandle part_upload);

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
    internal static partial nint uplink_list_uploads(UplinkHandle project, string bucket, UplinkListUploadsOptions* options); // returns UplinkUploadIterator*

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
    internal static partial nint uplink_list_upload_parts(UplinkHandle project, string bucket, string key, string upload_id, UplinkListUploadPartsOptions* options); // returns UplinkPartIterator*

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

    // ── Helpers ───────────────────────────────────────────────────────────────
    /// <summary>Reads error message and code from a native UplinkError*, then frees it.</summary>
    internal static (string message, uint code) ConsumeError(nint errorPtr)
    {
        if (errorPtr == nint.Zero)
            return (string.Empty, 0);

        var err = *(UplinkError*)errorPtr;
        string msg = err.message != nint.Zero
            ? Marshal.PtrToStringUTF8(err.message) ?? string.Empty
            : string.Empty;
        uint code = err.code;
        uplink_free_error(errorPtr);
        return (msg, code);
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

    /// <summary>Marshals a native UplinkBucket* to a managed Bucket model.</summary>
    internal static Models.Bucket MarshalBucket(nint bucketPtr)
    {
        if (bucketPtr == nint.Zero)
            return new Models.Bucket();
        var b = *(UplinkBucket*)bucketPtr;
        return new Models.Bucket
        {
            Name    = PtrToString(b.name),
            Created = UnixToDateTime(b.created)
        };
    }

    /// <summary>Marshals a native UplinkObject* to a managed StorjObject model.</summary>
    internal static Models.StorjObject MarshalObject(nint objectPtr)
    {
        if (objectPtr == nint.Zero)
            return new Models.StorjObject();
        var o = *(UplinkObject*)objectPtr;
        var obj = new Models.StorjObject
        {
            Key           = PtrToString(o.key),
            IsPrefix      = o.is_prefix,
            Created       = UnixToDateTime(o.system.created),
            Expires       = UnixToDateTime(o.system.expires),
            ContentLength = o.system.content_length
        };
        if (o.custom.count > 0 && o.custom.entries != nint.Zero)
            obj.CustomMetadata = MarshalCustomMetadata(o.custom);
        return obj;
    }

    /// <summary>Marshals a native UplinkCustomMetadata to a managed CustomMetadata model.</summary>
    internal static Models.CustomMetadata MarshalCustomMetadata(UplinkCustomMetadata native)
    {
        var cm = new Models.CustomMetadata();
        if (native.entries == nint.Zero || native.count == 0)
            return cm;
        var count = (int)(uint)native.count;
        var entryPtr = (UplinkCustomMetadataEntry*)native.entries;
        for (int i = 0; i < count; i++)
        {
            var e = entryPtr[i];
            string key   = PtrToString(e.key);
            string value = PtrToString(e.value);
            if (!string.IsNullOrEmpty(key))
                cm.Entries[key] = value;
        }
        return cm;
    }

    /// <summary>Marshals a native UplinkUploadInfo* to a managed UploadInfo model.</summary>
    internal static Models.UploadInfo MarshalUploadInfo(nint infoPtr)
    {
        if (infoPtr == nint.Zero)
            return new Models.UploadInfo();
        var i = *(UplinkUploadInfo*)infoPtr;
        return new Models.UploadInfo
        {
            UploadId = PtrToString(i.upload_id),
            Key      = PtrToString(i.key)
        };
    }

    /// <summary>Marshals a native UplinkPart* to a managed PartResult model.</summary>
    internal static Models.PartResult MarshalPart(nint partPtr)
    {
        if (partPtr == nint.Zero)
            return new Models.PartResult();
        var p = *(UplinkPart*)partPtr;
        return new Models.PartResult
        {
            PartNumber = p.part_number,
            Size       = p.size,
            Modified   = UnixToDateTime(p.modified),
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
