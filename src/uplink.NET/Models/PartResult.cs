namespace uplink.NET.Models;

public class PartResult
{
    public uint PartNumber { get; internal set; }
    public long Size { get; internal set; }
    public DateTime Modified { get; internal set; }
    public string ETag { get; internal set; } = string.Empty;
}
