using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace uplink.NET.Diagnostics;

internal sealed class UplinkDiagnosticsSession
{
    private readonly object _sync = new();
    private bool _headerWritten;

    private UplinkDiagnosticsSession(string logFilePath)
    {
        LogFilePath = logFilePath;
    }

    internal string? LogFilePath { get; }

    internal static UplinkDiagnosticsSession? Create(bool enabled, string? logFilePath)
    {
        if (!enabled)
            return null;

        var resolvedPath = string.IsNullOrWhiteSpace(logFilePath)
            ? Path.Combine(Path.GetTempPath(), $"uplink.net-diagnostics-{Environment.ProcessId}.log")
            : Path.GetFullPath(logFilePath);

        return new UplinkDiagnosticsSession(resolvedPath);
    }

    internal NativeCallTrace Trace(string operation, params (string Key, object? Value)[] context)
        => new(this, operation, context);

    private void WriteHeaderIfNeeded()
    {
        if (_headerWritten)
            return;

        try
        {
            var directory = Path.GetDirectoryName(LogFilePath!);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.AppendAllText(
                LogFilePath!,
                $"# uplink.NET diagnostics {DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} runtime=\"{Escape(Uplink.GetRuntimeInfo())}\"{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Failed to initialize uplink.NET diagnostics log '{LogFilePath}'. {ex.GetType().Name}: {ex.Message}",
                ex);
        }

        _headerWritten = true;
    }

    private void AppendLine(
        string operation,
        string stage,
        string context,
        string? message,
        int? errorCode,
        TimeSpan? duration)
    {
        lock (_sync)
        {
            WriteHeaderIfNeeded();

            var builder = new StringBuilder();
            builder.Append(DateTimeOffset.UtcNow.ToString("O"));
            builder.Append(" pid=").Append(Environment.ProcessId);
            builder.Append(" tid=").Append(Environment.CurrentManagedThreadId);
            builder.Append(" op=").Append(operation);
            builder.Append(" stage=").Append(stage);

            if (duration.HasValue)
                builder.Append(" durationMs=").Append(duration.Value.TotalMilliseconds.ToString("0.000###", System.Globalization.CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(context))
                builder.Append(" context=\"").Append(Escape(context)).Append('"');

            if (errorCode.HasValue)
                builder.Append(" code=").Append(errorCode.Value);

            if (!string.IsNullOrWhiteSpace(message))
                builder.Append(" message=\"").Append(Escape(message)).Append('"');

            builder.Append(Environment.NewLine);

            try
            {
                File.AppendAllText(LogFilePath!, builder.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                throw new InvalidOperationException(
                    $"Failed to write uplink.NET diagnostics log '{LogFilePath}'. {ex.GetType().Name}: {ex.Message}",
                    ex);
            }
        }
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    internal sealed class NativeCallTrace : IDisposable
    {
        private readonly UplinkDiagnosticsSession _session;
        private readonly string _operation;
        private readonly string _context;
        private readonly Stopwatch _stopwatch;
        private bool _completed;

        internal NativeCallTrace(UplinkDiagnosticsSession session, string operation, params (string Key, object? Value)[] context)
        {
            _session = session;
            _operation = operation;
            _context = string.Join(
                ", ",
                context
                    .Where(entry => entry.Value != null)
                    .Select(entry => $"{entry.Key}={Format(entry.Value!)}"));
            _stopwatch = Stopwatch.StartNew();

            _session.AppendLine(_operation, "start", _context, null, null, null);
        }

        internal void Success(string? message = null)
        {
            Complete("success", message, null);
        }

        internal void NativeError(string message, int code)
        {
            Complete("native-error", message, code);
        }

        internal void Fail(string message)
        {
            Complete("failure", message, null);
        }

        internal void ManagedException(Exception exception)
        {
            Complete("managed-exception", $"{exception.GetType().Name}: {exception.Message}", null);
        }

        public void Dispose()
        {
            if (_completed)
                return;

            Success();
        }

        private void Complete(string stage, string? message, int? code)
        {
            if (_completed)
                return;

            _completed = true;
            _stopwatch.Stop();
            _session.AppendLine(_operation, stage, _context, message, code, _stopwatch.Elapsed);
        }

        private static string Format(object value)
        {
            return value switch
            {
                null => string.Empty,
                string text => text,
                Enum enumValue => enumValue.ToString(),
                IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty
            };
        }
    }
}
