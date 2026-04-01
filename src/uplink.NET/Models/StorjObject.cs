namespace uplink.NET.Models;

/// <summary>
/// Represents a Storj object stored in a bucket.
/// Named StorjObject to avoid collision with System.Object.
/// </summary>
public class StorjObject
{
    public string Key { get; internal set; } = string.Empty;
    public bool IsPrefix { get; internal set; }
    public DateTime Created { get; internal set; }
    public DateTime Expires { get; internal set; }
    public long ContentLength { get; internal set; }
    public CustomMetadata? CustomMetadata { get; internal set; }
}
