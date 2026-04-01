using uplink.NET.Native;

namespace uplink.NET.Models;

public delegate void DownloadOperationProgressChanged(DownloadOperation downloadOperation);
public delegate void DownloadOperationEnded(DownloadOperation downloadOperation);

/// <summary>
/// Represents an in-progress download from Storj.
/// Call <see cref="StartDownloadAsync"/> to begin the transfer.
/// </summary>
public class DownloadOperation : IDisposable
{
    private const int ChunkSize = 80 * 1024; // 80 KB

    private readonly nint _projectHandle;
    private readonly string _bucketName;
    private readonly DownloadOptions _options;

    private bool _cancelRequested;

    public string ObjectName { get; }
    public byte[] DownloadedBytes { get; private set; } = Array.Empty<byte>();
    public long BytesReceived { get; private set; }
    public long TotalBytes { get; private set; }
    public bool Completed { get; private set; }
    public bool Failed { get; set; }
    public bool Cancelled { get; set; }
    public bool Running { get; set; }
    public string? ErrorMessage { get; private set; }

    public float PercentageCompleted =>
        TotalBytes > 0 ? (float)BytesReceived / TotalBytes * 100f : 0f;

    public event DownloadOperationProgressChanged? DownloadOperationProgressChanged;
    public event DownloadOperationEnded? DownloadOperationEnded;

    internal DownloadOperation(
        nint projectHandle,
        string bucketName,
        string objectName,
        DownloadOptions options)
    {
        _projectHandle = projectHandle;
        _bucketName    = bucketName;
        ObjectName     = objectName;
        _options       = options;
    }

    public Task? StartDownloadAsync() => Task.Run(PerformDownloadAsync);

    private async Task PerformDownloadAsync()
    {
        Running       = true;
        BytesReceived = 0;

        // Open download outside unsafe/async boundary
        var (downloadHandle, beginError) = BeginNativeDownload();
        if (beginError != null)
        {
            SetFailed(beginError);
            return;
        }

        // Determine total bytes
        TotalBytes = GetTotalBytes(downloadHandle);

        try
        {
            using var ms = new System.IO.MemoryStream();
            var chunkBuf = new byte[ChunkSize];

            while (!_cancelRequested)
            {
                var (bytesRead, eof, readError) = ReadChunk(downloadHandle, chunkBuf);

                if (bytesRead > 0)
                {
                    ms.Write(chunkBuf, 0, (int)bytesRead);
                    BytesReceived += bytesRead;
                    DownloadOperationProgressChanged?.Invoke(this);
                    await Task.Yield();
                }

                if (readError != null)
                {
                    SetFailed(readError);
                    return;
                }

                if (eof || bytesRead == 0)
                    break;
            }

            if (_cancelRequested)
            {
                Cancelled = true;
                Running   = false;
                DownloadOperationEnded?.Invoke(this);
                return;
            }

            DownloadedBytes = ms.ToArray();
            Completed = true;
            Running   = false;
            DownloadOperationEnded?.Invoke(this);
        }
        catch (Exception ex)
        {
            SetFailed(ex.Message);
        }
        finally
        {
            UplinkInterop.CloseDownloadHandle(downloadHandle);
            UplinkInterop.FreeDownloadHandle(downloadHandle);
        }
    }

    private unsafe (nint handle, string? error) BeginNativeDownload()
    {
        var opts = new UplinkInterop.UplinkDownloadOptions
        {
            offset = _options.Offset,
            length = _options.Length
        };
        var result = UplinkInterop.uplink_download_object(
            _projectHandle, _bucketName, ObjectName, &opts);
        if (result.error != nint.Zero)
        {
            var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
            UplinkInterop.uplink_free_download_result(result);
            return (nint.Zero, msg);
        }
        return (result.download, null);
    }

    private static long GetTotalBytes(nint handle)
    {
        var infoResult = UplinkInterop.uplink_download_info(handle);
        long total = 0;
        if (infoResult.error == nint.Zero && infoResult.object_ != nint.Zero)
            total = UplinkInterop.MarshalObject(infoResult.object_).ContentLength;
        UplinkInterop.uplink_free_object_result(infoResult);
        return total;
    }

    private static unsafe (uint bytesRead, bool eof, string? error) ReadChunk(
        nint handle, byte[] buffer)
    {
        UplinkInterop.UplinkReadResult readResult;
        fixed (byte* bufPtr = buffer)
        {
            readResult = UplinkInterop.uplink_download_read(
                handle, bufPtr, (nuint)buffer.Length);
        }

        uint bytesRead = (uint)(nuint)readResult.bytes_read;
        if (readResult.error != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeError(readResult.error);
            bool isEof = code == UplinkInterop.EndOfFileErrorCode
                || msg.Contains("EOF", StringComparison.OrdinalIgnoreCase);
            return (bytesRead, isEof, isEof ? null : msg);
        }
        return (bytesRead, bytesRead == 0, null);
    }
    private void SetFailed(string message)
    {
        Failed       = true;
        Running      = false;
        ErrorMessage = message;
        DownloadOperationEnded?.Invoke(this);
    }

    public void Cancel() => _cancelRequested = true;

    protected virtual void Dispose(bool disposing) { }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
