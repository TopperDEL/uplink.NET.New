using System.Runtime.InteropServices;
using System.Text.Json;
using uplink.NET.Worker.Native;

namespace uplink.NET.Worker;

/// <summary>
/// Dispatches IPC requests to the native uplink-c library and returns result dictionaries.
/// </summary>
internal sealed class OperationDispatcher
{
    private readonly HandleRegistry _accessHandles  = new();
    private readonly HandleRegistry _projectHandles = new();
    private readonly HandleRegistry _uploadHandles  = new();
    private readonly HandleRegistry _downloadHandles = new();

    internal Dictionary<string, object?> Dispatch(JsonElement req, string op)
    {
        try
        {
            return op switch
            {
                "parse_access"           => ParseAccess(req),
                "access_serialize"       => AccessSerialize(req),
                "access_share"           => AccessShare(req),
                "access_revoke"          => AccessRevoke(req),
                "access_free"            => AccessFree(req),
                "bucket_create"          => BucketOp(req, "create"),
                "bucket_ensure"          => BucketOp(req, "ensure"),
                "bucket_stat"            => BucketOp(req, "stat"),
                "bucket_delete"          => BucketOp(req, "delete"),
                "bucket_delete_with_objects" => BucketDeleteWithObjects(req),
                "bucket_list"            => BucketList(req),
                "object_stat"            => ObjectStat(req),
                "object_delete"          => ObjectDelete(req),
                "object_list"            => ObjectList(req),
                "object_copy"            => ObjectCopy(req),
                "object_move"            => ObjectMove(req),
                "upload_begin"           => UploadBegin(req),
                "upload_write"           => UploadWrite(req),
                "upload_set_metadata"    => UploadSetMetadata(req),
                "upload_commit"          => UploadCommit(req),
                "upload_abort"           => UploadAbort(req),
                "download_begin"         => DownloadBegin(req),
                "download_read"          => DownloadRead(req),
                "download_close"         => DownloadClose(req),
                "multipart_begin"        => MultipartBegin(req),
                "multipart_commit"       => MultipartCommit(req),
                "multipart_abort"        => MultipartAbort(req),
                "multipart_upload_part"  => MultipartUploadPart(req),
                "multipart_list"         => MultipartList(req),
                "multipart_list_parts"   => MultipartListParts(req),
                _ => Error($"Unknown operation: {op}", -1)
            };
        }
        catch (Exception ex)
        {
            return Error(ex.Message, -1);
        }
    }

    // ── Access ────────────────────────────────────────────────────────────────

    private unsafe Dictionary<string, object?> ParseAccess(JsonElement req)
    {
        var serialized   = GetStr(req, "serialized");
        var userAgent    = GetStr(req, "user_agent");
        var dialTimeout  = GetInt(req, "dial_timeout_ms");
        var tempDir      = GetStr(req, "temp_dir");

        var accessResult = UplinkInterop.uplink_parse_access(serialized);
        try
        {
            if (accessResult.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref accessResult.error);
                return Error(msg, code);
            }

            var accessHandle = accessResult.access;
            accessResult.access = nint.Zero;

            var cfg = BuildConfig(userAgent, dialTimeout, tempDir);
            UplinkInterop.UplinkProjectResult projectResult;
            try
            {
                projectResult = UplinkInterop.uplink_config_open_project(cfg, accessHandle);
            }
            finally
            {
                FreeConfig(cfg);
            }

            if (projectResult.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref projectResult.error);
                UplinkInterop.uplink_free_project_result(projectResult);
                UplinkInterop.FreeAccessHandle(accessHandle);
                return Error(msg, code);
            }

            var projectHandle = projectResult.project;
            projectResult.project = nint.Zero;
            UplinkInterop.uplink_free_project_result(projectResult);

            long accessId  = _accessHandles.Register(accessHandle);
            long projectId = _projectHandles.Register(projectHandle);
            return Ok(new() { ["access_id"] = accessId, ["project_id"] = projectId });
        }
        finally
        {
            UplinkInterop.uplink_free_access_result(accessResult);
        }
    }

    private Dictionary<string, object?> AccessSerialize(JsonElement req)
    {
        long accessId = GetLong(req, "access_id");
        var accessHandle = _accessHandles.Get(accessId);

        var result = UplinkInterop.uplink_access_serialize(accessHandle);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                return Error(msg, code);
            }

            var serialized = UplinkInterop.PtrToString(result.stringValue);
            return Ok(new() { ["serialized"] = serialized });
        }
        finally
        {
            UplinkInterop.uplink_free_string_result(result);
        }
    }

    private unsafe Dictionary<string, object?> AccessShare(JsonElement req)
    {
        long accessId    = GetLong(req, "access_id");
        var userAgent    = GetStr(req, "user_agent");
        var dialTimeout  = GetInt(req, "dial_timeout_ms");
        var tempDir      = GetStr(req, "temp_dir");

        var permission = new UplinkInterop.UplinkPermission
        {
            allow_download = (byte)(GetBool(req, "allow_download") ? 1 : 0),
            allow_upload   = (byte)(GetBool(req, "allow_upload")   ? 1 : 0),
            allow_list     = (byte)(GetBool(req, "allow_list")     ? 1 : 0),
            allow_delete   = (byte)(GetBool(req, "allow_delete")   ? 1 : 0),
            not_before     = GetLong(req, "not_before"),
            not_after      = GetLong(req, "not_after")
        };

        var prefixesJson = req.TryGetProperty("prefixes", out var pElem) ? pElem : default;
        var nativePrefixes = Array.Empty<UplinkInterop.UplinkSharePrefix>();

        if (prefixesJson.ValueKind == JsonValueKind.Array)
        {
            var list = new List<UplinkInterop.UplinkSharePrefix>();
            foreach (var p in prefixesJson.EnumerateArray())
            {
                list.Add(new UplinkInterop.UplinkSharePrefix
                {
                    bucket = Marshal.StringToCoTaskMemUTF8(p.TryGetProperty("bucket", out var b) ? b.GetString() ?? string.Empty : string.Empty),
                    prefix = Marshal.StringToCoTaskMemUTF8(p.TryGetProperty("prefix", out var pf) ? pf.GetString() ?? string.Empty : string.Empty)
                });
            }
            nativePrefixes = list.ToArray();
        }

        var accessHandle = _accessHandles.Get(accessId);
        UplinkInterop.UplinkAccessResult result;
        try
        {
            if (nativePrefixes.Length == 0)
            {
                result = UplinkInterop.uplink_access_share(accessHandle, permission, null, 0);
            }
            else
            {
                fixed (UplinkInterop.UplinkSharePrefix* ptr = nativePrefixes)
                    result = UplinkInterop.uplink_access_share(accessHandle, permission, ptr, checked((nint)nativePrefixes.Length));
            }
        }
        finally
        {
            foreach (var np in nativePrefixes)
            {
                if (np.bucket != nint.Zero) Marshal.FreeCoTaskMem(np.bucket);
                if (np.prefix != nint.Zero) Marshal.FreeCoTaskMem(np.prefix);
            }
        }

        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                return Error(msg, code);
            }

            var newAccessHandle = result.access;
            result.access = nint.Zero;

            var cfg = BuildConfig(userAgent, dialTimeout, tempDir);
            UplinkInterop.UplinkProjectResult projectResult;
            try
            {
                projectResult = UplinkInterop.uplink_config_open_project(cfg, newAccessHandle);
            }
            finally
            {
                FreeConfig(cfg);
            }

            if (projectResult.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref projectResult.error);
                UplinkInterop.uplink_free_project_result(projectResult);
                UplinkInterop.FreeAccessHandle(newAccessHandle);
                return Error(msg, code);
            }

            var projectHandle = projectResult.project;
            projectResult.project = nint.Zero;
            UplinkInterop.uplink_free_project_result(projectResult);

            long newAccessId  = _accessHandles.Register(newAccessHandle);
            long newProjectId = _projectHandles.Register(projectHandle);
            return Ok(new() { ["access_id"] = newAccessId, ["project_id"] = newProjectId });
        }
        finally
        {
            UplinkInterop.uplink_free_access_result(result);
        }
    }

    private Dictionary<string, object?> AccessRevoke(JsonElement req)
    {
        long projectId     = GetLong(req, "project_id");
        long childAccessId = GetLong(req, "child_access_id");

        var projectHandle = _projectHandles.Get(projectId);
        var childHandle   = _accessHandles.Get(childAccessId);

        var errPtr = UplinkInterop.uplink_revoke_access(projectHandle, childHandle);
        if (errPtr != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeError(errPtr);
            return Error(msg, code);
        }

        return Ok();
    }

    private Dictionary<string, object?> AccessFree(JsonElement req)
    {
        long accessId  = GetLong(req, "access_id");
        long projectId = GetLong(req, "project_id");

        if (_projectHandles.Remove(projectId, out var projectHandle) && projectHandle != nint.Zero)
            UplinkInterop.FreeProjectHandle(projectHandle);

        if (_accessHandles.Remove(accessId, out var accessHandle) && accessHandle != nint.Zero)
            UplinkInterop.FreeAccessHandle(accessHandle);

        return Ok();
    }

    // ── Bucket ────────────────────────────────────────────────────────────────

    private Dictionary<string, object?> BucketOp(JsonElement req, string kind)
    {
        long projectId = GetLong(req, "project_id");
        var name       = GetStr(req, "name");
        var handle     = _projectHandles.Get(projectId);

        UplinkInterop.UplinkBucketResult result = kind switch
        {
            "create" => UplinkInterop.uplink_create_bucket(handle, name),
            "ensure" => UplinkInterop.uplink_ensure_bucket(handle, name),
            "stat"   => UplinkInterop.uplink_stat_bucket(handle, name),
            "delete" => UplinkInterop.uplink_delete_bucket(handle, name),
            _        => throw new InvalidOperationException($"Unknown bucket op: {kind}")
        };

        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                return Error(msg, code);
            }

            var (bktName, bktCreated) = UplinkInterop.MarshalBucket(result.bucket);
            return Ok(new()
            {
                ["bucket_name"]    = bktName,
                ["bucket_created"] = bktCreated
            });
        }
        finally
        {
            UplinkInterop.uplink_free_bucket_result(result);
        }
    }

    private Dictionary<string, object?> BucketDeleteWithObjects(JsonElement req)
    {
        long projectId = GetLong(req, "project_id");
        var name       = GetStr(req, "name");
        var handle     = _projectHandles.Get(projectId);

        var result = UplinkInterop.uplink_delete_bucket_with_objects(handle, name);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                return Error(msg, code);
            }

            return Ok();
        }
        finally
        {
            UplinkInterop.uplink_free_bucket_result(result);
        }
    }

    private unsafe Dictionary<string, object?> BucketList(JsonElement req)
    {
        long projectId = GetLong(req, "project_id");
        var cursor     = GetStr(req, "cursor");
        var handle     = _projectHandles.Get(projectId);

        var cursorPtr = Marshal.StringToCoTaskMemUTF8(cursor);
        try
        {
            var opts = new UplinkInterop.UplinkListBucketsOptions { cursor = cursorPtr };
            nint iterator = UplinkInterop.uplink_list_buckets(handle, &opts);
            try
            {
                var buckets = new List<object?>();
                while (UplinkInterop.uplink_bucket_iterator_next(iterator))
                {
                    nint bPtr = UplinkInterop.uplink_bucket_iterator_item(iterator);
                    var (bname, bcreated) = UplinkInterop.MarshalBucket(bPtr);
                    buckets.Add(new Dictionary<string, object?>
                    {
                        ["name"]    = bname,
                        ["created"] = bcreated
                    });
                }

                nint errPtr = UplinkInterop.uplink_bucket_iterator_err(iterator);
                if (errPtr != nint.Zero)
                {
                    var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                    return Error(msg, code);
                }

                return Ok(new() { ["buckets"] = buckets });
            }
            finally
            {
                UplinkInterop.uplink_free_bucket_iterator(iterator);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(cursorPtr);
        }
    }

    // ── Object ────────────────────────────────────────────────────────────────

    private Dictionary<string, object?> ObjectStat(JsonElement req)
    {
        long projectId = GetLong(req, "project_id");
        var bucket     = GetStr(req, "bucket");
        var key        = GetStr(req, "key");
        var handle     = _projectHandles.Get(projectId);

        var result = UplinkInterop.uplink_stat_object(handle, bucket, key);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                return Error(msg, code);
            }

            return ObjectResult(UplinkInterop.MarshalObject(result.object_));
        }
        finally
        {
            UplinkInterop.uplink_free_object_result(result);
        }
    }

    private Dictionary<string, object?> ObjectDelete(JsonElement req)
    {
        long projectId = GetLong(req, "project_id");
        var bucket     = GetStr(req, "bucket");
        var key        = GetStr(req, "key");
        var handle     = _projectHandles.Get(projectId);

        var result = UplinkInterop.uplink_delete_object(handle, bucket, key);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                return Error(msg, code);
            }

            if (result.object_ == nint.Zero)
                return Ok();

            return ObjectResult(UplinkInterop.MarshalObject(result.object_));
        }
        finally
        {
            UplinkInterop.uplink_free_object_result(result);
        }
    }

    private unsafe Dictionary<string, object?> ObjectList(JsonElement req)
    {
        long projectId  = GetLong(req, "project_id");
        var bucket      = GetStr(req, "bucket");
        var prefix      = GetStr(req, "prefix");
        var cursor      = GetStr(req, "cursor");
        bool recursive  = GetBool(req, "recursive");
        bool systemMeta = GetBool(req, "system_meta");
        bool customMeta = GetBool(req, "custom_meta");
        var handle      = _projectHandles.Get(projectId);

        var prefixPtr = Marshal.StringToCoTaskMemUTF8(prefix);
        var cursorPtr = Marshal.StringToCoTaskMemUTF8(cursor);
        var bucketPtr = Marshal.StringToCoTaskMemUTF8(bucket);
        try
        {
            var opts = new UplinkInterop.UplinkListObjectsOptions
            {
                prefix    = prefixPtr,
                cursor    = cursorPtr,
                recursive = recursive,
                system    = systemMeta,
                custom    = customMeta
            };

            nint iterator = UplinkInterop.uplink_list_objects(handle, bucketPtr, &opts);
            try
            {
                var objects = new List<object?>();
                while (UplinkInterop.uplink_object_iterator_next(iterator))
                {
                    nint oPtr = UplinkInterop.uplink_object_iterator_item(iterator);
                    objects.Add(BuildObjectDict(UplinkInterop.MarshalObject(oPtr)));
                }

                nint errPtr = UplinkInterop.uplink_object_iterator_err(iterator);
                if (errPtr != nint.Zero)
                {
                    var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                    return Error(msg, code);
                }

                return Ok(new() { ["objects"] = objects });
            }
            finally
            {
                UplinkInterop.uplink_free_object_iterator(iterator);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(prefixPtr);
            Marshal.FreeCoTaskMem(cursorPtr);
            Marshal.FreeCoTaskMem(bucketPtr);
        }
    }

    private Dictionary<string, object?> ObjectCopy(JsonElement req)
    {
        long projectId = GetLong(req, "project_id");
        var srcBucket  = GetStr(req, "src_bucket");
        var srcKey     = GetStr(req, "src_key");
        var dstBucket  = GetStr(req, "dst_bucket");
        var dstKey     = GetStr(req, "dst_key");
        var handle     = _projectHandles.Get(projectId);

        var result = UplinkInterop.uplink_copy_object(handle, srcBucket, srcKey, dstBucket, dstKey, nint.Zero);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                return Error(msg, code);
            }

            return ObjectResult(UplinkInterop.MarshalObject(result.object_));
        }
        finally
        {
            UplinkInterop.uplink_free_object_result(result);
        }
    }

    private Dictionary<string, object?> ObjectMove(JsonElement req)
    {
        long projectId = GetLong(req, "project_id");
        var srcBucket  = GetStr(req, "src_bucket");
        var srcKey     = GetStr(req, "src_key");
        var dstBucket  = GetStr(req, "dst_bucket");
        var dstKey     = GetStr(req, "dst_key");
        var handle     = _projectHandles.Get(projectId);

        var errPtr = UplinkInterop.uplink_move_object(handle, srcBucket, srcKey, dstBucket, dstKey, nint.Zero);
        if (errPtr != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref errPtr);
            return Error(msg, code);
        }

        return Ok();
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    private unsafe Dictionary<string, object?> UploadBegin(JsonElement req)
    {
        long projectId = GetLong(req, "project_id");
        var bucket     = GetStr(req, "bucket");
        var key        = GetStr(req, "key");
        long expires   = GetLong(req, "expires");
        var handle     = _projectHandles.Get(projectId);

        var opts = new UplinkInterop.UplinkUploadOptions { expires = expires };
        var result = UplinkInterop.uplink_upload_object(handle, bucket, key, &opts);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                return Error(msg, code);
            }

            if (result.upload == nint.Zero)
                return Error("Native library returned a null upload handle.", -1);

            var uploadHandle = result.upload;
            result.upload = nint.Zero;
            long uploadId = _uploadHandles.Register(uploadHandle);
            return Ok(new() { ["upload_id"] = uploadId });
        }
        finally
        {
            UplinkInterop.uplink_free_upload_result(result);
        }
    }

    private unsafe Dictionary<string, object?> UploadWrite(JsonElement req)
    {
        long uploadId = GetLong(req, "upload_id");
        var dataB64   = GetStr(req, "data_b64");
        var handle    = _uploadHandles.Get(uploadId);

        if (string.IsNullOrEmpty(dataB64))
            return Ok(new() { ["bytes_written"] = 0L });

        var data = Convert.FromBase64String(dataB64);

        UplinkInterop.UplinkWriteResult writeResult;
        var gcHandle = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            unsafe
            {
                var ptr = (void*)gcHandle.AddrOfPinnedObject();
                writeResult = UplinkInterop.uplink_upload_write(handle, ptr, (nuint)data.Length);
            }
        }
        finally
        {
            gcHandle.Free();
        }

        try
        {
            if (writeResult.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref writeResult.error);
                return Error(msg, code);
            }

            return Ok(new() { ["bytes_written"] = (long)(nuint)writeResult.bytes_written });
        }
        finally
        {
            UplinkInterop.uplink_free_write_result(writeResult);
        }
    }

    private unsafe Dictionary<string, object?> UploadSetMetadata(JsonElement req)
    {
        long uploadId  = GetLong(req, "upload_id");
        var handle     = _uploadHandles.Get(uploadId);
        var entriesElem = req.TryGetProperty("entries", out var ee) ? ee : default;

        if (entriesElem.ValueKind != JsonValueKind.Array)
            return Ok();

        var nativeEntries = new List<UplinkInterop.UplinkCustomMetadataEntry>();
        foreach (var e in entriesElem.EnumerateArray())
        {
            var k = e.TryGetProperty("key",   out var ke) ? ke.GetString() ?? string.Empty : string.Empty;
            var v = e.TryGetProperty("value", out var ve) ? ve.GetString() ?? string.Empty : string.Empty;
            nativeEntries.Add(new UplinkInterop.UplinkCustomMetadataEntry
            {
                key          = Marshal.StringToCoTaskMemUTF8(k),
                key_length   = (nuint)System.Text.Encoding.UTF8.GetByteCount(k),
                value        = Marshal.StringToCoTaskMemUTF8(v),
                value_length = (nuint)System.Text.Encoding.UTF8.GetByteCount(v)
            });
        }

        var arr = nativeEntries.ToArray();
        try
        {
            nint errPtr;
            fixed (UplinkInterop.UplinkCustomMetadataEntry* entriesPtr = arr)
            {
                var meta = new UplinkInterop.UplinkCustomMetadata
                {
                    entries = (nint)entriesPtr,
                    count   = (nuint)arr.Length
                };
                errPtr = UplinkInterop.uplink_upload_set_custom_metadata(handle, meta);
            }

            if (errPtr != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                return Error(msg, code);
            }

            return Ok();
        }
        finally
        {
            foreach (var e in arr)
            {
                if (e.key   != nint.Zero) Marshal.FreeCoTaskMem(e.key);
                if (e.value != nint.Zero) Marshal.FreeCoTaskMem(e.value);
            }
        }
    }

    private Dictionary<string, object?> UploadCommit(JsonElement req)
    {
        long uploadId = GetLong(req, "upload_id");
        if (!_uploadHandles.Remove(uploadId, out var handle) || handle == nint.Zero)
            return Error($"Upload handle {uploadId} not found.", -1);

        var errPtr = UplinkInterop.uplink_upload_commit(handle);
        UplinkInterop.FreeUploadHandle(handle);

        if (errPtr != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeError(errPtr);
            return Error(msg, code);
        }

        return Ok();
    }

    private Dictionary<string, object?> UploadAbort(JsonElement req)
    {
        long uploadId = GetLong(req, "upload_id");
        if (!_uploadHandles.Remove(uploadId, out var handle) || handle == nint.Zero)
            return Ok(); // Already freed or doesn't exist - idempotent

        var errPtr = UplinkInterop.uplink_upload_abort(handle);
        UplinkInterop.FreeUploadHandle(handle);

        if (errPtr != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeError(errPtr);
            return Error(msg, code);
        }

        return Ok();
    }

    // ── Download ──────────────────────────────────────────────────────────────

    private unsafe Dictionary<string, object?> DownloadBegin(JsonElement req)
    {
        long projectId = GetLong(req, "project_id");
        var bucket     = GetStr(req, "bucket");
        var key        = GetStr(req, "key");
        long offset    = GetLong(req, "offset");
        long length    = req.TryGetProperty("length", out var le) ? le.GetInt64() : -1L;
        var handle     = _projectHandles.Get(projectId);

        var opts = new UplinkInterop.UplinkDownloadOptions { offset = offset, length = length };
        var result = UplinkInterop.uplink_download_object(handle, bucket, key, &opts);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                return Error(msg, code);
            }

            if (result.download == nint.Zero)
                return Error("Native library returned a null download handle.", -1);

            var downloadHandle = result.download;
            result.download = nint.Zero;

            // Get total bytes from info
            long totalBytes = 0;
            var infoResult = UplinkInterop.uplink_download_info(downloadHandle);
            if (infoResult.error == nint.Zero && infoResult.object_ != nint.Zero)
            {
                var wobj = UplinkInterop.MarshalObject(infoResult.object_);
                totalBytes = CalculateDownloadLength(wobj.ContentLength, offset, length);
            }
            UplinkInterop.uplink_free_object_result(infoResult);

            long downloadId = _downloadHandles.Register(downloadHandle);
            return Ok(new() { ["download_id"] = downloadId, ["total_bytes"] = totalBytes });
        }
        finally
        {
            UplinkInterop.uplink_free_download_result(result);
        }
    }

    private unsafe Dictionary<string, object?> DownloadRead(JsonElement req)
    {
        long downloadId = GetLong(req, "download_id");
        int maxBytes    = (int)GetLong(req, "max_bytes");
        if (maxBytes <= 0) maxBytes = 80 * 1024;
        var handle      = _downloadHandles.Get(downloadId);

        var buf = new byte[maxBytes];
        UplinkInterop.UplinkReadResult readResult;
        fixed (byte* bufPtr = buf)
        {
            readResult = UplinkInterop.uplink_download_read(handle, bufPtr, (nuint)maxBytes);
        }

        try
        {
            int bytesRead = (int)(nuint)readResult.bytes_read;
            bool eof = false;

            if (readResult.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref readResult.error);
                bool isEof = code == UplinkInterop.EndOfFileErrorCode
                    || msg.Contains("EOF", StringComparison.OrdinalIgnoreCase);
                if (!isEof)
                    return Error(msg, code);
                eof = true;
            }

            if (bytesRead == 0 && !eof)
                eof = true;

            var dataB64 = bytesRead > 0 ? Convert.ToBase64String(buf, 0, bytesRead) : string.Empty;
            return Ok(new() { ["data_b64"] = dataB64, ["bytes_read"] = bytesRead, ["eof"] = eof });
        }
        finally
        {
            UplinkInterop.uplink_free_read_result(readResult);
        }
    }

    private Dictionary<string, object?> DownloadClose(JsonElement req)
    {
        long downloadId = GetLong(req, "download_id");
        if (!_downloadHandles.Remove(downloadId, out var handle) || handle == nint.Zero)
            return Ok();

        var errPtr = UplinkInterop.uplink_close_download(handle);
        UplinkInterop.FreeDownloadHandle(handle);

        if (errPtr != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeError(errPtr);
            return Error(msg, code);
        }

        return Ok();
    }

    // ── Multipart ─────────────────────────────────────────────────────────────

    private unsafe Dictionary<string, object?> MultipartBegin(JsonElement req)
    {
        long projectId = GetLong(req, "project_id");
        var bucket     = GetStr(req, "bucket");
        var key        = GetStr(req, "key");
        long expires   = GetLong(req, "expires");
        var handle     = _projectHandles.Get(projectId);

        var opts = new UplinkInterop.UplinkUploadOptions { expires = expires };
        var result = UplinkInterop.uplink_begin_upload(handle, bucket, key, &opts);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                return Error(msg, code);
            }

            var (uploadIdStr, _) = UplinkInterop.MarshalUploadInfo(result.info);
            return Ok(new() { ["upload_id_str"] = uploadIdStr });
        }
        finally
        {
            UplinkInterop.uplink_free_upload_info_result(result);
        }
    }

    private unsafe Dictionary<string, object?> MultipartCommit(JsonElement req)
    {
        long projectId    = GetLong(req, "project_id");
        var bucket        = GetStr(req, "bucket");
        var key           = GetStr(req, "key");
        var uploadIdStr   = GetStr(req, "upload_id_str");
        var handle        = _projectHandles.Get(projectId);

        var entriesElem = req.TryGetProperty("entries", out var ee) ? ee : default;
        UplinkInterop.UplinkCustomMetadataEntry[]? entries = null;
        System.Runtime.InteropServices.GCHandle entriesPin = default;
        nint entriesPtr = nint.Zero;

        if (entriesElem.ValueKind == JsonValueKind.Array)
        {
            var list = new List<UplinkInterop.UplinkCustomMetadataEntry>();
            foreach (var e in entriesElem.EnumerateArray())
            {
                var k = e.TryGetProperty("key",   out var ke) ? ke.GetString() ?? string.Empty : string.Empty;
                var v = e.TryGetProperty("value", out var ve) ? ve.GetString() ?? string.Empty : string.Empty;
                list.Add(new UplinkInterop.UplinkCustomMetadataEntry
                {
                    key          = Marshal.StringToCoTaskMemUTF8(k),
                    key_length   = (nuint)System.Text.Encoding.UTF8.GetByteCount(k),
                    value        = Marshal.StringToCoTaskMemUTF8(v),
                    value_length = (nuint)System.Text.Encoding.UTF8.GetByteCount(v)
                });
            }
            entries = list.ToArray();
            if (entries.Length > 0)
            {
                entriesPin = System.Runtime.InteropServices.GCHandle.Alloc(entries, System.Runtime.InteropServices.GCHandleType.Pinned);
                entriesPtr = entriesPin.AddrOfPinnedObject();
            }
        }

        var nativeMeta = new UplinkInterop.UplinkCustomMetadata
        {
            entries = entriesPtr,
            count   = entries != null ? (nuint)entries.Length : 0
        };
        var nativeOpts = new UplinkInterop.UplinkCommitUploadOptions { custom_metadata = nativeMeta };

        UplinkInterop.UplinkCommitUploadResult result;
        try
        {
            result = UplinkInterop.uplink_commit_upload(handle, bucket, key, uploadIdStr, &nativeOpts);
        }
        finally
        {
            if (entriesPin.IsAllocated) entriesPin.Free();
            if (entries != null)
                foreach (var e in entries)
                {
                    if (e.key   != nint.Zero) Marshal.FreeCoTaskMem(e.key);
                    if (e.value != nint.Zero) Marshal.FreeCoTaskMem(e.value);
                }
        }

        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                return Error(msg, code);
            }

            if (result.object_ == nint.Zero)
                return Ok();

            return ObjectResult(UplinkInterop.MarshalObject(result.object_));
        }
        finally
        {
            UplinkInterop.uplink_free_commit_upload_result(result);
        }
    }

    private Dictionary<string, object?> MultipartAbort(JsonElement req)
    {
        long projectId  = GetLong(req, "project_id");
        var bucket      = GetStr(req, "bucket");
        var key         = GetStr(req, "key");
        var uploadIdStr = GetStr(req, "upload_id_str");
        var handle      = _projectHandles.Get(projectId);

        var errPtr = UplinkInterop.uplink_abort_upload(handle, bucket, key, uploadIdStr);
        if (errPtr != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeError(errPtr);
            return Error(msg, code);
        }

        return Ok();
    }

    private unsafe Dictionary<string, object?> MultipartUploadPart(JsonElement req)
    {
        long projectId  = GetLong(req, "project_id");
        var bucket      = GetStr(req, "bucket");
        var key         = GetStr(req, "key");
        var uploadIdStr = GetStr(req, "upload_id_str");
        uint partNumber = (uint)GetLong(req, "part_number");
        var dataB64     = GetStr(req, "data_b64");
        var handle      = _projectHandles.Get(projectId);

        var partResult = UplinkInterop.uplink_upload_part(handle, bucket, key, uploadIdStr, partNumber);
        if (partResult.error != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref partResult.error);
            UplinkInterop.uplink_free_part_upload_result(partResult);
            return Error(msg, code);
        }

        var partHandle = partResult.part_upload;
        UplinkInterop.uplink_free_part_upload_result(partResult);

        if (partHandle == nint.Zero)
            return Error("Native library returned a null part upload handle.", -1);

        try
        {
            var data = string.IsNullOrEmpty(dataB64) ? Array.Empty<byte>() : Convert.FromBase64String(dataB64);
            long totalWritten = 0;

            if (data.Length > 0)
            {
                const int chunkSize = 80 * 1024;
                int offset = 0;
                while (offset < data.Length)
                {
                    int toWrite = Math.Min(chunkSize, data.Length - offset);
                    UplinkInterop.UplinkWriteResult writeResult;
                    var gcHandle = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
                    try
                    {
                        var ptr = (void*)(gcHandle.AddrOfPinnedObject() + offset);
                        writeResult = UplinkInterop.uplink_part_upload_write(partHandle, ptr, (nuint)toWrite);
                    }
                    finally
                    {
                        gcHandle.Free();
                    }

                    try
                    {
                        if (writeResult.error != nint.Zero)
                        {
                            var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref writeResult.error);
                            return Error(msg, code);
                        }

                        int written = (int)(nuint)writeResult.bytes_written;
                        if (written == 0)
                            return Error("Part upload write stalled: 0 bytes written.", -1);

                        totalWritten += written;
                        offset += written;
                    }
                    finally
                    {
                        UplinkInterop.uplink_free_write_result(writeResult);
                    }
                }
            }

            var commitErr = UplinkInterop.uplink_part_upload_commit(partHandle);
            if (commitErr != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeError(commitErr);
                return Error(msg, code);
            }

            return Ok(new() { ["bytes_written"] = totalWritten });
        }
        finally
        {
            UplinkInterop.FreePartUploadHandle(partHandle);
        }
    }

    private unsafe Dictionary<string, object?> MultipartList(JsonElement req)
    {
        long projectId = GetLong(req, "project_id");
        var bucket     = GetStr(req, "bucket");
        var prefix     = GetStr(req, "prefix");
        var cursor     = GetStr(req, "cursor");
        var handle     = _projectHandles.Get(projectId);

        var prefixPtr = Marshal.StringToCoTaskMemUTF8(prefix);
        var cursorPtr = Marshal.StringToCoTaskMemUTF8(cursor);
        try
        {
            var opts = new UplinkInterop.UplinkListUploadsOptions
            {
                prefix = prefixPtr,
                cursor = cursorPtr
            };

            nint iterator = UplinkInterop.uplink_list_uploads(handle, bucket, &opts);
            try
            {
                var uploads = new List<object?>();
                while (UplinkInterop.uplink_upload_iterator_next(iterator))
                {
                    nint infoPtr = UplinkInterop.uplink_upload_iterator_item(iterator);
                    var (uid, ukey) = UplinkInterop.MarshalUploadInfo(infoPtr);
                    uploads.Add(new Dictionary<string, object?>
                    {
                        ["upload_id"] = uid,
                        ["key"]       = ukey
                    });
                }

                nint errPtr = UplinkInterop.uplink_upload_iterator_err(iterator);
                if (errPtr != nint.Zero)
                {
                    var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                    return Error(msg, code);
                }

                return Ok(new() { ["uploads"] = uploads });
            }
            finally
            {
                UplinkInterop.uplink_free_upload_iterator(iterator);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(prefixPtr);
            Marshal.FreeCoTaskMem(cursorPtr);
        }
    }

    private unsafe Dictionary<string, object?> MultipartListParts(JsonElement req)
    {
        long projectId  = GetLong(req, "project_id");
        var bucket      = GetStr(req, "bucket");
        var key         = GetStr(req, "key");
        var uploadIdStr = GetStr(req, "upload_id_str");
        uint cursor     = (uint)GetLong(req, "cursor");
        var handle      = _projectHandles.Get(projectId);

        var opts = new UplinkInterop.UplinkListUploadPartsOptions { cursor = cursor };
        nint iterator = UplinkInterop.uplink_list_upload_parts(handle, bucket, key, uploadIdStr, &opts);
        try
        {
            var parts = new List<object?>();
            while (UplinkInterop.uplink_part_iterator_next(iterator))
            {
                nint partPtr = UplinkInterop.uplink_part_iterator_item(iterator);
                var part = UplinkInterop.MarshalPart(partPtr);
                parts.Add(new Dictionary<string, object?>
                {
                    ["part_number"] = part.PartNumber,
                    ["size"]        = part.Size,
                    ["modified"]    = part.Modified,
                    ["etag"]        = part.ETag
                });
            }

            nint errPtr = UplinkInterop.uplink_part_iterator_err(iterator);
            if (errPtr != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                return Error(msg, code);
            }

            return Ok(new() { ["parts"] = parts });
        }
        finally
        {
            UplinkInterop.uplink_free_part_iterator(iterator);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Ok(Dictionary<string, object?>? extra = null)
    {
        var d = new Dictionary<string, object?> { ["err"] = null };
        if (extra != null)
            foreach (var kv in extra) d[kv.Key] = kv.Value;
        return d;
    }

    private static Dictionary<string, object?> Error(string msg, int code) =>
        new() { ["err"] = msg, ["code"] = code };

    private static Dictionary<string, object?> ObjectResult(WorkerObject obj)
    {
        var d = Ok(BuildObjectDict(obj));
        return d;
    }

    private static Dictionary<string, object?> BuildObjectDict(WorkerObject obj)
    {
        var d = new Dictionary<string, object?>
        {
            ["obj_key"]            = obj.Key,
            ["obj_is_prefix"]      = obj.IsPrefix,
            ["obj_created"]        = obj.Created,
            ["obj_expires"]        = obj.Expires,
            ["obj_content_length"] = obj.ContentLength
        };

        if (obj.CustomMetadata.Count > 0)
        {
            d["obj_custom_metadata"] = obj.CustomMetadata
                .Select(kv => new Dictionary<string, object?> { ["key"] = kv.Key, ["value"] = kv.Value })
                .ToList<object?>();
        }
        else
        {
            d["obj_custom_metadata"] = new List<object?>();
        }

        return d;
    }

    private static string GetStr(JsonElement req, string key, string def = "") =>
        req.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? def : def;

    private static long GetLong(JsonElement req, string key, long def = 0) =>
        req.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.Number)
            ? v.GetInt64() : def;

    private static int GetInt(JsonElement req, string key, int def = 0) =>
        req.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : def;

    private static bool GetBool(JsonElement req, string key) =>
        req.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static UplinkInterop.UplinkConfig BuildConfig(string userAgent, int dialTimeout, string tempDir)
    {
        return new UplinkInterop.UplinkConfig
        {
            user_agent               = Marshal.StringToCoTaskMemUTF8(userAgent ?? string.Empty),
            dial_timeout_milliseconds = dialTimeout,
            temp_directory           = Marshal.StringToCoTaskMemUTF8(
                string.IsNullOrWhiteSpace(tempDir) ? Path.GetTempPath() : tempDir)
        };
    }

    private static void FreeConfig(UplinkInterop.UplinkConfig cfg)
    {
        if (cfg.user_agent    != nint.Zero) Marshal.FreeCoTaskMem(cfg.user_agent);
        if (cfg.temp_directory != nint.Zero) Marshal.FreeCoTaskMem(cfg.temp_directory);
    }

    private static long CalculateDownloadLength(long contentLength, long offset, long length)
    {
        var off = Math.Max(0, offset);
        if (contentLength <= off) return 0;
        var remaining = contentLength - off;
        if (length < 0) return remaining;
        return Math.Min(remaining, length);
    }
}
