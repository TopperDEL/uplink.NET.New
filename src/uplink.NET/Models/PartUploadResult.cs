namespace uplink.NET.Models;

public class PartUploadResult
{
    public uint BytesWritten { get; internal set; }
    public string Error { get; internal set; } = string.Empty;
}
