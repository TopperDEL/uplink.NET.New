using System.Text;
using System.Text.Json;

namespace uplink.NET.Worker;

/// <summary>
/// Main loop: reads length-prefixed JSON from stdin, dispatches, writes response to stdout.
/// </summary>
internal sealed class WorkerHost
{
    private readonly OperationDispatcher _dispatcher = new();

    internal void Run()
    {
        // Use raw binary streams to avoid any text encoding interference.
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        var lenBuf = new byte[4];

        while (true)
        {
            if (!ReadExact(stdin, lenBuf, 0, 4))
                break; // EOF – parent process closed stdin

            int length = BitConverter.ToInt32(lenBuf, 0);
            if (length <= 0)
                break;

            var msgBuf = new byte[length];
            if (!ReadExact(stdin, msgBuf, 0, length))
                break;

            Dictionary<string, object?> response;
            int requestId = 0;
            try
            {
                using var doc = JsonDocument.Parse(msgBuf);
                var root = doc.RootElement;

                if (root.TryGetProperty("id", out var idElem))
                    requestId = idElem.GetInt32();

                var op = root.TryGetProperty("op", out var opElem)
                    ? opElem.GetString() ?? string.Empty
                    : string.Empty;

                response = _dispatcher.Dispatch(root, op);
            }
            catch (Exception ex)
            {
                response = new Dictionary<string, object?>
                {
                    ["err"] = ex.Message,
                    ["code"] = -1
                };
            }

            response["id"] = requestId;

            var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response);
            var responseLen = BitConverter.GetBytes(responseBytes.Length);
            stdout.Write(responseLen, 0, 4);
            stdout.Write(responseBytes, 0, responseBytes.Length);
            stdout.Flush();
        }
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        int remaining = count;
        while (remaining > 0)
        {
            int read = stream.Read(buffer, offset, remaining);
            if (read == 0) return false;
            offset += read;
            remaining -= read;
        }
        return true;
    }
}
