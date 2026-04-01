using SQLite;

namespace uplink.NET.Models;

public class UploadQueueEntry
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public string Identifier { get; set; } = string.Empty;
    public string AccessGrant { get; set; } = string.Empty;
    public string BucketName { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string UploadId { get; set; } = string.Empty;
    public int TotalBytes { get; set; }
    public int BytesCompleted { get; set; }
    public uint CurrentPartNumber { get; set; }
    public bool Failed { get; set; }
    public string FailedMessage { get; set; } = string.Empty;
    public string CustomMetadataJson { get; set; } = string.Empty;
}
