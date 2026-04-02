using System.Collections.Concurrent;
using uplink.NET.IntegrationTests.Infrastructure;
using uplink.NET.Models;
using uplink.NET.Services;

namespace uplink.NET.IntegrationTests;

public class ConcurrencyStressTests
{
    [StorjStressFact]
    public async Task SharedAccess_RepeatedConcurrentTransfers_Complete()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var multipartUploadService = new MultipartUploadService(context.Access);
        var createdKeys = new ConcurrentBag<string>();

        await bucketService.EnsureBucketAsync(context.BucketName);

        var iterations = IntegrationTestEnvironment.StressIterations;

        try
        {
            var workers = new Task[]
            {
                RunObjectWorkerAsync("object-a"),
                RunObjectWorkerAsync("object-b"),
                RunMultipartWorkerAsync()
            };

            await Task.WhenAll(workers);
        }
        finally
        {
            while (createdKeys.TryTake(out var objectKey))
                await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, objectKey);
        }

        async Task RunObjectWorkerAsync(string category)
        {
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var objectKey = StorjTestHelper.CreateObjectKey($"{category}-{iteration}");
                var payload = IntegrationTestEnvironment.CreatePayload(2_048 + iteration);
                createdKeys.Add(objectKey);

                await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, objectKey, payload);

                var listedObjects = await objectService.ListObjectsAsync(
                    context.BucketName,
                    new ListObjectsOptions
                    {
                        Prefix = objectKey,
                        Recursive = true
                    });

                Assert.Contains(listedObjects.Items, item => item.Key == objectKey);

                var downloaded = await StorjTestHelper.DownloadBytesAsync(objectService, context.BucketName, objectKey);
                Assert.Equal(payload, downloaded);
            }
        }

        async Task RunMultipartWorkerAsync()
        {
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var objectKey = StorjTestHelper.CreateObjectKey($"multipart-{iteration}");
                var payload = IntegrationTestEnvironment.CreatePayload(6_144 + iteration);
                createdKeys.Add(objectKey);

                var uploadInfo = await multipartUploadService.BeginUploadAsync(context.BucketName, objectKey, new UploadOptions());

                var midpoint = payload.Length / 2;
                var firstPart = payload.Take(midpoint).ToArray();
                var secondPart = payload.Skip(midpoint).ToArray();

                var firstResult = await multipartUploadService.UploadPartAsync(
                    context.BucketName,
                    objectKey,
                    uploadInfo.UploadId,
                    1,
                    firstPart);
                var secondResult = await multipartUploadService.UploadPartAsync(
                    context.BucketName,
                    objectKey,
                    uploadInfo.UploadId,
                    2,
                    secondPart);

                Assert.True(string.IsNullOrEmpty(firstResult.Error), firstResult.Error);
                Assert.True(string.IsNullOrEmpty(secondResult.Error), secondResult.Error);

                var parts = await multipartUploadService.ListUploadPartsAsync(
                    context.BucketName,
                    objectKey,
                    uploadInfo.UploadId,
                    new ListUploadPartsOptions());
                Assert.Equal(2, parts.Items.Count);

                var commit = await multipartUploadService.CommitUploadAsync(
                    context.BucketName,
                    objectKey,
                    uploadInfo.UploadId,
                    new CommitUploadOptions());
                Assert.True(string.IsNullOrEmpty(commit.Error), commit.Error);

                var downloaded = await StorjTestHelper.DownloadBytesAsync(objectService, context.BucketName, objectKey);
                Assert.Equal(payload, downloaded);
            }
        }
    }
}
