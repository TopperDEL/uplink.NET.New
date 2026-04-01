namespace uplink.NET.Exceptions;

public class SetETagFailedException : Exception
{
    public SetETagFailedException(string message) : base(message) { }
}
