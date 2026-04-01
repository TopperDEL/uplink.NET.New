namespace uplink.NET.Models;

public class ObjectList
{
    public List<StorjObject> Items { get; } = new();
    public bool More { get; internal set; }
}
