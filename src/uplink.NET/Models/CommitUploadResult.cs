namespace uplink.NET.Models;

public class CommitUploadResult
{
    public StorjObject? Object { get; internal set; }
    public string Error { get; internal set; } = string.Empty;
}
