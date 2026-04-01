namespace uplink.NET.Exceptions;

public class MultipartUploadFailedException : Exception
{
    public MultipartUploadFailedException(string message) : base(message) { }
}
