namespace uplink.NET.Exceptions;

public class AbortUploadFailedException : Exception
{
    public AbortUploadFailedException(string message) : base(message) { }
}
