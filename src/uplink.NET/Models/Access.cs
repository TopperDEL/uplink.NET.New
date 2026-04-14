using uplink.NET.Diagnostics;
using uplink.NET.Exceptions;
using uplink.NET.Ipc;
using uplink.NET.Native;

namespace uplink.NET.Models;

/// <summary>
/// Represents a parsed Storj access grant and an open project connection.
/// Dispose to release the underlying native resources in the worker process.
/// </summary>
public class Access : IDisposable
{
    internal long _accessId;
    internal long _projectId;

    private readonly Config? _config;
    private readonly UplinkDiagnosticsSession? _diagnostics;
    private readonly object _lifetimeSync = new();
    private int _activeAccessLeases;
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

        _config      = CloneConfig(config);
        _diagnostics = CreateDiagnosticsSession(_config);

        var tempDir = ResolveTempDirectory(_config?.TempDirectory);

        var result = NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]              = "parse_access",
            ["serialized"]      = accessGrant,
            ["user_agent"]      = _config?.UserAgent ?? string.Empty,
            ["dial_timeout_ms"] = _config?.DialTimeoutMilliseconds ?? 0,
            ["temp_dir"]        = tempDir
        }).GetAwaiter().GetResult();

        if (result.IsError)
            throw new AccessException($"Failed to parse access grant: {result.ErrorMessage}");

        _accessId  = result.Data.GetProperty("access_id").GetInt64();
        _projectId = result.Data.GetProperty("project_id").GetInt64();
    }

    private Access(long accessId, long projectId, Config? config)
    {
        _config      = CloneConfig(config);
        _diagnostics = CreateDiagnosticsSession(_config);
        _accessId    = accessId;
        _projectId   = projectId;
    }

    /// <summary>Serialize this access grant so it can be stored or reused later.</summary>
    public string Serialize()
    {
        ThrowIfDisposed();

        var result = NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]        = "access_serialize",
            ["access_id"] = _accessId
        }).GetAwaiter().GetResult();

        if (result.IsError)
            throw new AccessException($"Failed to serialize access grant: {result.ErrorMessage}");

        return result.Data.GetProperty("serialized").GetString() ?? string.Empty;
    }

    /// <summary>Share this access grant with the supplied permission set and prefixes.</summary>
    public Access Share(Permission permission, params SharePrefix[] prefixes)
        => Share(permission, (IEnumerable<SharePrefix>)prefixes);

    /// <summary>Share this access grant with the supplied permission set and prefixes.</summary>
    public Access Share(Permission permission, IEnumerable<SharePrefix> prefixes)
    {
        ArgumentNullException.ThrowIfNull(permission);
        ArgumentNullException.ThrowIfNull(prefixes);

        using var accessLease = AcquireAccessLease();
        var sharePrefixes = prefixes.ToArray();

        var prefixList = sharePrefixes.Select((sp, i) =>
        {
            if (sp == null) throw new ArgumentException("Share prefixes must not contain null values.", nameof(prefixes));
            if (string.IsNullOrWhiteSpace(sp.Bucket))
                throw new ArgumentException("Share prefix bucket must not be null or whitespace.", nameof(prefixes));
            return (object?)new Dictionary<string, object?> { ["bucket"] = sp.Bucket, ["prefix"] = sp.Prefix ?? string.Empty };
        }).ToArray();

        var tempDir = ResolveTempDirectory(_config?.TempDirectory);

        var result = NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]              = "access_share",
            ["access_id"]       = accessLease.Handle,
            ["allow_download"]  = permission.AllowDownload,
            ["allow_upload"]    = permission.AllowUpload,
            ["allow_list"]      = permission.AllowList,
            ["allow_delete"]    = permission.AllowDelete,
            ["not_before"]      = UplinkInterop.DateTimeToUnix(permission.NotBefore),
            ["not_after"]       = UplinkInterop.DateTimeToUnix(permission.NotAfter),
            ["prefixes"]        = prefixList,
            ["user_agent"]      = _config?.UserAgent ?? string.Empty,
            ["dial_timeout_ms"] = _config?.DialTimeoutMilliseconds ?? 0,
            ["temp_dir"]        = tempDir
        }).GetAwaiter().GetResult();

        if (result.IsError)
            throw new AccessException($"Failed to share access grant: {result.ErrorMessage}");

        long newAccessId  = result.Data.GetProperty("access_id").GetInt64();
        long newProjectId = result.Data.GetProperty("project_id").GetInt64();
        return new Access(newAccessId, newProjectId, _config);
    }

    /// <summary>Revoke a child access grant that was derived from this access grant.</summary>
    public async Task RevokeAsync(Access childAccess)
    {
        ArgumentNullException.ThrowIfNull(childAccess);
        using var childAccessLease = childAccess.AcquireAccessLease();
        using var projectLease = AcquireProjectLease();

        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]             = "access_revoke",
            ["project_id"]     = projectLease.Handle,
            ["child_access_id"] = childAccessLease.Handle
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new AccessException($"Failed to revoke access grant: {result.ErrorMessage}");
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

    private static Config? CloneConfig(Config? config)
    {
        if (config == null) return null;
        return new Config
        {
            UserAgent               = config.UserAgent,
            DialTimeoutMilliseconds = config.DialTimeoutMilliseconds,
            TempDirectory           = config.TempDirectory,
            EnableDiagnostics       = config.EnableDiagnostics,
            DiagnosticsLogFilePath  = config.DiagnosticsLogFilePath
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
            if (_disposeRequested) return;
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
            return new AccessHandleLease(this, _accessId);
        }
    }

    internal ProjectHandleLease AcquireProjectLease()
    {
        lock (_lifetimeSync)
        {
            ThrowIfDisposedNoLock();

            if (_projectId == 0)
                throw new ObjectDisposedException(nameof(Access));

            _activeProjectLeases++;
            return new ProjectHandleLease(this, _projectId);
        }
    }

    private void ReleaseProjectLease(long projectId)
    {
        lock (_lifetimeSync)
        {
            if (_activeProjectLeases == 0)
                throw new InvalidOperationException("Project lease released without an active lease.");

            _activeProjectLeases--;

            if (_disposeRequested && _activeAccessLeases == 0 && _activeProjectLeases == 0)
                ReleaseHandlesNoLock();
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

    private void ReleaseHandlesNoLock()
    {
        if (_disposed) return;
        _disposed = true;

        if (_accessId != 0 || _projectId != 0)
        {
            var (aid, pid) = (_accessId, _projectId);
            _accessId  = 0;
            _projectId = 0;

            _ = Task.Run(async () =>
            {
                try
                {
                    await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
                    {
                        ["op"]        = "access_free",
                        ["access_id"]  = aid,
                        ["project_id"] = pid
                    }).ConfigureAwait(false);
                }
                catch { }
            });
        }
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

        internal ProjectHandleLease(Access owner, long handle)
        {
            _owner = owner;
            Handle = handle;
        }

        internal long Handle { get; }

        public void Dispose()
        {
            var owner = System.Threading.Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseProjectLease(Handle);
        }
    }

    internal sealed class AccessHandleLease : IDisposable
    {
        private Access? _owner;

        internal AccessHandleLease(Access owner, long handle)
        {
            _owner = owner;
            Handle = handle;
        }

        internal long Handle { get; }

        public void Dispose()
        {
            var owner = System.Threading.Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseAccessLease();
        }
    }
}
