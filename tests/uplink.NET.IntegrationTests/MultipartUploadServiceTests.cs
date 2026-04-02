using uplink.NET.Exceptions;
using uplink.NET.IntegrationTests.Infrastructure;
using uplink.NET.Models;
using uplink.NET.Services;

namespace uplink.NET.IntegrationTests;

public class MultipartUploadServiceTests
{
    public static TheoryData<int> SingleTakeUploadSizes => new()
    {
        512,
        5_120,
        524_288
    };

    public static TheoryData<int, int, int> MultiPartUploadCases => new()
    {
        { 512, 1, 5_242_880 },
        { 7_340_032, 2, 5_242_880 }
    };

    [StorjIntegrationTheory]
    [MemberData(nameof(SingleTakeUploadSizes))]
    public async Task MultipartUpload_X_BytesInOneTake(int sizeInBytes)
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var multipartUploadService = new MultipartUploadService(context.Access);
        var objectService = new ObjectService(context.Access);
        var objectKey = StorjTestHelper.CreateObjectKey($"multipart-single-{sizeInBytes}");
        var payload = IntegrationTestEnvironment.CreatePayload(sizeInBytes);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            await UploadMultipartAsync(multipartUploadService, context.BucketName, objectKey, payload, payload.Length);

            var downloaded = await StorjTestHelper.DownloadBytesAsync(objectService, context.BucketName, objectKey);
            Assert.Equal(payload, downloaded);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, objectKey);
        }
    }

    [StorjIntegrationTheory]
    [MemberData(nameof(MultiPartUploadCases))]
    public async Task MultipartUpload_X_BytesByMultipleParts(int sizeInBytes, int expectedPartCount, int partSize)
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var multipartUploadService = new MultipartUploadService(context.Access);
        var objectService = new ObjectService(context.Access);
        var objectKey = StorjTestHelper.CreateObjectKey($"multipart-multi-{sizeInBytes}");
        var payload = IntegrationTestEnvironment.CreatePayload(sizeInBytes);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            var uploadInfo = await multipartUploadService.BeginUploadAsync(context.BucketName, objectKey, new UploadOptions());

            var uploadedPartCount = await UploadPartsAsync(
                multipartUploadService,
                context.BucketName,
                objectKey,
                uploadInfo.UploadId,
                payload,
                partSize);

            var parts = await multipartUploadService.ListUploadPartsAsync(
                context.BucketName,
                objectKey,
                uploadInfo.UploadId,
                new ListUploadPartsOptions());
            var commit = await multipartUploadService.CommitUploadAsync(
                context.BucketName,
                objectKey,
                uploadInfo.UploadId,
                new CommitUploadOptions());

            Assert.Equal(expectedPartCount, uploadedPartCount);
            Assert.Equal(expectedPartCount, parts.Items.Count);
            Assert.True(string.IsNullOrEmpty(commit.Error), commit.Error);
            Assert.NotNull(commit.Object);

            var downloaded = await StorjTestHelper.DownloadBytesAsync(objectService, context.BucketName, objectKey);
            Assert.Equal(payload, downloaded);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, objectKey);
        }
    }

    [StorjIntegrationFact]
    public async Task AbortMultipartUpload_AfterXParts()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var multipartUploadService = new MultipartUploadService(context.Access);
        var objectService = new ObjectService(context.Access);
        var objectKey = StorjTestHelper.CreateObjectKey("multipart-abort-after-parts");
        var payload = IntegrationTestEnvironment.CreatePayload(5_120);

        await bucketService.EnsureBucketAsync(context.BucketName);

        var uploadInfo = await multipartUploadService.BeginUploadAsync(context.BucketName, objectKey, new UploadOptions());
        await UploadPartsAsync(
            multipartUploadService,
            context.BucketName,
            objectKey,
            uploadInfo.UploadId,
            payload,
            partSize: 10,
            maxParts: 2);

        await multipartUploadService.AbortUploadAsync(context.BucketName, objectKey, uploadInfo.UploadId);

        var uploads = await multipartUploadService.ListUploadsAsync(
            context.BucketName,
            new ListUploadOptions { Prefix = objectKey });

        Assert.DoesNotContain(uploads.Items, item => item.UploadId == uploadInfo.UploadId);
        await Assert.ThrowsAsync<ObjectNotFoundException>(() => objectService.GetObjectAsync(context.BucketName, objectKey));
    }

    [StorjIntegrationFact]
    public async Task ListMultipartUploads_Lists_OpenUploads()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var multipartUploadService = new MultipartUploadService(context.Access);
        var objectKey = StorjTestHelper.CreateObjectKey("multipart-open-list");

        await bucketService.EnsureBucketAsync(context.BucketName);
        var uploadInfo = await multipartUploadService.BeginUploadAsync(context.BucketName, objectKey, new UploadOptions());

        try
        {
            var uploads = await multipartUploadService.ListUploadsAsync(
                context.BucketName,
                new ListUploadOptions { Prefix = objectKey });

            Assert.Contains(uploads.Items, item => item.UploadId == uploadInfo.UploadId && item.Key == objectKey);
        }
        finally
        {
            await multipartUploadService.AbortUploadAsync(context.BucketName, objectKey, uploadInfo.UploadId);
        }
    }

    [StorjIntegrationFact]
    public async Task AbortMultipartUploads_Aborts_OpenUpload()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var multipartUploadService = new MultipartUploadService(context.Access);
        var objectKey = StorjTestHelper.CreateObjectKey("multipart-abort-open");

        await bucketService.EnsureBucketAsync(context.BucketName);
        var uploadInfo = await multipartUploadService.BeginUploadAsync(context.BucketName, objectKey, new UploadOptions());
        await multipartUploadService.AbortUploadAsync(context.BucketName, objectKey, uploadInfo.UploadId);

        var uploads = await multipartUploadService.ListUploadsAsync(
            context.BucketName,
            new ListUploadOptions { Prefix = objectKey });

        Assert.DoesNotContain(uploads.Items, item => item.UploadId == uploadInfo.UploadId);
    }

    private static async Task UploadMultipartAsync(
        MultipartUploadService multipartUploadService,
        string bucketName,
        string objectKey,
        byte[] payload,
        int partSize)
    {
        var uploadInfo = await multipartUploadService.BeginUploadAsync(bucketName, objectKey, new UploadOptions());
        await UploadPartsAsync(multipartUploadService, bucketName, objectKey, uploadInfo.UploadId, payload, partSize);

        var commit = await multipartUploadService.CommitUploadAsync(
            bucketName,
            objectKey,
            uploadInfo.UploadId,
            new CommitUploadOptions());

        Assert.True(string.IsNullOrEmpty(commit.Error), commit.Error);
        Assert.NotNull(commit.Object);
    }

    private static async Task<int> UploadPartsAsync(
        MultipartUploadService multipartUploadService,
        string bucketName,
        string objectKey,
        string uploadId,
        byte[] payload,
        int partSize,
        int? maxParts = null)
    {
        var partNumber = 1u;
        var uploadedPartCount = 0;

        for (var offset = 0; offset < payload.Length; offset += partSize)
        {
            if (maxParts.HasValue && uploadedPartCount >= maxParts.Value)
                break;

            var length = Math.Min(partSize, payload.Length - offset);
            var partBytes = payload.Skip(offset).Take(length).ToArray();
            var partResult = await multipartUploadService.UploadPartAsync(
                bucketName,
                objectKey,
                uploadId,
                partNumber++,
                partBytes);

            Assert.True(string.IsNullOrEmpty(partResult.Error), partResult.Error);
            Assert.Equal((uint)length, partResult.BytesWritten);
            uploadedPartCount++;
        }

        return uploadedPartCount;
    }
}
