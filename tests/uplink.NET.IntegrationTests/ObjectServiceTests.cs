using System.Text;
using uplink.NET.Exceptions;
using uplink.NET.IntegrationTests.Infrastructure;
using uplink.NET.Models;
using uplink.NET.Services;

namespace uplink.NET.IntegrationTests;

public class ObjectServiceTests
{
    public static TheoryData<int> UploadSizes => new()
    {
        256,
        2_048,
        2_500,
        512 * 1_024,
        6 * 1_024 * 1_024
    };

    public static TheoryData<int> StreamUploadSizes => new()
    {
        256,
        2_048,
        2_500,
        512 * 1_024
    };

    public static TheoryData<int> DownloadSizes => new()
    {
        4,
        256,
        2_048,
        2_500,
        512 * 1_024
    };

    [StorjIntegrationTheory]
    [MemberData(nameof(UploadSizes))]
    public async Task UploadObject_Uploads_ExpectedBytes(int sizeInBytes)
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var objectKey = StorjTestHelper.CreateObjectKey($"upload-{sizeInBytes}");
        var payload = IntegrationTestEnvironment.CreatePayload(sizeInBytes);
        var progressEventCount = 0;

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            var upload = await objectService.UploadObjectAsync(context.BucketName, objectKey, payload, startImmediately: false);
            upload.UploadOperationProgressChanged += _ => Interlocked.Increment(ref progressEventCount);

            var uploadTask = upload.StartUploadAsync();
            await StorjTestHelper.RequireStarted(uploadTask, "upload");

            Assert.True(upload.Completed, upload.ErrorMessage);
            Assert.False(upload.Failed);
            Assert.False(upload.Cancelled);
            Assert.Equal(payload.Length, upload.BytesSent);
            Assert.Equal(100f, upload.PercentageCompleted);
            Assert.True(progressEventCount >= 1);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, objectKey);
        }
    }

    [StorjIntegrationTheory]
    [MemberData(nameof(StreamUploadSizes))]
    public async Task UploadObject_Uploads_ExpectedBytesAsStream(int sizeInBytes)
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var objectKey = StorjTestHelper.CreateObjectKey($"upload-stream-{sizeInBytes}");
        var payload = IntegrationTestEnvironment.CreatePayload(sizeInBytes);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);

            await using var stream = new MemoryStream(payload, writable: false);
            await StorjTestHelper.UploadStreamAsync(objectService, context.BucketName, objectKey, stream);

            var storedObject = await objectService.GetObjectAsync(context.BucketName, objectKey);
            Assert.Equal(sizeInBytes, storedObject.ContentLength);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, objectKey);
        }
    }

    [StorjIntegrationFact]
    public async Task UploadObject_ParallelUploads_Complete()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var firstKey = StorjTestHelper.CreateObjectKey("parallel-upload-first");
        var secondKey = StorjTestHelper.CreateObjectKey("parallel-upload-second");
        var firstPayload = IntegrationTestEnvironment.CreatePayload(512 * 1_024);
        var secondPayload = IntegrationTestEnvironment.CreatePayload(512 * 1_024);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);

            var firstUpload = await objectService.UploadObjectAsync(context.BucketName, firstKey, firstPayload, startImmediately: false);
            var secondUpload = await objectService.UploadObjectAsync(context.BucketName, secondKey, secondPayload, startImmediately: false);

            await Task.WhenAll(
                StorjTestHelper.RequireStarted(firstUpload.StartUploadAsync(), "first parallel upload"),
                StorjTestHelper.RequireStarted(secondUpload.StartUploadAsync(), "second parallel upload"));

            Assert.True(firstUpload.Completed);
            Assert.True(secondUpload.Completed);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, firstKey);
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, secondKey);
        }
    }

    [StorjIntegrationTheory]
    [MemberData(nameof(DownloadSizes))]
    public async Task DownloadObject_Downloads_ExpectedBytes(int sizeInBytes)
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var objectKey = StorjTestHelper.CreateObjectKey($"download-{sizeInBytes}");
        var payload = IntegrationTestEnvironment.CreatePayload(sizeInBytes);
        var progressEventCount = 0;

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, objectKey, payload);

            var download = await objectService.DownloadObjectAsync(context.BucketName, objectKey, startImmediately: false);
            download.DownloadOperationProgressChanged += _ => Interlocked.Increment(ref progressEventCount);

            var downloadTask = download.StartDownloadAsync();
            await StorjTestHelper.RequireStarted(downloadTask, "download");

            Assert.True(download.Completed, download.ErrorMessage);
            Assert.False(download.Failed);
            Assert.False(download.Cancelled);
            Assert.Equal(payload.Length, download.BytesReceived);
            Assert.Equal(payload, download.DownloadedBytes);
            Assert.True(progressEventCount >= 1);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, objectKey);
        }
    }

    [StorjIntegrationFact]
    public async Task DownloadObject_ParallelDownloads_Complete()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var firstKey = StorjTestHelper.CreateObjectKey("parallel-download-first");
        var secondKey = StorjTestHelper.CreateObjectKey("parallel-download-second");
        var firstPayload = IntegrationTestEnvironment.CreatePayload(2_048);
        var secondPayload = IntegrationTestEnvironment.CreatePayload(2_500);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, firstKey, firstPayload);
            await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, secondKey, secondPayload);

            var firstDownload = await objectService.DownloadObjectAsync(context.BucketName, firstKey, startImmediately: false);
            var secondDownload = await objectService.DownloadObjectAsync(context.BucketName, secondKey, startImmediately: false);

            await Task.WhenAll(
                StorjTestHelper.RequireStarted(firstDownload.StartDownloadAsync(), "first parallel download"),
                StorjTestHelper.RequireStarted(secondDownload.StartDownloadAsync(), "second parallel download"));

            Assert.Equal(firstPayload, firstDownload.DownloadedBytes);
            Assert.Equal(secondPayload, secondDownload.DownloadedBytes);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, firstKey);
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, secondKey);
        }
    }

    [StorjIntegrationFact]
    public async Task UploadAndDownload_CanOverlap()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var uploadKey = StorjTestHelper.CreateObjectKey("overlap-upload");
        var downloadKey = StorjTestHelper.CreateObjectKey("overlap-download");
        var uploadPayload = IntegrationTestEnvironment.CreatePayload(6 * 1_024 * 1_024);
        var downloadPayload = Encoding.UTF8.GetBytes("overlap payload");

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, downloadKey, downloadPayload);

            var upload = await objectService.UploadObjectAsync(context.BucketName, uploadKey, uploadPayload, startImmediately: false);
            var uploadTask = upload.StartUploadAsync();
            var requiredUploadTask = StorjTestHelper.RequireStarted(uploadTask, "overlapping upload");

            await StorjTestHelper.WaitUntilAsync(
                () => upload.BytesSent > 0 || upload.Completed,
                TimeSpan.FromSeconds(30),
                "Timed out waiting for the overlapping upload to begin.");

            var downloaded = await StorjTestHelper.DownloadBytesAsync(objectService, context.BucketName, downloadKey);
            await requiredUploadTask;

            Assert.Equal(downloadPayload, downloaded);
            Assert.True(upload.Completed, upload.ErrorMessage);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, uploadKey);
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, downloadKey);
        }
    }

    [StorjIntegrationFact]
    public async Task DownloadStream_Provides_First50Bytes()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var objectKey = StorjTestHelper.CreateObjectKey("download-stream-first-50");
        var payload = IntegrationTestEnvironment.CreatePayload(256);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, objectKey, payload);

            using var stream = await objectService.GetObjectAsStream(context.BucketName, objectKey);
            var firstFiftyBytes = new byte[50];
            var bytesRead = await stream.ReadAsync(firstFiftyBytes, 0, firstFiftyBytes.Length);

            Assert.Equal(50, bytesRead);
            Assert.Equal(payload.Take(50).ToArray(), firstFiftyBytes);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, objectKey);
        }
    }

    [StorjIntegrationFact]
    public async Task TransferOperations_CanRepeat_OnSameAccess()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var createdKeys = new List<string>();

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);

            for (var iteration = 0; iteration < 3; iteration++)
            {
                var objectKey = StorjTestHelper.CreateObjectKey($"repeat-{iteration}");
                var payload = IntegrationTestEnvironment.CreatePayload(2_048 + iteration);
                createdKeys.Add(objectKey);

                await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, objectKey, payload);
                var downloaded = await StorjTestHelper.DownloadBytesAsync(objectService, context.BucketName, objectKey);

                Assert.Equal(payload, downloaded);
            }
        }
        finally
        {
            foreach (var objectKey in createdKeys)
                await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, objectKey);
        }
    }

    [StorjIntegrationFact]
    public async Task ListObjects_Lists_ExistingObject()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var prefix = StorjTestHelper.CreatePrefix("list-objects");
        var firstKey = $"{prefix}first.bin";
        var secondKey = $"{prefix}second.bin";

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, firstKey, IntegrationTestEnvironment.CreatePayload(256));
            await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, secondKey, IntegrationTestEnvironment.CreatePayload(512));

            var objects = await objectService.ListObjectsAsync(
                context.BucketName,
                new ListObjectsOptions
                {
                    Prefix = prefix,
                    Recursive = true
                });

            var keys = objects.Items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            Assert.Contains(firstKey, keys);
            Assert.Contains(secondKey, keys);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, firstKey);
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, secondKey);
        }
    }

    [StorjIntegrationFact]
    public async Task GetObject_Gets_Object()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var objectKey = StorjTestHelper.CreateObjectKey("get-object");
        var payload = IntegrationTestEnvironment.CreatePayload(2_048);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, objectKey, payload);

            var storjObject = await objectService.GetObjectAsync(context.BucketName, objectKey);
            Assert.Equal(objectKey, storjObject.Key);
            Assert.Equal(payload.Length, storjObject.ContentLength);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, objectKey);
        }
    }

    [StorjIntegrationFact]
    public async Task GetObject_Fails_OnNotExistingObject()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var objectService = new ObjectService(context.Access);

        await Assert.ThrowsAsync<ObjectNotFoundException>(
            () => objectService.GetObjectAsync(context.BucketName, StorjTestHelper.CreateObjectKey("missing-object")));
    }

    [StorjIntegrationFact]
    public async Task DeleteObject_Fails_OnNotExistingObject()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var objectService = new ObjectService(context.Access);

        await Assert.ThrowsAsync<ObjectNotFoundException>(
            () => objectService.DeleteObjectAsync(context.BucketName, StorjTestHelper.CreateObjectKey("missing-delete")));
    }

    [StorjIntegrationFact]
    public async Task DeleteObject_Deletes_Object()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var objectKey = StorjTestHelper.CreateObjectKey("delete-object");
        var payload = IntegrationTestEnvironment.CreatePayload(256);

        await bucketService.EnsureBucketAsync(context.BucketName);
        await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, objectKey, payload);
        await objectService.DeleteObjectAsync(context.BucketName, objectKey);

        await Assert.ThrowsAsync<ObjectNotFoundException>(() => objectService.GetObjectAsync(context.BucketName, objectKey));
    }

    [StorjIntegrationFact]
    public async Task SetCustomMetaData_Works()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var objectKey = StorjTestHelper.CreateObjectKey("metadata");
        var payload = IntegrationTestEnvironment.CreatePayload(2_048);
        var metadata = new CustomMetadata
        {
            Entries =
            {
                ["content-type"] = "application/octet-stream",
                ["origin"] = "integration-test"
            }
        };

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, objectKey, payload, metadata);

            var storjObject = await objectService.GetObjectAsync(context.BucketName, objectKey);
            Assert.NotNull(storjObject.CustomMetadata);
            Assert.Equal("application/octet-stream", storjObject.CustomMetadata!.Entries["content-type"]);
            Assert.Equal("integration-test", storjObject.CustomMetadata.Entries["origin"]);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, objectKey);
        }
    }
}
