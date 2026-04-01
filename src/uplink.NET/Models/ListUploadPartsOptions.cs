namespace uplink.NET.Models;

public class ListUploadPartsOptions
{
    public string Cursor { get; set; } = string.Empty;
    public uint CursorPartNumber { get; set; }
}
