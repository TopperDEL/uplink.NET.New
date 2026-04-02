using uplink.NET.Exceptions;
using uplink.NET.IntegrationTests.Infrastructure;
using uplink.NET.Models;
using uplink.NET.Services;

namespace uplink.NET.IntegrationTests;

public class BucketServiceTests
{
    [StorjIntegrationFact]
    public async Task CreateBucket_Creates_NewBucket()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var bucketName = StorjTestHelper.CreateBucketName("create");

        try
        {
            var bucket = await bucketService.CreateBucketAsync(bucketName);
            Assert.Equal(bucketName, bucket.Name);
        }
        finally
        {
            await StorjTestHelper.DeleteBucketIfPresentAsync(bucketService, bucketName);
        }
    }

    [StorjIntegrationFact]
    public async Task CreateBucket_Fails_OnBucketAlreadyExisting()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var bucketName = StorjTestHelper.CreateBucketName("exists");

        try
        {
            await bucketService.CreateBucketAsync(bucketName);
            await Assert.ThrowsAsync<BucketCreationException>(() => bucketService.CreateBucketAsync(bucketName));
        }
        finally
        {
            await StorjTestHelper.DeleteBucketIfPresentAsync(bucketService, bucketName);
        }
    }

    [StorjIntegrationFact]
    public async Task EnsureBucket_Creates_NewBucket()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var bucketName = StorjTestHelper.CreateBucketName("ensure");

        try
        {
            var bucket = await bucketService.EnsureBucketAsync(bucketName);
            Assert.Equal(bucketName, bucket.Name);
        }
        finally
        {
            await StorjTestHelper.DeleteBucketIfPresentAsync(bucketService, bucketName);
        }
    }

    [StorjIntegrationFact]
    public async Task EnsureBucket_Returns_BucketEvenIfItExistsAlready()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var bucketName = StorjTestHelper.CreateBucketName("ensuretwice");

        try
        {
            await bucketService.CreateBucketAsync(bucketName);
            var bucket = await bucketService.EnsureBucketAsync(bucketName);

            Assert.Equal(bucketName, bucket.Name);
        }
        finally
        {
            await StorjTestHelper.DeleteBucketIfPresentAsync(bucketService, bucketName);
        }
    }

    [StorjIntegrationFact]
    public async Task GetBucket_Retrieves_Bucket()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var bucketName = StorjTestHelper.CreateBucketName("get");

        try
        {
            await bucketService.CreateBucketAsync(bucketName);
            var bucket = await bucketService.GetBucketAsync(bucketName);

            Assert.Equal(bucketName, bucket.Name);
        }
        finally
        {
            await StorjTestHelper.DeleteBucketIfPresentAsync(bucketService, bucketName);
        }
    }

    [StorjIntegrationFact]
    public async Task GetBucket_Fails_OnNotExistingBucket()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);

        await Assert.ThrowsAsync<BucketNotFoundException>(
            () => bucketService.GetBucketAsync(StorjTestHelper.CreateBucketName("missing")));
    }

    [StorjIntegrationFact]
    public async Task DeleteBucket_Deletes_Bucket()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var bucketName = StorjTestHelper.CreateBucketName("delete");

        await bucketService.CreateBucketAsync(bucketName);
        await bucketService.DeleteBucketAsync(bucketName);

        await Assert.ThrowsAsync<BucketNotFoundException>(() => bucketService.GetBucketAsync(bucketName));
    }

    [StorjIntegrationFact]
    public async Task DeleteBucket_Fails_OnNotExistingBucket()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);

        await Assert.ThrowsAsync<BucketDeletionException>(
            () => bucketService.DeleteBucketAsync(StorjTestHelper.CreateBucketName("missingdelete")));
    }

    [StorjIntegrationFact]
    public async Task DeleteBucketWithObjects_Deletes_Bucket()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var bucketName = StorjTestHelper.CreateBucketName("deleteobjects");
        var objectKey = StorjTestHelper.CreateObjectKey("delete-bucket-with-objects");
        var payload = IntegrationTestEnvironment.CreatePayload(512);

        await bucketService.CreateBucketAsync(bucketName);

        try
        {
            await StorjTestHelper.UploadBytesAsync(objectService, bucketName, objectKey, payload);
            await bucketService.DeleteBucketWithObjectsAsync(bucketName);

            await Assert.ThrowsAsync<BucketNotFoundException>(() => bucketService.GetBucketAsync(bucketName));
        }
        catch
        {
            await StorjTestHelper.DeleteBucketIfPresentAsync(bucketService, bucketName);
            throw;
        }
    }

    [StorjIntegrationFact]
    public async Task DeleteBucketWithObjects_Fails_OnNotExistingBucket()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);

        await Assert.ThrowsAsync<BucketDeletionException>(
            () => bucketService.DeleteBucketWithObjectsAsync(StorjTestHelper.CreateBucketName("missingrecursive")));
    }

    [StorjIntegrationFact]
    public async Task ListBuckets_Lists_TwoNewlyCreatedBuckets()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var firstBucketName = StorjTestHelper.CreateBucketName("lista");
        var secondBucketName = StorjTestHelper.CreateBucketName("listb");

        try
        {
            await bucketService.CreateBucketAsync(firstBucketName);
            await bucketService.CreateBucketAsync(secondBucketName);

            var buckets = await bucketService.ListBucketsAsync(new ListBucketsOptions());
            var bucketNames = buckets.Items.Select(bucket => bucket.Name).ToHashSet(StringComparer.Ordinal);

            Assert.Contains(firstBucketName, bucketNames);
            Assert.Contains(secondBucketName, bucketNames);
        }
        finally
        {
            await StorjTestHelper.DeleteBucketIfPresentAsync(bucketService, firstBucketName);
            await StorjTestHelper.DeleteBucketIfPresentAsync(bucketService, secondBucketName);
        }
    }
}
