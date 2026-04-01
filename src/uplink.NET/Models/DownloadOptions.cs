namespace uplink.NET.Models;

public class DownloadOptions
{
    public long Offset { get; set; }
    /// <summary>Number of bytes to download. Use -1 (default) to download the entire object.</summary>
    public long Length { get; set; } = -1;
}
