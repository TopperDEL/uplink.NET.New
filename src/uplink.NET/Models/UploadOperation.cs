using uplink.NET.Ipc;
using uplink.NET.Native;

namespace uplink.NET.Models;

public delegate void UploadOperationProgressChanged(UploadOperation uploadOperation);
public delegate void UploadOperationEnded(UploadOperation uploadOperation);

/// <summary>
/// Represents an in-progress upload to Storj.
/// Call <see cref="StartUploadAsync"/> to begin the transfer.
/// </summary>
public class UploadOperation : IDisposable
{
    private const int ChunkSizeBytes = 80 * 1024;

    private readonly Access _access;
    private readonly string _bucketName;
    private readonly byte[] _data;
    private readonly UplinkOptions _nativeOptions;
    private readonly CustomMetadata? _customMetadata;
    private readonly object _startSync = new();

    private bool _cancelRequested;
    private Access.ProjectHandleLease? _projectLease;

    public string ObjectName { get; }
    public long BytesSent { get; private set; }
    public long TotalBytes { get; private set; }
    public bool Completed { get; private set; }
    public bool Failed { get; set; }
    public bool Cancelled { get; set; }
    public bool Running { get; set; }
    public string? ErrorMessage { get; private set; }

    public float PercentageCompleted =>
        TotalBytes > 0 ? (float)BytesSent / TotalBytes * 100f : 0f;

    public event UploadOperationProgressChanged? UploadOperationProgressChanged;
    public event UploadOperationEnded? UploadOperationEnded;

    internal record UplinkOptions(long Expires);

    internal UploadOperation(
        Access access,
        string bucketName,
        string objectName,
        byte[] data,
        UplinkOptions nativeOptions,
        CustomMetadata? customMetadata)
    {
        _access         = access;
        _bucketName     = bucketName;
        ObjectName      = objectName;
        _data           = data;
        TotalBytes      = data.Length;
        _nativeOptions  = nativeOptions;
        _customMetadata = customMetadata;
    }

    public Task? StartUploadAsync()
    {
        lock (_startSync)
        {
            if (_projectLease != null)
                throw new InvalidOperationException("The upload operation has already been started.");

            var projectLease = _access.AcquireProjectLease();

            try
            {
                _projectLease = projectLease;
                return Task.Run(PerformUploadAsync);
            }
            catch
            {
                projectLease.Dispose();
                _projectLease = null;
                throw;
            }
        }
    }

    private async Task PerformUploadAsync()
    {
        Running   = true;
        BytesSent = 0;

        try
        {
            // Begin upload
            var beginResult = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
            {
                ["op"]         = "upload_begin",
                ["project_id"] = _projectLease!.Handle,
                ["bucket"]     = _bucketName,
                ["key"]        = ObjectName,
                ["expires"]    = _nativeOptions.Expires
            }).ConfigureAwait(false);

            if (beginResult.IsError)
            {
                SetFailed(beginResult.ErrorMessage!);
                return;
            }

            long uploadId = beginResult.Data.GetProperty("upload_id").GetInt64();

            if (_cancelRequested)
            {
                await AbortUploadAsync(uploadId).ConfigureAwait(false);
                Cancelled = true;
                Running   = false;
                UploadOperationEnded?.Invoke(this);
                return;
            }

            // Write data in IPC-sized chunks so large uploads do not send a single
            // giant base64 request to the worker process.
            if (_data.Length > 0)
            {
                for (int offset = 0; offset < _data.Length; offset += ChunkSizeBytes)
                {
                    int count = Math.Min(ChunkSizeBytes, _data.Length - offset);
                    var writeResult = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
                    {
                        ["op"]        = "upload_write",
                        ["upload_id"] = uploadId,
                        ["data_b64"]  = Convert.ToBase64String(_data, offset, count)
                    }).ConfigureAwait(false);

                    if (writeResult.IsError)
                    {
                        await AbortUploadAsync(uploadId).ConfigureAwait(false);
                        SetFailed(writeResult.ErrorMessage!);
                        return;
                    }

                    var bytesWritten = writeResult.Data.TryGetProperty("bytes_written", out var bw)
                        ? bw.GetInt64()
                        : 0L;

                    if (bytesWritten != count)
                    {
                        await AbortUploadAsync(uploadId).ConfigureAwait(false);
                        SetFailed(
                            $"Upload write mismatch at offset {offset}: wrote {bytesWritten} bytes, expected {count}.");
                        return;
                    }

                    BytesSent += bytesWritten;
                    UploadOperationProgressChanged?.Invoke(this);

                    if (_cancelRequested)
                    {
                        await AbortUploadAsync(uploadId).ConfigureAwait(false);
                        Cancelled = true;
                        Running   = false;
                        UploadOperationEnded?.Invoke(this);
                        return;
                    }
                }
            }

            // Set custom metadata if needed
            if (_customMetadata?.Entries.Count > 0)
            {
                var metaResult = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
                {
                    ["op"]        = "upload_set_metadata",
                    ["upload_id"] = uploadId,
                    ["entries"]   = _customMetadata.Entries
                        .Select(kv => (object?)new Dictionary<string, object?> { ["key"] = kv.Key, ["value"] = kv.Value })
                        .ToArray()
                }).ConfigureAwait(false);

                if (metaResult.IsError)
                {
                    await AbortUploadAsync(uploadId).ConfigureAwait(false);
                    SetFailed(metaResult.ErrorMessage!);
                    return;
                }
            }

            // Commit
            var commitResult = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
            {
                ["op"]        = "upload_commit",
                ["upload_id"] = uploadId
            }).ConfigureAwait(false);

            if (commitResult.IsError)
            {
                SetFailed(commitResult.ErrorMessage!);
                return;
            }

            Completed = true;
            Running   = false;
            UploadOperationEnded?.Invoke(this);
        }
        catch (Exception ex)
        {
            SetFailed(ex.Message);
        }
        finally
        {
            lock (_startSync)
            {
                _projectLease?.Dispose();
                _projectLease = null;
            }
        }
    }

    private static async Task AbortUploadAsync(long uploadId)
    {
        try
        {
            await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
            {
                ["op"]        = "upload_abort",
                ["upload_id"] = uploadId
            }).ConfigureAwait(false);
        }
        catch { }
    }

    private void SetFailed(string message)
    {
        Failed       = true;
        Running      = false;
        ErrorMessage = message;
        UploadOperationEnded?.Invoke(this);
    }

    public void Cancel() => _cancelRequested = true;

    protected virtual void Dispose(bool disposing) { }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
