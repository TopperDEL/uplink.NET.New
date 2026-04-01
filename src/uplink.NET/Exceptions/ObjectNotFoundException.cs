namespace uplink.NET.Exceptions;

public class ObjectNotFoundException : Exception
{
    public string ObjectKey { get; }

    public ObjectNotFoundException(string objectKey, string message)
        : base(message)
    {
        ObjectKey = objectKey;
    }
}
