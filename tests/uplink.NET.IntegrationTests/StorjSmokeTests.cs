using uplink.NET.Exceptions;
using uplink.NET.IntegrationTests.Infrastructure;
using uplink.NET.Services;

namespace uplink.NET.IntegrationTests;

public class StorjSmokeTests
{
    [StorjIntegrationFact]
    public async Task Bucket_roundtrip_succeeds_with_real_access_grant()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var objectKey = $"integration-tests/{Guid.NewGuid():N}.txt";
        var payload = System.Text.Encoding.UTF8.GetBytes("uplink.NET integration smoke test");

        try
        {
            var bucket = await bucketService.EnsureBucketAsync(context.BucketName);
            Assert.Equal(context.BucketName, bucket.Name);

            var upload = await objectService.UploadObjectAsync(context.Access, context.BucketName, objectKey, payload, startImmediately: false);
            await upload.StartUploadAsync()!;
            Assert.True(upload.Completed);
            Assert.False(upload.Failed);
            Assert.False(upload.Cancelled);

            var storedObject = await objectService.GetObjectAsync(context.Access, context.BucketName, objectKey);
            Assert.Equal(objectKey, storedObject.Key);
            Assert.Equal(payload.Length, storedObject.ContentLength);

            var download = await objectService.DownloadObjectAsync(context.Access, context.BucketName, objectKey, startImmediately: false);
            await download.StartDownloadAsync()!;
            Assert.True(download.Completed);
            Assert.False(download.Failed);
            Assert.False(download.Cancelled);
            Assert.Equal(payload, download.DownloadedBytes);
        }
        finally
        {
            try
            {
                await objectService.DeleteObjectAsync(context.Access, context.BucketName, objectKey);
            }
            catch (ObjectNotFoundException)
            {
            }
        }

        await Assert.ThrowsAsync<ObjectNotFoundException>(() => objectService.GetObjectAsync(context.Access, context.BucketName, objectKey));
    }
}
