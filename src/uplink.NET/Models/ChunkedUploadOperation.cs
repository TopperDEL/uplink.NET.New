using uplink.NET.Native;

namespace uplink.NET.Models;

/// <summary>
/// Allows uploading large objects in sequential chunks without buffering the
/// entire payload in memory.
/// </summary>
public class ChunkedUploadOperation : IDisposable
{
    private readonly Access _access;
    private UplinkUploadSafeHandle? _uploadHandle;
    private Access.ProjectHandleLease? _projectLease;
    private bool _committed;
    private bool _disposed;

    public string ObjectName { get; }
    public bool Failed { get; private set; }
    public string? ErrorMessage { get; private set; }

    internal ChunkedUploadOperation(UplinkUploadSafeHandle uploadHandle, string objectName, Access.ProjectHandleLease projectLease, Access access)
    {
        ArgumentNullException.ThrowIfNull(uploadHandle);
        ArgumentNullException.ThrowIfNull(projectLease);
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _uploadHandle = uploadHandle;
        ObjectName = objectName;
        _projectLease = projectLease;
    }

    /// <summary>Writes the supplied bytes to the upload stream.</summary>
    public async Task<bool> UploadChunkAsync(byte[] chunk)
    {
        if (chunk is null || chunk.Length == 0)
            return true;

        if (_disposed)
            throw new ObjectDisposedException(nameof(ChunkedUploadOperation));

        await Task.Yield();

        var handle = _uploadHandle ?? throw new ObjectDisposedException(nameof(ChunkedUploadOperation));
        unsafe
        {
            using var trace = _access.Trace(
                "uplink_upload_write",
                ("key", ObjectName),
                ("uploadHandle", handle.DangerousHandle),
                ("count", chunk.Length));
            var writeResult = UplinkInterop.WithPinnedBuffer(
                chunk,
                0,
                chunk.Length,
                (ptr, len) => UplinkInterop.uplink_upload_write(handle.DangerousHandle, (void*)ptr, len));
            try
            {
                if (writeResult.error != nint.Zero)
                {
                    var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref writeResult.error);
                    trace?.NativeError(msg, code);
                    Failed = true;
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
        if (_committed)
            return true;

        if (_disposed)
            throw new ObjectDisposedException(nameof(ChunkedUploadOperation));

        await Task.Yield();

        var handle = _uploadHandle ?? throw new ObjectDisposedException(nameof(ChunkedUploadOperation));
        using var trace = _access.Trace(
            "uplink_upload_commit",
            ("key", ObjectName),
            ("uploadHandle", handle.DangerousHandle));
        var errPtr = UplinkInterop.uplink_upload_commit(handle.DangerousHandle);
        if (errPtr != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeError(errPtr);
            trace?.NativeError(msg, code);
            Failed = true;
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

        var handle = _uploadHandle ?? throw new ObjectDisposedException(nameof(ChunkedUploadOperation));
        using var trace = _access.Trace(
            "uplink_upload_abort",
            ("key", ObjectName),
            ("uploadHandle", handle.DangerousHandle));
        var errPtr = UplinkInterop.uplink_upload_abort(handle.DangerousHandle);
        if (errPtr != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeError(errPtr);
            trace?.NativeError(msg, code);
            Failed = true;
            ErrorMessage = msg;
            return false;
        }

        trace?.Success();
        ReleaseHandle();
        return true;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;

        var handle = _uploadHandle;
        if (handle != null && !handle.IsClosed && !handle.IsInvalid && !_committed)
        {
            using var trace = _access.Trace(
                "uplink_upload_abort",
                ("key", ObjectName),
                ("uploadHandle", handle.DangerousHandle));
            var errPtr = UplinkInterop.uplink_upload_abort(handle.DangerousHandle);
            if (errPtr != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                trace?.NativeError(msg, code);
                Failed = true;
                ErrorMessage = msg;
            }
            else
            {
                trace?.Success();
            }
        }

        ReleaseHandle();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void ReleaseHandle()
    {
        _uploadHandle?.Dispose();
        _uploadHandle = null;
        _projectLease?.Dispose();
        _projectLease = null;
    }
}
