namespace uplink.NET.Models;

public class ListObjectsOptions
{
    public string Prefix { get; set; } = string.Empty;
    public string Cursor { get; set; } = string.Empty;
    public char Delimiter { get; set; }
    public bool Recursive { get; set; }
    public bool System { get; set; }
    public bool Custom { get; set; }
}
