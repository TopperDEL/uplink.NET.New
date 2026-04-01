namespace uplink.NET.Models;

/// <summary>
/// Defines which actions a shared access grant may perform and when it is valid.
/// </summary>
public class Permission
{
    /// <summary>Allow downloading object data.</summary>
    public bool AllowDownload { get; set; }

    /// <summary>Allow uploading objects.</summary>
    public bool AllowUpload { get; set; }

    /// <summary>Allow listing buckets or objects.</summary>
    public bool AllowList { get; set; }

    /// <summary>Allow deleting buckets or objects.</summary>
    public bool AllowDelete { get; set; }

    /// <summary>The earliest UTC time when the shared access becomes valid.</summary>
    public DateTime? NotBefore { get; set; }

    /// <summary>The latest UTC time when the shared access remains valid.</summary>
    public DateTime? NotAfter { get; set; }
}
