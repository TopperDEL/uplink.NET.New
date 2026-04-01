namespace uplink.NET.Exceptions;

public class BucketDeletionException : Exception
{
    public string BucketName { get; }

    public BucketDeletionException(string bucketName, string message)
        : base(message)
    {
        BucketName = bucketName;
    }
}
