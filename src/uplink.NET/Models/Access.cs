using System.Runtime.InteropServices;
using uplink.NET.Exceptions;
using uplink.NET.Native;

namespace uplink.NET.Models;

/// <summary>
/// Represents a parsed Storj access grant and an open project connection.
/// Dispose to release the underlying native resources.
/// </summary>
public class Access : IDisposable
{
    internal UplinkInterop.UplinkHandle _projectHandle;
    internal UplinkInterop.UplinkHandle _accessHandle;

    private bool _disposed;

    /// <summary>Open a project using the supplied access-grant string.</summary>
    public Access(string accessGrant) : this(accessGrant, null) { }

    /// <summary>Open a project with an optional <see cref="Config"/>.</summary>
    public Access(string accessGrant, Config? config)
    {
        if (string.IsNullOrWhiteSpace(accessGrant))
            throw new ArgumentNullException(nameof(accessGrant));

        var accessResult = UplinkInterop.uplink_parse_access(accessGrant);
        try
        {
            if (accessResult.error != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeError(accessResult.error);
                throw new AccessException($"Failed to parse access grant: {msg}");
            }

            if (accessResult.access == nint.Zero)
            {
                throw new AccessException("Failed to parse access grant: native library returned a null access handle.");
            }

            _accessHandle = new UplinkInterop.UplinkHandle
            {
                _handle = (ulong)accessResult.access
            };

            var nativeConfig = BuildNativeConfig(config);
            UplinkInterop.UplinkProjectResult projectResult;

            try
            {
                projectResult = UplinkInterop.uplink_config_open_project(nativeConfig, _accessHandle);
            }
            finally
            {
                FreeNativeConfig(nativeConfig);
            }

            if (projectResult.error != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeError(projectResult.error);
                throw new AccessException($"Failed to open project: {msg}");
            }

            _projectHandle = projectResult.project;
        }
        finally
        {
            UplinkInterop.uplink_free_access_result(accessResult);
        }
    }

    private static UplinkInterop.UplinkConfig BuildNativeConfig(Config? config)
    {
        string userAgent = config?.UserAgent ?? string.Empty;
        string tempDirectory = ResolveTempDirectory(config?.TempDirectory);

        return new UplinkInterop.UplinkConfig
        {
            user_agent = Marshal.StringToCoTaskMemUTF8(userAgent),
            dial_timeout_milliseconds = config?.DialTimeoutMilliseconds ?? 0,
            temp_directory = Marshal.StringToCoTaskMemUTF8(tempDirectory)
        };
    }

    private static string ResolveTempDirectory(string? tempDirectory)
    {
        if (string.IsNullOrWhiteSpace(tempDirectory))
            return Path.GetTempPath();

        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }

    private static void FreeNativeConfig(UplinkInterop.UplinkConfig cfg)
    {
        if (cfg.user_agent != nint.Zero)
            Marshal.FreeCoTaskMem(cfg.user_agent);
        if (cfg.temp_directory != nint.Zero)
            Marshal.FreeCoTaskMem(cfg.temp_directory);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (_projectHandle._handle != 0)
        {
            var errPtr = UplinkInterop.uplink_close_project(_projectHandle);
            if (errPtr != nint.Zero)
                UplinkInterop.uplink_free_error(errPtr);
            _projectHandle = default;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~Access() => Dispose(false);
}
