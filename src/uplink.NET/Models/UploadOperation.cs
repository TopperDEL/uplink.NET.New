using System.Runtime.InteropServices;
using uplink.NET.Exceptions;
using uplink.NET.Native;
using NativeCallTrace = uplink.NET.Diagnostics.UplinkDiagnosticsSession.NativeCallTrace;

namespace uplink.NET.Models;

public delegate void UploadOperationProgressChanged(UploadOperation uploadOperation);
public delegate void UploadOperationEnded(UploadOperation uploadOperation);

/// <summary>
/// Represents an in-progress upload to Storj.
/// Call <see cref="StartUploadAsync"/> to begin the transfer.
/// </summary>
public class UploadOperation : IDisposable
{
    private const int ChunkSize = 80 * 1024; // 80 KB

    private readonly Access _access;
    private readonly string _bucketName;
    private readonly byte[] _data;
    private readonly UplinkOptions _nativeOptions;
    private readonly CustomMetadata? _customMetadata;
    private readonly object _startSync = new();

    private bool _cancelRequested;
    private Access.ProjectHandleLease? _projectLease;

    public string ObjectName { get; }
    public long BytesSent { get; private set; }
    public long TotalBytes { get; private set; }
    public bool Completed { get; private set; }
    public bool Failed { get; set; }
    public bool Cancelled { get; set; }
    public bool Running { get; set; }
    public string? ErrorMessage { get; private set; }

    public float PercentageCompleted =>
        TotalBytes > 0 ? (float)BytesSent / TotalBytes * 100f : 0f;

    public event UploadOperationProgressChanged? UploadOperationProgressChanged;
    public event UploadOperationEnded? UploadOperationEnded;

    internal record UplinkOptions(long Expires);

    internal UploadOperation(
        Access access,
        string bucketName,
        string objectName,
        byte[] data,
        UplinkOptions nativeOptions,
        CustomMetadata? customMetadata)
    {
        _access         = access;
        _bucketName     = bucketName;
        ObjectName      = objectName;
        _data           = data;
        TotalBytes      = data.Length;
        _nativeOptions  = nativeOptions;
        _customMetadata = customMetadata;
    }

    public Task? StartUploadAsync()
    {
        lock (_startSync)
        {
            if (_projectLease != null)
                throw new InvalidOperationException("The upload operation has already been started.");

            var projectLease = _access.AcquireProjectLease();

            try
            {
                _projectLease = projectLease;
                return Task.Run(PerformUploadAsync);
            }
            catch
            {
                projectLease.Dispose();
                _projectLease = null;
                throw;
            }
        }
    }

    private async Task PerformUploadAsync()
    {
        Running = true;
        BytesSent = 0;

        // Begin upload outside async/unsafe boundary
        var (uploadHandle, beginError) = BeginNativeUpload();
        if (beginError != null)
        {
            SetFailed(beginError);
            return;
        }

        try
        {
            // Write in chunks; each WriteChunk call is sync/unsafe but await is safe here
            int offset = 0;
            while (offset < _data.Length && !_cancelRequested)
            {
                int toWrite = Math.Min(ChunkSize, _data.Length - offset);
                var (written, writeError) = WriteChunk(uploadHandle, offset, toWrite);
                if (writeError != null)
                {
                    AbortNativeUpload(uploadHandle);
                    SetFailed(writeError);
                    return;
                }
                offset   += (int)written;
                BytesSent = offset;
                UploadOperationProgressChanged?.Invoke(this);
                await Task.Yield();
            }

            if (_cancelRequested)
            {
                AbortNativeUpload(uploadHandle);
                Cancelled = true;
                Running   = false;
                UploadOperationEnded?.Invoke(this);
                return;
            }

            if (_customMetadata?.Entries.Count > 0)
            {
                using var metadataTrace = _access.Trace("uplink_upload_set_custom_metadata", ("bucket", _bucketName), ("key", ObjectName));
                SetCustomMetadataNative(uploadHandle, _customMetadata, metadataTrace);
            }

            string? commitError = CommitNativeUpload(uploadHandle);
            if (commitError != null)
            {
                SetFailed(commitError);
                return;
            }

            Completed = true;
            Running   = false;
            UploadOperationEnded?.Invoke(this);
        }
        catch (Exception ex)
        {
            AbortNativeUpload(uploadHandle);
            SetFailed(ex.Message);
        }
        finally
        {
            UplinkInterop.FreeUploadHandle(uploadHandle);
            lock (_startSync)
            {
                _projectLease?.Dispose();
                _projectLease = null;
            }
        }
    }

    private unsafe (nint handle, string? error) BeginNativeUpload()
    {
        using var trace = _access.Trace("uplink_upload_object", ("bucket", _bucketName), ("key", ObjectName));
        var opts = new UplinkInterop.UplinkUploadOptions { expires = _nativeOptions.Expires };
        var result = UplinkInterop.uplink_upload_object(
            _projectLease!.Handle, _bucketName, ObjectName, &opts);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                trace?.NativeError(msg, code);
                return (nint.Zero, msg);
            }

            if (result.upload == nint.Zero)
            {
                trace?.Fail("Native library returned a null upload handle without an error.");
                return (nint.Zero, "Native library returned a null upload handle without an error.");
            }

            var uploadHandle = result.upload;
            result.upload = nint.Zero;
            trace?.Success();
            return (uploadHandle, null);
        }
        finally
        {
            UplinkInterop.uplink_free_upload_result(result);
        }
    }

    private unsafe (uint written, string? error) WriteChunk(
        nint handle, int offset, int count)
    {
        using var trace = _access.Trace("uplink_upload_write", ("bucket", _bucketName), ("key", ObjectName), ("offset", offset), ("count", count));
        var writeResult = UplinkInterop.WithPinnedBuffer(
            _data, offset, count,
            (ptr, len) => UplinkInterop.uplink_upload_write(handle, (void*)ptr, len));
        try
        {
            if (writeResult.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref writeResult.error);
                trace?.NativeError(msg, code);
                return (0, msg);
            }

            trace?.Success();
            return ((uint)(nuint)writeResult.bytes_written, null);
        }
        finally
        {
            UplinkInterop.uplink_free_write_result(writeResult);
        }
    }

    private static void AbortNativeUpload(nint handle)
    {
        var errPtr = UplinkInterop.uplink_upload_abort(handle);
        if (errPtr != nint.Zero) UplinkInterop.uplink_free_error(errPtr);
    }

    private string? CommitNativeUpload(nint handle)
    {
        using var trace = _access.Trace("uplink_upload_commit", ("bucket", _bucketName), ("key", ObjectName));
        var errPtr = UplinkInterop.uplink_upload_commit(handle);
        if (errPtr != nint.Zero)
        {
            var (msg, code) = UplinkInterop.ConsumeError(errPtr);
            trace?.NativeError(msg, code);
            return msg;
        }

        trace?.Success();
        return null;
    }

    private static unsafe void SetCustomMetadataNative(
        nint uploadHandle, CustomMetadata metadata, NativeCallTrace? trace)
    {
        var entries = metadata.Entries
            .Select(kv => new UplinkInterop.UplinkCustomMetadataEntry
            {
                key          = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(kv.Key),
                key_length   = (nuint)System.Text.Encoding.UTF8.GetByteCount(kv.Key),
                value        = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(kv.Value),
                value_length = (nuint)System.Text.Encoding.UTF8.GetByteCount(kv.Value)
            })
            .ToArray();

        fixed (UplinkInterop.UplinkCustomMetadataEntry* entriesPtr = entries)
        {
            var nativeMeta = new UplinkInterop.UplinkCustomMetadata
            {
                entries = (nint)entriesPtr,
                count   = (nuint)entries.Length
            };
            var errPtr = UplinkInterop.uplink_upload_set_custom_metadata(uploadHandle, nativeMeta);
            if (errPtr != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                trace?.NativeError(msg, code);
                throw new IOException($"Failed to set custom metadata: {msg}");
            }
        }

        foreach (var e in entries)
        {
            Marshal.FreeCoTaskMem(e.key);
            Marshal.FreeCoTaskMem(e.value);
        }

        trace?.Success();
    }

    private void SetFailed(string message)
    {
        Failed       = true;
        Running      = false;
        ErrorMessage = message;
        UploadOperationEnded?.Invoke(this);
    }

    public void Cancel() => _cancelRequested = true;

    protected virtual void Dispose(bool disposing) { }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
