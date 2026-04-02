using System.IO;
using uplink.NET.Native;

namespace uplink.NET.Models;

/// <summary>
/// Represents a readable stream backed by a native Storj download handle.
/// </summary>
public class DownloadStream : Stream
{
    private readonly object _syncRoot = new();
    private readonly Access _access;

    private UplinkDownloadSafeHandle? _downloadHandle;
    private Access.ProjectHandleLease? _projectLease;
    private readonly long _length;
    private long _position;
    private bool _disposed;
    private bool _endOfStream;

    internal DownloadStream(UplinkDownloadSafeHandle downloadHandle, long length, Access.ProjectHandleLease projectLease, Access access)
    {
        ArgumentNullException.ThrowIfNull(downloadHandle);
        ArgumentNullException.ThrowIfNull(projectLease);
        ArgumentNullException.ThrowIfNull(access);

        if (downloadHandle.IsInvalid)
            throw new ArgumentException("A valid native download handle is required.", nameof(downloadHandle));

        _access = access;
        _downloadHandle = downloadHandle;
        _projectLease = projectLease;
        _length = Math.Max(0, length);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                return _length;
            }
        }
    }

    public override long Position
    {
        get
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                return _position;
            }
        }
        set => throw new NotSupportedException("Seeking is not supported.");
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (buffer.Length - offset < count)
            throw new ArgumentException("The buffer is too small for the requested offset and count.", nameof(buffer));

        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
            return 0;

        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (_endOfStream)
                return 0;

            var handle = _downloadHandle ?? throw new ObjectDisposedException(nameof(DownloadStream));
            var (bytesRead, eof, error) = ReadChunk(handle, buffer);
            if (error != null)
                throw new IOException($"Failed to read from Storj download stream: {error}");

            if (bytesRead > 0)
                _position += bytesRead;

            if (eof || bytesRead == 0)
                _endOfStream = true;

            return bytesRead;
        }
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<int>(cancellationToken);

        try
        {
            return ValueTask.FromResult(Read(buffer.Span));
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<int>(ex);
        }
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
        => ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException("Seeking is not supported.");

    public override void SetLength(long value)
        => throw new NotSupportedException("Writing is not supported.");

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("Writing is not supported.");

    protected override void Dispose(bool disposing)
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            _disposed = true;
            _downloadHandle?.Dispose();
            _downloadHandle = null;
            _projectLease?.Dispose();
            _projectLease = null;
        }

        base.Dispose(disposing);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private unsafe (int bytesRead, bool eof, string? error) ReadChunk(
        UplinkDownloadSafeHandle handle,
        Span<byte> buffer)
    {
        using var trace = _access.Trace(
            "uplink_download_read",
            ("downloadHandle", handle.DangerousHandle),
            ("bufferLength", buffer.Length));
        UplinkInterop.UplinkReadResult readResult;
        fixed (byte* bufPtr = buffer)
        {
            readResult = UplinkInterop.uplink_download_read(
                handle.DangerousHandle,
                bufPtr,
                (nuint)buffer.Length);
        }

        try
        {
            var bytesRead = (int)(nuint)readResult.bytes_read;
            if (readResult.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref readResult.error);
                var isEof = code == UplinkInterop.EndOfFileErrorCode
                    || msg.Contains("EOF", StringComparison.OrdinalIgnoreCase);
                if (isEof)
                    trace?.Success("Reached EOF.");
                else
                    trace?.NativeError(msg, code);
                return (bytesRead, isEof, isEof ? null : msg);
            }

            trace?.Success();
            return (bytesRead, bytesRead == 0, null);
        }
        finally
        {
            UplinkInterop.uplink_free_read_result(readResult);
        }
    }
}
