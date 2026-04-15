using uplink.NET.Ipc;

namespace uplink.NET.Models;

/// <summary>
/// Represents a readable stream backed by a download handle in the native worker process.
/// </summary>
public class DownloadStream : Stream
{
    private const int MaxConsecutiveEmptyReads = 8;
    private static readonly TimeSpan EmptyReadRetryDelay = TimeSpan.FromMilliseconds(10);

    private readonly object _syncRoot = new();
    private readonly Access _access;

    private long _downloadId;
    private Access.ProjectHandleLease? _projectLease;
    private readonly long _length;
    private long _position;
    private bool _disposed;
    private bool _endOfStream;

    internal DownloadStream(long downloadId, long length, Access.ProjectHandleLease projectLease, Access access)
    {
        if (downloadId == 0)
            throw new ArgumentException("A valid download ID is required.", nameof(downloadId));
        ArgumentNullException.ThrowIfNull(projectLease);
        ArgumentNullException.ThrowIfNull(access);
        _access       = access;
        _downloadId   = downloadId;
        _projectLease = projectLease;
        _length       = Math.Max(0, length);
    }

    public override bool CanRead  => !_disposed;
    public override bool CanSeek  => false;
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

    public override void Flush() { }

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

            var consecutiveEmptyReads = 0;
            while (true)
            {
                var (data, eof, error) = ReadChunkFromWorker(_downloadId, buffer.Length);
                if (error != null)
                    throw new IOException($"Failed to read from Storj download stream: {error}");

                int bytesRead = data?.Length ?? 0;
                if (bytesRead > 0)
                {
                    data!.AsSpan(0, Math.Min(bytesRead, buffer.Length)).CopyTo(buffer);
                    _position += bytesRead;
                    return bytesRead;
                }

                if (eof || _position >= _length)
                {
                    _endOfStream = true;
                    return 0;
                }

                consecutiveEmptyReads++;
                if (consecutiveEmptyReads >= MaxConsecutiveEmptyReads)
                    throw new IOException("Storj download stream stalled after repeated empty reads before EOF.");

                Monitor.Exit(_syncRoot);
                try
                {
                    Thread.Sleep(EmptyReadRetryDelay);
                }
                finally
                {
                    Monitor.Enter(_syncRoot);
                    ThrowIfDisposed();
                }
            }
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
        byte[] buffer, int offset, int count,
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
            if (_disposed) return;
            _disposed = true;

            if (_downloadId != 0)
            {
                var id = _downloadId;
                _downloadId = 0;
                _ = Task.Run(async () =>
                {
                    // Fire-and-forget: if lost, the worker cleans up on stdin-close / process exit.
                    try
                    {
                        await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
                        {
                            ["op"]          = "download_close",
                            ["download_id"] = id
                        }).ConfigureAwait(false);
                    }
                    catch { }
                });
            }

            _projectLease?.Dispose();
            _projectLease = null;
        }

        base.Dispose(disposing);
    }

    ~DownloadStream() => Dispose(false);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static (byte[]? data, bool eof, string? error) ReadChunkFromWorker(long downloadId, int maxBytes)
    {
        try
        {
            var result = NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
            {
                ["op"]          = "download_read",
                ["download_id"] = downloadId,
                ["max_bytes"]   = maxBytes
            }).GetAwaiter().GetResult();

            if (result.IsError)
                return (null, false, result.ErrorMessage);

            var dataB64   = result.Data.TryGetProperty("data_b64",  out var db) ? db.GetString() ?? string.Empty : string.Empty;
            int bytesRead = result.Data.TryGetProperty("bytes_read", out var br) ? br.GetInt32() : 0;
            bool eof      = result.Data.TryGetProperty("eof",        out var ef) && ef.GetBoolean();

            if (bytesRead > 0 && !string.IsNullOrEmpty(dataB64))
                return (Convert.FromBase64String(dataB64), eof, null);

            return (Array.Empty<byte>(), eof, null);
        }
        catch (Exception ex)
        {
            return (null, false, ex.Message);
        }
    }
}
