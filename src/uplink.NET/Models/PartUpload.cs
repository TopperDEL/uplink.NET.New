using uplink.NET.Native;

namespace uplink.NET.Models;

/// <summary>Represents a native part-upload handle used in multipart uploads.</summary>
public class PartUpload
{
    internal nint Handle { get; set; }
}
