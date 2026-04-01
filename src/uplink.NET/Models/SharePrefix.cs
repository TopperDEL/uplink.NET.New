namespace uplink.NET.Models;

/// <summary>
/// Restricts a shared access grant to a bucket and optional object-key prefix.
/// </summary>
public class SharePrefix
{
    /// <summary>The bucket covered by the shared access grant.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>The object-key prefix covered by the shared access grant.</summary>
    public string Prefix { get; set; } = string.Empty;
}
