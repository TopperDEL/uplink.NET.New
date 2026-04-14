using uplink.NET.Exceptions;
using uplink.NET.Interfaces;
using uplink.NET.Ipc;
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

    public async Task<UploadInfo> BeginUploadAsync(
        string bucketName, string objectKey, UploadOptions uploadOptions)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]         = "multipart_begin",
            ["project_id"] = projectLease.Handle,
            ["bucket"]     = bucketName,
            ["key"]        = objectKey,
            ["expires"]    = UplinkInterop.DateTimeToUnix(uploadOptions?.Expires)
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new MultipartUploadFailedException(result.ErrorMessage!);

        return new UploadInfo
        {
            UploadId = result.Data.TryGetProperty("upload_id_str", out var u) ? u.GetString() ?? string.Empty : string.Empty,
            Key      = objectKey
        };
    }

    public async Task<CommitUploadResult> CommitUploadAsync(
        string bucketName, string objectKey, string uploadId,
        CommitUploadOptions commitUploadOptions)
    {
        using var projectLease = _access.AcquireProjectLease();

        var entries = commitUploadOptions?.CustomMetadata?.Entries.Count > 0
            ? commitUploadOptions.CustomMetadata!.Entries
                .Select(kv => (object?)new Dictionary<string, object?> { ["key"] = kv.Key, ["value"] = kv.Value })
                .ToArray()
            : Array.Empty<object?>();

        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]            = "multipart_commit",
            ["project_id"]    = projectLease.Handle,
            ["bucket"]        = bucketName,
            ["key"]           = objectKey,
            ["upload_id_str"] = uploadId,
            ["entries"]       = entries
        }).ConfigureAwait(false);

        var commitResult = new CommitUploadResult();
        if (result.IsError)
        {
            commitResult.Error = result.ErrorMessage ?? "Unknown error";
        }
        else if (result.Data.TryGetProperty("obj_key", out _))
        {
            commitResult.Object = ParseStorjObject(result.Data);
        }

        return commitResult;
    }

    public async Task AbortUploadAsync(string bucketName, string objectKey, string uploadId)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]            = "multipart_abort",
            ["project_id"]    = projectLease.Handle,
            ["bucket"]        = bucketName,
            ["key"]           = objectKey,
            ["upload_id_str"] = uploadId
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new AbortUploadFailedException(result.ErrorMessage!);
    }

    public async Task<PartUploadResult> UploadPartAsync(
        string bucketName, string objectKey, string uploadId,
        uint partNumber, byte[] partBytes)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]            = "multipart_upload_part",
            ["project_id"]    = projectLease.Handle,
            ["bucket"]        = bucketName,
            ["key"]           = objectKey,
            ["upload_id_str"] = uploadId,
            ["part_number"]   = (long)partNumber,
            ["data_b64"]      = partBytes.Length > 0 ? Convert.ToBase64String(partBytes) : string.Empty
        }).ConfigureAwait(false);

        var uploadResult = new PartUploadResult();
        if (result.IsError)
        {
            uploadResult.Error = result.ErrorMessage ?? "Unknown error";
        }
        else
        {
            uploadResult.BytesWritten = (uint)(result.Data.TryGetProperty("bytes_written", out var bw) ? bw.GetInt64() : 0L);
        }

        return uploadResult;
    }

    public Task UploadPartSetETagAsync(PartUpload partUpload, string eTag)
    {
        // PartUpload handles are not exposed via IPC - not implemented
        throw new NotImplementedException(
            "UploadPartSetETagAsync is not supported in the IPC architecture. " +
            "ETag must be set before or after the part upload via multipart commit options.");
    }

    public Task<PartResult> GetPartUploadInfoAsync(PartUpload partUpload)
    {
        // PartUpload handles are not exposed via IPC - not implemented
        throw new NotImplementedException(
            "GetPartUploadInfoAsync is not supported in the IPC architecture.");
    }

    public async Task<UploadsList> ListUploadsAsync(
        string bucketName, ListUploadOptions listUploadOptions)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]         = "multipart_list",
            ["project_id"] = projectLease.Handle,
            ["bucket"]     = bucketName,
            ["prefix"]     = listUploadOptions.Prefix ?? string.Empty,
            ["cursor"]     = listUploadOptions.Cursor ?? string.Empty
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new MultipartUploadFailedException(result.ErrorMessage!);

        var list = new UploadsList();
        if (result.Data.TryGetProperty("uploads", out var uploadsElem))
        {
            foreach (var u in uploadsElem.EnumerateArray())
            {
                list.Items.Add(new UploadInfo
                {
                    UploadId = u.TryGetProperty("upload_id", out var uid) ? uid.GetString() ?? string.Empty : string.Empty,
                    Key      = u.TryGetProperty("key",       out var uk)  ? uk.GetString()  ?? string.Empty : string.Empty
                });
            }
        }

        return list;
    }

    public async Task<UploadPartsList> ListUploadPartsAsync(
        string bucketName, string objectKey, string uploadId,
        ListUploadPartsOptions listUploadPartOptions)
    {
        using var projectLease = _access.AcquireProjectLease();
        uint cursor = listUploadPartOptions.CursorPartNumber != 0
            ? listUploadPartOptions.CursorPartNumber
            : (!string.IsNullOrWhiteSpace(listUploadPartOptions.Cursor) && uint.TryParse(listUploadPartOptions.Cursor, out var pc) ? pc : 0);

        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]            = "multipart_list_parts",
            ["project_id"]    = projectLease.Handle,
            ["bucket"]        = bucketName,
            ["key"]           = objectKey,
            ["upload_id_str"] = uploadId,
            ["cursor"]        = (long)cursor
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new MultipartUploadFailedException(result.ErrorMessage!);

        var list = new UploadPartsList();
        if (result.Data.TryGetProperty("parts", out var partsElem))
        {
            foreach (var p in partsElem.EnumerateArray())
            {
                list.Items.Add(new PartResult
                {
                    PartNumber = (uint)(p.TryGetProperty("part_number", out var pn) ? pn.GetInt64() : 0L),
                    Size       = p.TryGetProperty("size",     out var ps) ? ps.GetInt64() : 0L,
                    Modified   = p.TryGetProperty("modified", out var pm)
                        ? DateTimeOffset.FromUnixTimeSeconds(pm.GetInt64()).UtcDateTime : DateTime.MinValue,
                    ETag       = p.TryGetProperty("etag",     out var pe) ? pe.GetString() ?? string.Empty : string.Empty
                });
            }
        }

        return list;
    }

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
            Created       = created == 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime,
            Expires       = expires == 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime,
            ContentLength = contentLength
        };

        if (e.TryGetProperty("obj_custom_metadata", out var cm) && cm.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var meta = new CustomMetadata();
            foreach (var entry in cm.EnumerateArray())
            {
                var ek = entry.TryGetProperty("key",   out var ekv) ? ekv.GetString() ?? string.Empty : string.Empty;
                var ev = entry.TryGetProperty("value", out var evv) ? evv.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrEmpty(ek))
                    meta.Entries[ek] = ev;
            }
            if (meta.Entries.Count > 0)
                obj.CustomMetadata = meta;
        }

        return obj;
    }
}
