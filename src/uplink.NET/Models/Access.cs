using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using uplink.NET.Diagnostics;
using uplink.NET.Exceptions;
using uplink.NET.Native;

namespace uplink.NET.Models;

/// <summary>
/// Represents a parsed Storj access grant and an open project connection.
/// Dispose to release the underlying native resources.
/// </summary>
public class Access : IDisposable
{
    private static long _nextAccessId;

    internal UplinkAccessSafeHandle? _accessHandle;
    internal UplinkProjectSafeHandle? _projectHandle;

    private readonly Config? _config;
    private readonly UplinkDiagnosticsSession? _diagnostics;
    private readonly SemaphoreSlim _nativeOperationGate = new(1, 1);
    private readonly object _lifetimeSync = new();
    private readonly long _accessId = Interlocked.Increment(ref _nextAccessId);
    private long _nextOperationId;
    private int _activeProjectLeases;
    private bool _disposeRequested;
    private bool _disposed;

    /// <summary>Open a project using the supplied access-grant string.</summary>
    public Access(string accessGrant) : this(accessGrant, null) { }

    /// <summary>Open a project with an optional <see cref="Config"/>.</summary>
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

            _accessHandle = new UplinkAccessSafeHandle(accessResult.access);
            accessResult.access = nint.Zero;

            try
            {
                _projectHandle = OpenProjectHandle(_accessHandle.DangerousHandle, _config, _diagnostics);
                trace?.Success();
            }
            catch
            {
                _accessHandle.Dispose();
                _accessHandle = null;
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
        _diagnostics = CreateDiagnosticsSession(_config);
        _accessHandle = new UplinkAccessSafeHandle(accessHandle);

        using var trace = Trace("uplink_config_open_project");
        try
        {
            _projectHandle = OpenProjectHandle(_accessHandle.DangerousHandle, _config, _diagnostics);
            trace?.Success();
        }
        catch
        {
            _accessHandle.Dispose();
            _accessHandle = null;
            throw;
        }
    }

    /// <summary>Serialize this access grant so it can be stored or reused later.</summary>
    public string Serialize()
    {
        ThrowIfDisposed();

        using var trace = Trace("uplink_access_serialize");
        var result = UplinkInterop.uplink_access_serialize(GetAccessHandle());
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
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(permission);
        ArgumentNullException.ThrowIfNull(prefixes);

        var nativePermission = new UplinkInterop.UplinkPermission
        {
            allow_download = permission.AllowDownload ? (byte)1 : (byte)0,
            allow_upload = permission.AllowUpload ? (byte)1 : (byte)0,
            allow_list = permission.AllowList ? (byte)1 : (byte)0,
            allow_delete = permission.AllowDelete ? (byte)1 : (byte)0,
            not_before = UplinkInterop.DateTimeToUnix(permission.NotBefore),
            not_after = UplinkInterop.DateTimeToUnix(permission.NotAfter)
        };

        using var nativePrefixes = new UplinkInterop.MarshalledSharePrefixes(prefixes);
        using var trace = Trace("uplink_access_share", ("prefixCount", nativePrefixes.Count));
        var result = UplinkInterop.uplink_access_share(
            GetAccessHandle(),
            nativePermission,
            nativePrefixes.Pointer,
            nativePrefixes.Count);

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

    /// <summary>Revoke a child access grant that was derived from this access grant.</summary>
    public Task RevokeAsync(Access childAccess)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(childAccess);
        childAccess.ThrowIfDisposed();

        var projectLease = AcquireProjectLease();

        return Task.Run(() =>
        {
            using var trace = Trace("uplink_revoke_access");
            try
            {
                var errPtr = UplinkInterop.uplink_revoke_access(projectLease.Handle.DangerousHandle, childAccess.GetAccessHandle());
                if (errPtr != nint.Zero)
                {
                    var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref errPtr);
                    trace?.NativeError(msg, code);
                    throw new AccessException($"Failed to revoke access grant: {msg}");
                }

                trace?.Success();
            }
            finally
            {
                projectLease.Dispose();
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
            TempDirectory = config.TempDirectory,
            EnableDiagnostics = config.EnableDiagnostics,
            DiagnosticsLogFilePath = config.DiagnosticsLogFilePath,
            SerializeNativeOperations = config.SerializeNativeOperations
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

    private static UplinkProjectSafeHandle OpenProjectHandle(nint accessHandle, Config? config, UplinkDiagnosticsSession? diagnostics)
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

        var projectHandle = new UplinkProjectSafeHandle(projectResult.project);
        projectResult.project = nint.Zero;
        UplinkInterop.uplink_free_project_result(projectResult);
        trace?.Success();
        return projectHandle;
    }

    public string? DiagnosticsLogFilePath => _diagnostics?.LogFilePath;

    internal UplinkDiagnosticsSession.NativeCallTrace? Trace(string operation, params (string Key, object? Value)[] context)
    {
        if (_diagnostics == null)
            return null;

        var mergedContext = new (string Key, object? Value)[context.Length + 2];
        mergedContext[0] = ("accessId", _accessId);
        mergedContext[1] = ("operationId", Interlocked.Increment(ref _nextOperationId));
        Array.Copy(context, 0, mergedContext, 2, context.Length);
        return _diagnostics.Trace(operation, mergedContext);
    }

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

            if (_activeProjectLeases == 0)
                ReleaseHandlesNoLock();
        }
    }

    internal ProjectHandleLease AcquireProjectLease()
    {
        var serializeNativeOperations = _config?.SerializeNativeOperations ?? false;
        if (serializeNativeOperations)
            _nativeOperationGate.Wait();

        lock (_lifetimeSync)
        {
            try
            {
                ThrowIfDisposedNoLock();
                var leaseHandle = OpenProjectHandle(GetAccessHandleNoLock(), _config, _diagnostics);
                _activeProjectLeases++;
                return new ProjectHandleLease(this, leaseHandle, serializeNativeOperations ? _nativeOperationGate : null);
            }
            catch
            {
                if (serializeNativeOperations)
                    _nativeOperationGate.Release();

                throw;
            }
        }
    }

    private void ReleaseProjectLease(UplinkProjectSafeHandle? projectHandle)
    {
        lock (_lifetimeSync)
        {
            try
            {
                projectHandle?.Dispose();
            }
            finally
            {
                if (_activeProjectLeases > 0)
                    _activeProjectLeases--;

                if (_disposeRequested && _activeProjectLeases == 0)
                    ReleaseHandlesNoLock();
            }
        }
    }

    private void ReleaseHandlesNoLock()
    {
        if (_disposed)
            return;

        _projectHandle?.Dispose();
        _projectHandle = null;

        _accessHandle?.Dispose();
        _accessHandle = null;

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
    }

    private nint GetAccessHandle()
    {
        lock (_lifetimeSync)
        {
            return GetAccessHandleNoLock();
        }
    }

    private nint GetAccessHandleNoLock()
    {
        if (_accessHandle is null || _accessHandle.IsClosed || _accessHandle.IsInvalid)
            throw new ObjectDisposedException(nameof(Access));

        return _accessHandle.DangerousHandle;
    }

    internal sealed class ProjectHandleLease : IDisposable
    {
        private Access? _owner;
        private SemaphoreSlim? _gate;

        internal ProjectHandleLease(Access owner, UplinkProjectSafeHandle handle, SemaphoreSlim? gate)
        {
            _owner = owner;
            Handle = handle;
            _gate = gate;
        }

        internal UplinkProjectSafeHandle Handle { get; }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            try
            {
                owner?.ReleaseProjectLease(Handle);
            }
            finally
            {
                Interlocked.Exchange(ref _gate, null)?.Release();
            }
        }
    }
}
