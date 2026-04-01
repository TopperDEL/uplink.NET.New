namespace uplink.NET.Models;

public class Permission
{
    public bool AllowDownload { get; set; }

    public bool AllowUpload { get; set; }

    public bool AllowList { get; set; }

    public bool AllowDelete { get; set; }

    public DateTime? NotBefore { get; set; }

    public DateTime? NotAfter { get; set; }
}
