using System.Collections.Generic;
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
    internal nint _accessHandle;
    internal nint _projectHandle;

    private readonly Config? _config;
    private bool _disposed;

    /// <summary>Open a project using the supplied access-grant string.</summary>
    public Access(string accessGrant) : this(accessGrant, null) { }

    /// <summary>Open a project with an optional <see cref="Config"/>.</summary>
    public Access(string accessGrant, Config? config)
    {
        if (string.IsNullOrWhiteSpace(accessGrant))
            throw new ArgumentNullException(nameof(accessGrant));

        _config = CloneConfig(config);

        var accessResult = UplinkInterop.uplink_parse_access(accessGrant);
        try
        {
            if (accessResult.error != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref accessResult.error);
                throw new AccessException($"Failed to parse access grant: {msg}");
            }

            if (accessResult.access == nint.Zero)
            {
                throw new AccessException("Failed to parse access grant: native library returned a null access handle.");
            }

            _accessHandle = accessResult.access;
            accessResult.access = nint.Zero;

            try
            {
                _projectHandle = OpenProjectHandle(_accessHandle, _config);
            }
            catch
            {
                UplinkInterop.FreeAccessHandle(_accessHandle);
                _accessHandle = nint.Zero;
                throw;
            }
        }
        finally
        {
            UplinkInterop.uplink_free_access_result(accessResult);
        }
    }

    private Access(nint accessHandle, Config? config)
    {
        if (accessHandle == nint.Zero)
            throw new ArgumentException("Access handle must not be null.", nameof(accessHandle));

        _config = CloneConfig(config);
        _accessHandle = accessHandle;

        try
        {
            _projectHandle = OpenProjectHandle(_accessHandle, _config);
        }
        catch
        {
            UplinkInterop.FreeAccessHandle(_accessHandle);
            _accessHandle = nint.Zero;
            throw;
        }
    }

    /// <summary>Serialize this access grant so it can be stored or reused later.</summary>
    public string Serialize()
    {
        ThrowIfDisposed();

        var result = UplinkInterop.uplink_access_serialize(_accessHandle);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                throw new AccessException($"Failed to serialize access grant: {msg}");
            }

            if (result.stringValue == nint.Zero)
                throw new AccessException("Failed to serialize access grant: native library returned a null string.");

            return UplinkInterop.PtrToString(result.stringValue);
        }
        finally
        {
            UplinkInterop.uplink_free_string_result(result);
        }
    }

    /// <summary>Share this access grant with the supplied permission set and prefixes.</summary>
    public Access Share(Permission permission, params SharePrefix[] prefixes)
        => Share(permission, (IEnumerable<SharePrefix>)prefixes);

    /// <summary>Share this access grant with the supplied permission set and prefixes.</summary>
    public unsafe Access Share(Permission permission, IEnumerable<SharePrefix> prefixes)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(permission);
        ArgumentNullException.ThrowIfNull(prefixes);

        var sharePrefixes = prefixes.ToArray();
        var nativePrefixes = new UplinkInterop.UplinkSharePrefix[sharePrefixes.Length];

        for (var index = 0; index < sharePrefixes.Length; index++)
        {
            var sharePrefix = sharePrefixes[index] ?? throw new ArgumentException("Share prefixes must not contain null values.", nameof(prefixes));
            if (string.IsNullOrWhiteSpace(sharePrefix.Bucket))
                throw new ArgumentException("Share prefix bucket must not be null or whitespace.", nameof(prefixes));

            nativePrefixes[index] = new UplinkInterop.UplinkSharePrefix
            {
                bucket = Marshal.StringToCoTaskMemUTF8(sharePrefix.Bucket),
                prefix = Marshal.StringToCoTaskMemUTF8(sharePrefix.Prefix ?? string.Empty)
            };
        }

        var nativePermission = new UplinkInterop.UplinkPermission
        {
            allow_download = permission.AllowDownload ? (byte)1 : (byte)0,
            allow_upload = permission.AllowUpload ? (byte)1 : (byte)0,
            allow_list = permission.AllowList ? (byte)1 : (byte)0,
            allow_delete = permission.AllowDelete ? (byte)1 : (byte)0,
            not_before = UplinkInterop.DateTimeToUnix(permission.NotBefore),
            not_after = UplinkInterop.DateTimeToUnix(permission.NotAfter)
        };

        UplinkInterop.UplinkAccessResult result;
        try
        {
            if (nativePrefixes.Length == 0)
            {
                result = UplinkInterop.uplink_access_share(_accessHandle, nativePermission, null, 0);
            }
            else
            {
                fixed (UplinkInterop.UplinkSharePrefix* prefixesPtr = nativePrefixes)
                    result = UplinkInterop.uplink_access_share(_accessHandle, nativePermission, prefixesPtr, nativePrefixes.Length);
            }

            try
            {
                if (result.error != nint.Zero)
                {
                    var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                    throw new AccessException($"Failed to share access grant: {msg}");
                }

                if (result.access == nint.Zero)
                    throw new AccessException("Failed to share access grant: native library returned a null access handle.");

                var sharedAccessHandle = result.access;
                result.access = nint.Zero;
                return new Access(sharedAccessHandle, _config);
            }
            finally
            {
                UplinkInterop.uplink_free_access_result(result);
            }
        }
        finally
        {
            foreach (var nativePrefix in nativePrefixes)
            {
                if (nativePrefix.bucket != nint.Zero)
                    Marshal.FreeCoTaskMem(nativePrefix.bucket);
                if (nativePrefix.prefix != nint.Zero)
                    Marshal.FreeCoTaskMem(nativePrefix.prefix);
            }
        }
    }

    /// <summary>Revoke a child access grant that was derived from this access grant.</summary>
    public Task RevokeAsync(Access childAccess)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(childAccess);
        childAccess.ThrowIfDisposed();

        return Task.Run(() =>
        {
            var errPtr = UplinkInterop.uplink_revoke_access(_projectHandle, childAccess._accessHandle);
            if (errPtr != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref errPtr);
                throw new AccessException($"Failed to revoke access grant: {msg}");
            }
        });
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

        try
        {
            Directory.CreateDirectory(tempDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new AccessException($"Failed to prepare temp directory '{tempDirectory}' ({ex.GetType().Name}): {ex.Message}");
        }

        return tempDirectory;
    }

    private static void FreeNativeConfig(UplinkInterop.UplinkConfig cfg)
    {
        if (cfg.user_agent != nint.Zero)
            Marshal.FreeCoTaskMem(cfg.user_agent);
        if (cfg.temp_directory != nint.Zero)
            Marshal.FreeCoTaskMem(cfg.temp_directory);
    }

    private static Config? CloneConfig(Config? config)
    {
        if (config == null)
            return null;

        return new Config
        {
            UserAgent = config.UserAgent,
            DialTimeoutMilliseconds = config.DialTimeoutMilliseconds,
            TempDirectory = config.TempDirectory
        };
    }

    private static nint OpenProjectHandle(nint accessHandle, Config? config)
    {
        var nativeConfig = BuildNativeConfig(config);
        UplinkInterop.UplinkProjectResult projectResult;

        try
        {
            projectResult = UplinkInterop.uplink_config_open_project(nativeConfig, accessHandle);
        }
        finally
        {
            FreeNativeConfig(nativeConfig);
        }

        if (projectResult.error != nint.Zero)
        {
            var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref projectResult.error);
            UplinkInterop.uplink_free_project_result(projectResult);
            throw new AccessException($"Failed to open project: {msg}");
        }

        if (projectResult.project == nint.Zero)
        {
            UplinkInterop.uplink_free_project_result(projectResult);
            throw new AccessException("Failed to open project: native library returned a null project handle.");
        }

        var projectHandle = projectResult.project;
        projectResult.project = nint.Zero;
        UplinkInterop.uplink_free_project_result(projectResult);
        return projectHandle;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Access));
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (_projectHandle != nint.Zero)
        {
            UplinkInterop.CloseProjectHandle(_projectHandle);
            UplinkInterop.FreeProjectHandle(_projectHandle);
            _projectHandle = nint.Zero;
        }

        if (_accessHandle != nint.Zero)
        {
            UplinkInterop.FreeAccessHandle(_accessHandle);
            _accessHandle = nint.Zero;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~Access() => Dispose(false);
}
