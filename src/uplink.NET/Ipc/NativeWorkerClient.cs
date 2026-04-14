using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace uplink.NET.Ipc;

/// <summary>Result of an IPC call to the native worker process.</summary>
internal readonly struct IpcResult
{
    public JsonElement Data { get; init; }
    public string? ErrorMessage { get; init; }
    public int ErrorCode { get; init; }
    public bool IsError => ErrorMessage != null;
}

/// <summary>
/// Manages the native worker child process and provides serialized IPC communication.
/// Calls are serialized via SemaphoreSlim(1,1) so the single-threaded worker handles one at a time.
/// </summary>
internal sealed class NativeWorkerClient : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Process? _process;
    private Stream? _stdin;
    private Stream? _stdout;
    private int _nextId;
    private bool _disposed;

    internal void EnsureStarted()
    {
        if (_process != null)
        {
            // If the worker exited unexpectedly, clean up so we can restart it.
            try
            {
                if (!_process.HasExited)
                    return;
            }
            catch
            {
                // HasExited can throw if the process handle is invalid; treat as exited.
            }

            try { _stdin?.Close(); } catch { }
            try { _stdout?.Close(); } catch { }
            try { _process.Dispose(); } catch { }
            _stdin   = null;
            _stdout  = null;
            _process = null;
        }

        var assemblyDir = Path.GetDirectoryName(typeof(NativeWorkerClient).Assembly.Location)!;
        var workerDll = Path.Combine(assemblyDir, "uplink.NET.Worker.dll");

        if (!File.Exists(workerDll))
            throw new FileNotFoundException(
                $"Native worker DLL not found at '{workerDll}'. " +
                "Ensure the uplink.NET.Worker project is built and its output is copied next to uplink.NET.dll.",
                workerDll);

        var dotnetExe = FindDotnet();

        var psi = new ProcessStartInfo
        {
            FileName               = dotnetExe,
            Arguments              = $"exec \"{workerDll}\"",
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = false,
            CreateNoWindow         = true
        };

        _process = new Process { StartInfo = psi };
        _process.Start();
        _stdin  = _process.StandardInput.BaseStream;
        _stdout = _process.StandardOutput.BaseStream;
    }

    internal async Task<IpcResult> SendAsync(
        Dictionary<string, object?> request,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureStarted();

            int id = Interlocked.Increment(ref _nextId);
            request["id"] = id;

            // Serialize request
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request);
            var lenBytes = BitConverter.GetBytes(requestBytes.Length);

            // Write length-prefixed JSON to worker stdin
            await _stdin!.WriteAsync(lenBytes.AsMemory(), ct).ConfigureAwait(false);
            await _stdin.WriteAsync(requestBytes.AsMemory(), ct).ConfigureAwait(false);
            await _stdin.FlushAsync(ct).ConfigureAwait(false);

            // Read 4-byte length prefix
            var lenBuf = new byte[4];
            await ReadExactAsync(_stdout!, lenBuf, ct).ConfigureAwait(false);
            int responseLength = BitConverter.ToInt32(lenBuf, 0);

            // Guard against corrupted / malicious length values (max 100 MB)
            const int MaxResponseBytes = 100 * 1024 * 1024;
            if (responseLength <= 0 || responseLength > MaxResponseBytes)
                throw new InvalidDataException(
                    $"Worker returned an invalid response length: {responseLength}. " +
                    $"Expected a value between 1 and {MaxResponseBytes} bytes.");

            // Read response body
            var responseBuf = new byte[responseLength];
            await ReadExactAsync(_stdout!, responseBuf, ct).ConfigureAwait(false);

            // Parse response
            using var doc = JsonDocument.Parse(responseBuf);
            var root = doc.RootElement;

            if (root.TryGetProperty("err", out var errElem) && errElem.ValueKind != JsonValueKind.Null)
            {
                var code = root.TryGetProperty("code", out var codeElem) ? codeElem.GetInt32() : 0;
                return new IpcResult { ErrorMessage = errElem.GetString() ?? "Unknown error", ErrorCode = code };
            }

            // Clone the element so it survives document disposal
            return new IpcResult { Data = root.Clone() };
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        int remaining = buffer.Length;
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, remaining), ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Worker process closed the connection unexpectedly.");
            offset += read;
            remaining -= read;
        }
    }

    private static string FindDotnet()
    {
        // If current process IS dotnet, reuse the same executable
        var currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        if (Path.GetFileNameWithoutExtension(currentExe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return currentExe;
        return "dotnet";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
        try { _stdin?.Close(); } catch { }
        // Give the worker a chance to flush and exit cleanly before forcing a kill.
        try
        {
            if (_process != null && !_process.WaitForExit(2000))
                _process.Kill();
        }
        catch { }
        _process?.Dispose();
    }
}

/// <summary>Singleton accessor for the shared worker client.</summary>
internal static class NativeWorkerProcess
{
    private static readonly NativeWorkerClient _client = new();
    internal static NativeWorkerClient Instance => _client;

    static NativeWorkerProcess()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _client.Dispose();
    }
}
