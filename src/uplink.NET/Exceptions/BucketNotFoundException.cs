namespace uplink.NET.Exceptions;

public class BucketNotFoundException : Exception
{
    public string BucketName { get; }

    public BucketNotFoundException(string bucketName, string message)
        : base(message)
    {
        BucketName = bucketName;
    }
}
