using uplink.NET.Native;

namespace uplink.NET.Models;

/// <summary>
/// Allows uploading large objects in sequential chunks without buffering the
/// entire payload in memory.
/// </summary>
public class ChunkedUploadOperation : IDisposable
{
    private readonly UplinkInterop.UplinkHandle _uploadHandle;
    private bool _committed;

    public string ObjectName { get; }
    public bool Failed { get; private set; }
    public string? ErrorMessage { get; private set; }

    internal ChunkedUploadOperation(UplinkInterop.UplinkHandle uploadHandle, string objectName)
    {
        _uploadHandle = uploadHandle;
        ObjectName    = objectName;
    }

    /// <summary>Writes the supplied bytes to the upload stream.</summary>
    public async Task<bool> UploadChunkAsync(byte[] chunk)
    {
        if (chunk is null || chunk.Length == 0)
            return true;

        await Task.Yield();

        unsafe
        {
            var writeResult = UplinkInterop.WithPinnedBuffer(
                chunk, 0, chunk.Length,
                (ptr, len) => UplinkInterop.uplink_upload_write(_uploadHandle, (void*)ptr, len));

            if (writeResult.error != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeError(writeResult.error);
                Failed       = true;
                ErrorMessage = msg;
                return false;
            }
        }

        return true;
    }

    /// <summary>Commits the upload, finalizing the object on Storj.</summary>
    public async Task<bool> CommitAsync()
    {
        if (_committed) return true;
        await Task.Yield();

        var errPtr = UplinkInterop.uplink_upload_commit(_uploadHandle);
        if (errPtr != nint.Zero)
        {
            var (msg, _) = UplinkInterop.ConsumeError(errPtr);
            Failed       = true;
            ErrorMessage = msg;
            return false;
        }

        _committed = true;
        return true;
    }

    /// <summary>Aborts the upload, discarding all uploaded data.</summary>
    public async Task<bool> AbortAsync()
    {
        await Task.Yield();
        var errPtr = UplinkInterop.uplink_upload_abort(_uploadHandle);
        if (errPtr != nint.Zero)
        {
            var (msg, _) = UplinkInterop.ConsumeError(errPtr);
            Failed       = true;
            ErrorMessage = msg;
            return false;
        }

        return true;
    }

    protected virtual void Dispose(bool disposing) { }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
