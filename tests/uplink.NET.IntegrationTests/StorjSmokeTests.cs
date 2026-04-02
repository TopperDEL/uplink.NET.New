using uplink.NET.Exceptions;
using uplink.NET.IntegrationTests.Infrastructure;
using uplink.NET.Models;
using uplink.NET.Services;

namespace uplink.NET.IntegrationTests;

public class StorjSmokeTests
{
    public static TheoryData<int> RoundtripObjectSizes => new()
    {
        256,
        IntegrationTestEnvironment.StorjInlinePlacementLimitBytes - 1,
        IntegrationTestEnvironment.StorjInlinePlacementLimitBytes,
        IntegrationTestEnvironment.StorjInlinePlacementLimitBytes + 1,
        8 * 1024,
        512 * 1024
    };

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

            var upload = await objectService.UploadObjectAsync(context.BucketName, objectKey, payload, startImmediately: false);
            var uploadTask = upload.StartUploadAsync();
            Assert.NotNull(uploadTask);
            await uploadTask;
            Assert.True(upload.Completed);
            Assert.False(upload.Failed);
            Assert.False(upload.Cancelled);

            var storedObject = await objectService.GetObjectAsync(context.BucketName, objectKey);
            Assert.Equal(objectKey, storedObject.Key);
            Assert.Equal(payload.Length, storedObject.ContentLength);

            var download = await objectService.DownloadObjectAsync(context.BucketName, objectKey, startImmediately: false);
            var downloadTask = download.StartDownloadAsync();
            Assert.NotNull(downloadTask);
            await downloadTask;
            Assert.True(download.Completed);
            Assert.False(download.Failed);
            Assert.False(download.Cancelled);
            Assert.Equal(payload, download.DownloadedBytes);
        }
        finally
        {
            try
            {
                await objectService.DeleteObjectAsync(context.BucketName, objectKey);
            }
            catch (ObjectNotFoundException)
            {
            }
        }

        await Assert.ThrowsAsync<ObjectNotFoundException>(() => objectService.GetObjectAsync(context.BucketName, objectKey));
    }

    [StorjIntegrationTheory]
    [MemberData(nameof(RoundtripObjectSizes))]
    public async Task UploadAndDownload_roundtrip_supports_multiple_sizes(int sizeInBytes)
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var objectKey = $"integration-tests/{Guid.NewGuid():N}-{sizeInBytes}.bin";
        var payload = IntegrationTestEnvironment.CreatePayload(sizeInBytes);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);

            var upload = await objectService.UploadObjectAsync(context.BucketName, objectKey, payload, startImmediately: false);
            var uploadTask = upload.StartUploadAsync();
            Assert.NotNull(uploadTask);
            await uploadTask;

            Assert.True(upload.Completed, upload.ErrorMessage);
            Assert.False(upload.Failed);
            Assert.False(upload.Cancelled);
            Assert.Equal(payload.Length, upload.BytesSent);

            var storedObject = await objectService.GetObjectAsync(context.BucketName, objectKey);
            Assert.Equal(objectKey, storedObject.Key);
            Assert.Equal(payload.Length, storedObject.ContentLength);

            var download = await objectService.DownloadObjectAsync(context.BucketName, objectKey, startImmediately: false);
            var downloadTask = download.StartDownloadAsync();
            Assert.NotNull(downloadTask);
            await downloadTask;

            Assert.True(download.Completed, download.ErrorMessage);
            Assert.False(download.Failed);
            Assert.False(download.Cancelled);
            Assert.Equal(payload.Length, download.BytesReceived);
            Assert.Equal(payload, download.DownloadedBytes);
        }
        finally
        {
            await DeleteObjectIfPresentAsync(objectService, context, objectKey);
        }
    }

    [StorjIntegrationTheory]
    [MemberData(nameof(RoundtripObjectSizes))]
    public async Task UploadFromStreamAndDownloadAsStream_roundtrip_supports_multiple_sizes(int sizeInBytes)
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var objectKey = $"integration-tests/{Guid.NewGuid():N}-{sizeInBytes}-stream.bin";
        var payload = IntegrationTestEnvironment.CreatePayload(sizeInBytes);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);

            using var uploadStream = new MemoryStream(payload, writable: false);
            var upload = await objectService.UploadObjectAsync(
                context.BucketName,
                objectKey,
                uploadStream,
                uploadOptions: null,
                customMetadata: null,
                startImmediately: false);
            var uploadTask = upload.StartUploadAsync();
            Assert.NotNull(uploadTask);
            await uploadTask;

            Assert.True(upload.Completed, upload.ErrorMessage);
            Assert.False(upload.Failed);
            Assert.False(upload.Cancelled);
            Assert.Equal(payload.Length, upload.BytesSent);

            using var downloadStream = await objectService.GetObjectAsStream(context.BucketName, objectKey);
            using var result = new MemoryStream();
            await downloadStream.CopyToAsync(result);

            Assert.Equal(payload.Length, downloadStream.Length);
            Assert.Equal(payload.Length, downloadStream.Position);
            Assert.Equal(payload, result.ToArray());
        }
        finally
        {
            await DeleteObjectIfPresentAsync(objectService, context, objectKey);
        }
    }

    [StorjIntegrationFact]
    public async Task GetObjectAsStream_downloads_stream_without_buffering_the_operation()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var objectKey = $"integration-tests/{Guid.NewGuid():N}.bin";
        var payload = Enumerable.Range(0, 200_000)
            .Select(index => (byte)(index % 251))
            .ToArray();

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);

            var upload = await objectService.UploadObjectAsync(context.BucketName, objectKey, payload, startImmediately: false);
            var uploadTask = upload.StartUploadAsync();
            Assert.NotNull(uploadTask);
            await uploadTask;
            Assert.True(upload.Completed);

            using var fullStream = await objectService.GetObjectAsStream(context.BucketName, objectKey);
            using var fullResult = new MemoryStream();
            var buffer = new byte[4096];

            int bytesRead;
            while ((bytesRead = await fullStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                fullResult.Write(buffer, 0, bytesRead);

            Assert.Equal(payload.Length, fullStream.Length);
            Assert.Equal(payload.Length, fullStream.Position);
            Assert.Equal(payload, fullResult.ToArray());

            using var rangeStream = await objectService.GetObjectAsStream(
                context.BucketName,
                objectKey,
                new DownloadOptions
                {
                    Offset = 1234,
                    Length = 4096
                });
            using var rangeResult = new MemoryStream();
            await rangeStream.CopyToAsync(rangeResult);

            Assert.Equal(4096, rangeStream.Length);
            Assert.Equal(payload.Skip(1234).Take(4096).ToArray(), rangeResult.ToArray());
        }
        finally
        {
            try
            {
                await objectService.DeleteObjectAsync(context.BucketName, objectKey);
            }
            catch (ObjectNotFoundException)
            {
            }
        }
    }

    private static async Task DeleteObjectIfPresentAsync(
        ObjectService objectService,
        IntegrationTestContext context,
        string objectKey)
    {
        try
        {
            await objectService.DeleteObjectAsync(context.BucketName, objectKey);
        }
        catch (ObjectNotFoundException)
        {
            // The object may not exist if upload/setup failed before it was committed.
        }
    }
}
