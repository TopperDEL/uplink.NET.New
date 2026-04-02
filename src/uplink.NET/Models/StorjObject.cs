namespace uplink.NET.Models;

/// <summary>
/// Represents a Storj object stored in a bucket.
/// Named StorjObject to avoid collision with System.Object.
/// </summary>
public class StorjObject
{
    public string Key { get; internal set; } = string.Empty;
    public bool IsPrefix { get; internal set; }
    public SystemMetadata SystemMetadata { get; internal set; } = new();
    public DateTime Created => SystemMetadata.Created;
    public DateTime Expires => SystemMetadata.Expires;
    public long ContentLength => SystemMetadata.ContentLength;
    public CustomMetadata? CustomMetadata { get; internal set; }
}
