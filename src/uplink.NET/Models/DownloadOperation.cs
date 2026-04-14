using uplink.NET.Ipc;

namespace uplink.NET.Models;

public delegate void DownloadOperationProgressChanged(DownloadOperation downloadOperation);
public delegate void DownloadOperationEnded(DownloadOperation downloadOperation);

/// <summary>
/// Represents an in-progress download from Storj.
/// Call <see cref="StartDownloadAsync"/> to begin the transfer.
/// </summary>
public class DownloadOperation : IDisposable
{
    private const int ChunkMaxBytes = 80 * 1024;

    private readonly Access _access;
    private readonly string _bucketName;
    private readonly DownloadOptions _options;
    private readonly object _startSync = new();

    private bool _cancelRequested;
    private Access.ProjectHandleLease? _projectLease;

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
        Access access,
        string bucketName,
        string objectName,
        DownloadOptions options)
    {
        _access     = access;
        _bucketName = bucketName;
        ObjectName  = objectName;
        _options    = options;
    }

    public Task? StartDownloadAsync()
    {
        lock (_startSync)
        {
            if (_projectLease != null)
                throw new InvalidOperationException("The download operation has already been started.");

            var projectLease = _access.AcquireProjectLease();

            try
            {
                _projectLease = projectLease;
                return Task.Run(PerformDownloadAsync);
            }
            catch
            {
                projectLease.Dispose();
                _projectLease = null;
                throw;
            }
        }
    }

    private async Task PerformDownloadAsync()
    {
        Running       = true;
        BytesReceived = 0;

        try
        {
            // Begin download
            var beginResult = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
            {
                ["op"]         = "download_begin",
                ["project_id"] = _projectLease!.Handle,
                ["bucket"]     = _bucketName,
                ["key"]        = ObjectName,
                ["offset"]     = _options.Offset,
                ["length"]     = _options.Length
            }).ConfigureAwait(false);

            if (beginResult.IsError)
            {
                SetFailed(beginResult.ErrorMessage!);
                return;
            }

            long downloadId = beginResult.Data.GetProperty("download_id").GetInt64();
            TotalBytes      = beginResult.Data.GetProperty("total_bytes").GetInt64();

            try
            {
                using var ms = new MemoryStream();

                while (!_cancelRequested)
                {
                    var readResult = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
                    {
                        ["op"]          = "download_read",
                        ["download_id"] = downloadId,
                        ["max_bytes"]   = ChunkMaxBytes
                    }).ConfigureAwait(false);

                    if (readResult.IsError)
                    {
                        SetFailed(readResult.ErrorMessage!);
                        return;
                    }

                    var dataB64   = readResult.Data.TryGetProperty("data_b64", out var db) ? db.GetString() ?? string.Empty : string.Empty;
                    int bytesRead = readResult.Data.TryGetProperty("bytes_read", out var br) ? br.GetInt32() : 0;
                    bool eof      = readResult.Data.TryGetProperty("eof",        out var ef) && ef.GetBoolean();

                    if (bytesRead > 0 && !string.IsNullOrEmpty(dataB64))
                    {
                        var chunk = Convert.FromBase64String(dataB64);
                        ms.Write(chunk, 0, bytesRead);
                        BytesReceived += bytesRead;
                        DownloadOperationProgressChanged?.Invoke(this);
                        await Task.Yield();
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
            finally
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
                        {
                            ["op"]          = "download_close",
                            ["download_id"] = downloadId
                        }).ConfigureAwait(false);
                    }
                    catch { }
                });
            }
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
