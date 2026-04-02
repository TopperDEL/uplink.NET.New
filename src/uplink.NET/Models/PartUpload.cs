using uplink.NET.Native;

namespace uplink.NET.Models;

/// <summary>Represents a native part-upload handle used in multipart uploads.</summary>
public sealed class PartUpload : IDisposable
{
    internal PartUpload(UplinkPartUploadSafeHandle handle)
    {
        Handle = handle ?? throw new ArgumentNullException(nameof(handle));
    }

    internal UplinkPartUploadSafeHandle Handle { get; }

    public void Dispose()
    {
        Handle.Dispose();
        GC.SuppressFinalize(this);
    }
}
