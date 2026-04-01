namespace uplink.NET.Models;

public class Config
{
    public string UserAgent { get; set; } = string.Empty;
    public int DialTimeoutMilliseconds { get; set; }
    public string TempDirectory { get; set; } = string.Empty;
}
