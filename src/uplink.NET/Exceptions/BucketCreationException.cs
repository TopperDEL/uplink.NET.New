namespace uplink.NET.Exceptions;

public class BucketCreationException : Exception
{
    public string BucketName { get; }

    public BucketCreationException(string bucketName, string message)
        : base(message)
    {
        BucketName = bucketName;
    }
}
