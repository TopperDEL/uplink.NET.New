using uplink.NET.Native;

namespace uplink.NET.Models;

/// <summary>
/// Allows uploading large objects in sequential chunks without buffering the
/// entire payload in memory.
/// </summary>
public class ChunkedUploadOperation : IDisposable
{
    private readonly Access _access;
    private nint _uploadHandle;
    private Access.ProjectHandleLease? _projectLease;
    private bool _committed;
    private bool _disposed;

    public string ObjectName { get; }
    public bool Failed { get; private set; }
    public string? ErrorMessage { get; private set; }

    internal ChunkedUploadOperation(nint uploadHandle, string objectName, Access.ProjectHandleLease projectLease, Access access)
    {
        ArgumentNullException.ThrowIfNull(projectLease);
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _uploadHandle = uploadHandle;
        ObjectName    = objectName;
        _projectLease = projectLease;
    }

    /// <summary>Writes the supplied bytes to the upload stream.</summary>
    public async Task<bool> UploadChunkAsync(byte[] chunk)
    {
        if (chunk is null || chunk.Length == 0)
            return true;

        await Task.Yield();

        unsafe
        {
            using var trace = _access.Trace("uplink_upload_write", ("key", ObjectName), ("count", chunk.Length));
            var writeResult = UplinkInterop.WithPinnedBuffer(
                chunk, 0, chunk.Length,
                (ptr, len) => UplinkInterop.uplink_upload_write(_uploadHandle, (void*)ptr, len));
            try
            {
                if (writeResult.error != nint.Zero)
                {
                    var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref writeResult.error);
                    trace?.NativeError(msg, code);
                    Failed       = true;
                    ErrorMessage = msg;
                    return false;
                }

                trace?.Success();
            }
            finally
            {
                UplinkInterop.uplink_free_write_result(writeResult);
            }
        }

        return true;
    }

    /// <summary>Commits the upload, finalizing the object on Storj.</summary>
    public async Task<bool> CommitAsync()
    {
        if (_committed) return true;
        if (_disposed)
            throw new ObjectDisposedException(nameof(ChunkedUploadOperation));
        await Task.Yield();

        using var trace = _access.Trace("uplink_upload_commit", ("key", ObjectName));
        var errPtr = UplinkInterop.uplink_upload_commit(_uploadHandle);
        if (errPtr != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeError(errPtr);
            trace?.NativeError(msg, code);
            Failed       = true;
            ErrorMessage = msg;
            return false;
        }

        _committed = true;
        trace?.Success();
        ReleaseHandle();
        return true;
    }

    /// <summary>Aborts the upload, discarding all uploaded data.</summary>
    public async Task<bool> AbortAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ChunkedUploadOperation));
        await Task.Yield();
        using var trace = _access.Trace("uplink_upload_abort", ("key", ObjectName));
        var errPtr = UplinkInterop.uplink_upload_abort(_uploadHandle);
        if (errPtr != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeError(errPtr);
            trace?.NativeError(msg, code);
            Failed       = true;
            ErrorMessage = msg;
            return false;
        }

        trace?.Success();
        ReleaseHandle();
        return true;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (_uploadHandle != nint.Zero)
        {
            if (!_committed)
            {
                using var trace = _access.Trace("uplink_upload_abort", ("key", ObjectName));
                var errPtr = UplinkInterop.uplink_upload_abort(_uploadHandle);
                if (errPtr != nint.Zero)
                {
                    var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                    trace?.NativeError(msg, code);
                    Failed       = true;
                    ErrorMessage = msg;
                }
                else
                {
                    trace?.Success();
                }
            }

            ReleaseHandle();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void ReleaseHandle()
    {
        if (_uploadHandle == nint.Zero)
            return;

        UplinkInterop.FreeUploadHandle(_uploadHandle);
        _uploadHandle = nint.Zero;
        _projectLease?.Dispose();
        _projectLease = null;
    }
}
