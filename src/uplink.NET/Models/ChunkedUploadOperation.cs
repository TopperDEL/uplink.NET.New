using uplink.NET.Ipc;

namespace uplink.NET.Models;

/// <summary>
/// Allows uploading large objects in sequential chunks without buffering the
/// entire payload in memory.
/// </summary>
public class ChunkedUploadOperation : IDisposable
{
    private readonly Access _access;
    private long _uploadId;
    private Access.ProjectHandleLease? _projectLease;
    private bool _committed;
    private bool _disposed;

    public string ObjectName { get; }
    public bool Failed { get; private set; }
    public string? ErrorMessage { get; private set; }

    internal ChunkedUploadOperation(long uploadId, string objectName, Access.ProjectHandleLease projectLease, Access access)
    {
        ArgumentNullException.ThrowIfNull(projectLease);
        _access       = access ?? throw new ArgumentNullException(nameof(access));
        _uploadId     = uploadId;
        ObjectName    = objectName;
        _projectLease = projectLease;
    }

    /// <summary>Writes the supplied bytes to the upload stream.</summary>
    public async Task<bool> UploadChunkAsync(byte[] chunk)
    {
        if (chunk is null || chunk.Length == 0)
            return true;

        await Task.Yield();

        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]        = "upload_write",
            ["upload_id"] = _uploadId,
            ["data_b64"]  = Convert.ToBase64String(chunk)
        }).ConfigureAwait(false);

        if (result.IsError)
        {
            Failed       = true;
            ErrorMessage = result.ErrorMessage;
            return false;
        }

        return true;
    }

    /// <summary>Commits the upload, finalizing the object on Storj.</summary>
    public async Task<bool> CommitAsync()
    {
        if (_committed) return true;
        if (_disposed) throw new ObjectDisposedException(nameof(ChunkedUploadOperation));

        await Task.Yield();

        var id = _uploadId;
        _uploadId  = 0;
        _committed = true;

        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]        = "upload_commit",
            ["upload_id"] = id
        }).ConfigureAwait(false);

        if (result.IsError)
        {
            _committed   = false;
            Failed       = true;
            ErrorMessage = result.ErrorMessage;
            return false;
        }

        ReleaseProjectLease();
        return true;
    }

    /// <summary>Aborts the upload, discarding all uploaded data.</summary>
    public async Task<bool> AbortAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ChunkedUploadOperation));

        await Task.Yield();

        var id = _uploadId;
        _uploadId = 0;

        if (id != 0)
        {
            var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
            {
                ["op"]        = "upload_abort",
                ["upload_id"] = id
            }).ConfigureAwait(false);

            if (result.IsError)
            {
                Failed       = true;
                ErrorMessage = result.ErrorMessage;
                ReleaseProjectLease();
                return false;
            }
        }

        ReleaseProjectLease();
        return true;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (_uploadId != 0 && !_committed)
        {
            var id = _uploadId;
            _uploadId = 0;
            _ = Task.Run(async () =>
            {
                // Fire-and-forget: if lost, the worker cleans up on stdin-close / process exit.
                try
                {
                    await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
                    {
                        ["op"]        = "upload_abort",
                        ["upload_id"] = id
                    }).ConfigureAwait(false);
                }
                catch { }
            });
        }

        ReleaseProjectLease();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void ReleaseProjectLease()
    {
        _projectLease?.Dispose();
        _projectLease = null;
    }
}
