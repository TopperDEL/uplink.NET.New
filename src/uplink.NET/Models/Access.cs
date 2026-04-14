using System.Collections.Generic;
using System.Runtime.InteropServices;
using uplink.NET.Diagnostics;
using uplink.NET.Exceptions;
using uplink.NET.Native;

namespace uplink.NET.Models;

/// <summary>
/// Represents a parsed Storj access grant.
/// Dispose to release the underlying native resources.
/// </summary>
public class Access : IDisposable
{
    internal nint _accessHandle;

    private readonly Config? _config;
    private readonly UplinkDiagnosticsSession? _diagnostics;
    private readonly object _lifetimeSync = new();
    private int _activeAccessLeases;
    private int _activeProjectLeases;
    private bool _disposeRequested;
    private bool _disposed;

    /// <summary>Parse the supplied access-grant string.</summary>
    public Access(string accessGrant) : this(accessGrant, null) { }

    /// <summary>Parse an access grant with an optional <see cref="Config"/>.</summary>
    public Access(string accessGrant, Config? config)
    {
        if (string.IsNullOrWhiteSpace(accessGrant))
            throw new ArgumentNullException(nameof(accessGrant));

        _config = CloneConfig(config);
        _diagnostics = CreateDiagnosticsSession(_config);

        using var trace = Trace("uplink_parse_access");
        var accessResult = UplinkInterop.uplink_parse_access(accessGrant);
        try
        {
            if (accessResult.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref accessResult.error);
                trace?.NativeError(msg, code);
                throw new AccessException($"Failed to parse access grant: {msg}");
            }

            if (accessResult.access == nint.Zero)
            {
                trace?.Fail("Native library returned a null access handle without an error.");
                throw new AccessException("Failed to parse access grant: native library returned a null access handle.");
            }

            _accessHandle = accessResult.access;
            accessResult.access = nint.Zero;

            trace?.Success();
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
        _diagnostics = CreateDiagnosticsSession(_config);
        _accessHandle = accessHandle;
    }

    /// <summary>Serialize this access grant so it can be stored or reused later.</summary>
    public string Serialize()
    {
        ThrowIfDisposed();

        using var trace = Trace("uplink_access_serialize");
        var result = UplinkInterop.uplink_access_serialize(_accessHandle);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                trace?.NativeError(msg, code);
                throw new AccessException($"Failed to serialize access grant: {msg}");
            }

            if (result.stringValue == nint.Zero)
            {
                trace?.Fail("Native library returned a null serialized access string without an error.");
                throw new AccessException("Failed to serialize access grant: native library returned a null string.");
            }

            trace?.Success();
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
        ArgumentNullException.ThrowIfNull(permission);
        ArgumentNullException.ThrowIfNull(prefixes);

        using var accessLease = AcquireAccessLease();
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

        using var trace = Trace("uplink_access_share", ("prefixCount", sharePrefixes.Length));
        UplinkInterop.UplinkAccessResult result;
        try
        {
            if (nativePrefixes.Length == 0)
            {
                result = UplinkInterop.uplink_access_share(accessLease.Handle, nativePermission, null, 0);
            }
            else
            {
                fixed (UplinkInterop.UplinkSharePrefix* prefixesPtr = nativePrefixes)
                    result = UplinkInterop.uplink_access_share(accessLease.Handle, nativePermission, prefixesPtr, checked((nint)nativePrefixes.Length));
            }

            try
            {
                if (result.error != nint.Zero)
                {
                    var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                    trace?.NativeError(msg, code);
                    throw new AccessException($"Failed to share access grant: {msg}");
                }

                if (result.access == nint.Zero)
                {
                    trace?.Fail("Native library returned a null shared access handle without an error.");
                    throw new AccessException("Failed to share access grant: native library returned a null access handle.");
                }

                var sharedAccessHandle = result.access;
                result.access = nint.Zero;
                trace?.Success();
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
    public async Task RevokeAsync(Access childAccess)
    {
        ArgumentNullException.ThrowIfNull(childAccess);
        using var childAccessLease = childAccess.AcquireAccessLease();
        using var projectLease = AcquireProjectLease();
        await Task.Run(() =>
        {
            using var trace = Trace("uplink_revoke_access");
            var errPtr = UplinkInterop.uplink_revoke_access(projectLease.Handle, childAccessLease.Handle);
            if (errPtr != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref errPtr);
                trace?.NativeError(msg, code);
                throw new AccessException($"Failed to revoke access grant: {msg}");
            }

            trace?.Success();
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
            TempDirectory = config.TempDirectory,
            EnableDiagnostics = config.EnableDiagnostics,
            DiagnosticsLogFilePath = config.DiagnosticsLogFilePath
        };
    }

    private static UplinkDiagnosticsSession? CreateDiagnosticsSession(Config? config)
    {
        try
        {
            return UplinkDiagnosticsSession.Create(config?.EnableDiagnostics ?? false, config?.DiagnosticsLogFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new AccessException($"Failed to initialize uplink.NET diagnostics. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static nint OpenProjectHandle(nint accessHandle, Config? config, UplinkDiagnosticsSession? diagnostics)
    {
        using var trace = diagnostics?.Trace(
            "uplink_config_open_project",
            ("dialTimeoutMilliseconds", config?.DialTimeoutMilliseconds ?? 0),
            ("tempDirectory", ResolveTempDirectory(config?.TempDirectory)));
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
            var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref projectResult.error);
            trace?.NativeError(msg, code);
            UplinkInterop.uplink_free_project_result(projectResult);
            throw new AccessException($"Failed to open project: {msg}");
        }

        if (projectResult.project == nint.Zero)
        {
            trace?.Fail("Native library returned a null project handle without an error.");
            UplinkInterop.uplink_free_project_result(projectResult);
            throw new AccessException("Failed to open project: native library returned a null project handle.");
        }

        var projectHandle = projectResult.project;
        projectResult.project = nint.Zero;
        UplinkInterop.uplink_free_project_result(projectResult);
        trace?.Success();
        return projectHandle;
    }

    public string? DiagnosticsLogFilePath => _diagnostics?.LogFilePath;

    internal UplinkDiagnosticsSession.NativeCallTrace? Trace(string operation, params (string Key, object? Value)[] context)
        => _diagnostics?.Trace(operation, context);

    private void ThrowIfDisposed()
    {
        lock (_lifetimeSync)
        {
            ThrowIfDisposedNoLock();
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        lock (_lifetimeSync)
        {
            if (_disposeRequested)
                return;

            _disposeRequested = true;

            if (_activeAccessLeases == 0 && _activeProjectLeases == 0)
                ReleaseHandlesNoLock();
        }
    }

    internal AccessHandleLease AcquireAccessLease()
    {
        lock (_lifetimeSync)
        {
            ThrowIfDisposedNoLock();
            _activeAccessLeases++;
            return new AccessHandleLease(this, _accessHandle);
        }
    }

    internal ProjectHandleLease AcquireProjectLease()
    {
        var accessLease = AcquireAccessLease();

        try
        {
            var projectHandle = OpenProjectHandle(accessLease.Handle, _config, _diagnostics);
            lock (_lifetimeSync)
            {
                _activeProjectLeases++;
            }

            return new ProjectHandleLease(this, projectHandle, accessLease);
        }
        catch
        {
            accessLease.Dispose();
            throw;
        }
    }

    private void ReleaseAccessLease()
    {
        lock (_lifetimeSync)
        {
            if (_activeAccessLeases > 0)
                _activeAccessLeases--;

            if (_disposeRequested && _activeAccessLeases == 0 && _activeProjectLeases == 0)
                ReleaseHandlesNoLock();
        }
    }

    private void ReleaseProjectLease(nint projectHandle)
    {
        lock (_lifetimeSync)
        {
            if (_activeProjectLeases == 0)
                throw new InvalidOperationException("Project lease released without an active lease.");

            _activeProjectLeases--;
        }

        if (projectHandle != nint.Zero)
            UplinkInterop.FreeProjectHandle(projectHandle);
    }

    private void ReleaseHandlesNoLock()
    {
        if (_disposed)
            return;

        if (_accessHandle != nint.Zero)
        {
            UplinkInterop.FreeAccessHandle(_accessHandle);
            _accessHandle = nint.Zero;
        }

        _disposed = true;
    }

    private void ThrowIfDisposedNoLock()
    {
        if (_disposeRequested || _disposed)
            throw new ObjectDisposedException(nameof(Access));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~Access() => Dispose(false);

    internal sealed class ProjectHandleLease : IDisposable
    {
        private Access? _owner;
        private AccessHandleLease? _accessLease;

        internal ProjectHandleLease(Access owner, nint handle, AccessHandleLease accessLease)
        {
            _owner = owner;
            _accessLease = accessLease;
            Handle = handle;
        }

        internal nint Handle { get; }

        public void Dispose()
        {
            var owner = System.Threading.Interlocked.Exchange(ref _owner, null);
            var accessLease = System.Threading.Interlocked.Exchange(ref _accessLease, null);
            try
            {
                owner?.ReleaseProjectLease(Handle);
            }
            finally
            {
                accessLease?.Dispose();
            }
        }
    }

    internal sealed class AccessHandleLease : IDisposable
    {
        private Access? _owner;

        internal AccessHandleLease(Access owner, nint handle)
        {
            _owner = owner;
            Handle = handle;
        }

        internal nint Handle { get; }

        public void Dispose()
        {
            var owner = System.Threading.Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseAccessLease();
        }
    }
}
