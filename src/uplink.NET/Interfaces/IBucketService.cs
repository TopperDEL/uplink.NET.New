using uplink.NET.Models;

namespace uplink.NET.Interfaces;

public interface IBucketService
{
    Task<Bucket> CreateBucketAsync(string bucketName);
    Task<Bucket> EnsureBucketAsync(string bucketName);
    Task<Bucket> GetBucketAsync(string bucketName);
    Task<BucketList> ListBucketsAsync(ListBucketsOptions listBucketsOptions);
    Task DeleteBucketAsync(string bucketName);
    Task DeleteBucketWithObjectsAsync(string bucketName);
}
