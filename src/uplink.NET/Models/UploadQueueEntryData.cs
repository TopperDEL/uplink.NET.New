using SQLite;

namespace uplink.NET.Models;

public class UploadQueueEntryData
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public int UploadQueueEntryId { get; set; }
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
}
