using uplink.NET.Exceptions;
using uplink.NET.Models;
using uplink.NET.Services;

namespace uplink.NET.IntegrationTests;

internal static class StorjTestHelper
{
    public static string CreateObjectKey(string category, string extension = "bin")
        => $"integration-tests/{category}/{Guid.NewGuid():N}.{extension}";

    public static string CreatePrefix(string category)
        => $"integration-tests/{category}/{Guid.NewGuid():N}/";

    public static string CreateBucketName(string category)
    {
        var normalizedCategory = new string(category
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .Take(10)
            .ToArray());

        if (string.IsNullOrWhiteSpace(normalizedCategory))
            normalizedCategory = "test";

        var suffix = Guid.NewGuid().ToString("N")[..20];
        return $"uplink-{normalizedCategory}-{suffix}";
    }

    public static async Task UploadBytesAsync(
        ObjectService objectService,
        string bucketName,
        string objectKey,
        byte[] payload,
        CustomMetadata? metadata = null)
    {
        var upload = metadata == null
            ? await objectService.UploadObjectAsync(bucketName, objectKey, payload, startImmediately: false)
            : await objectService.UploadObjectAsync(bucketName, objectKey, payload, new UploadOptions(), metadata, startImmediately: false);

        var uploadTask = upload.StartUploadAsync();
        Assert.NotNull(uploadTask);
        await uploadTask!;

        Assert.True(upload.Completed, upload.ErrorMessage);
        Assert.False(upload.Failed);
        Assert.False(upload.Cancelled);
        Assert.Equal(payload.Length, upload.BytesSent);
    }

    public static async Task UploadStreamAsync(
        ObjectService objectService,
        string bucketName,
        string objectKey,
        Stream stream,
        CustomMetadata? metadata = null)
    {
        var upload = await objectService.UploadObjectAsync(
            bucketName,
            objectKey,
            stream,
            uploadOptions: null,
            customMetadata: metadata,
            startImmediately: false);

        var uploadTask = upload.StartUploadAsync();
        Assert.NotNull(uploadTask);
        await uploadTask!;

        Assert.True(upload.Completed, upload.ErrorMessage);
        Assert.False(upload.Failed);
        Assert.False(upload.Cancelled);
    }

    public static async Task<byte[]> DownloadBytesAsync(
        ObjectService objectService,
        string bucketName,
        string objectKey,
        DownloadOptions? options = null)
    {
        var download = await objectService.DownloadObjectAsync(
            bucketName,
            objectKey,
            options ?? new DownloadOptions(),
            startImmediately: false);

        var downloadTask = download.StartDownloadAsync();
        Assert.NotNull(downloadTask);
        await downloadTask!;

        Assert.True(download.Completed, download.ErrorMessage);
        Assert.False(download.Failed);
        Assert.False(download.Cancelled);
        return download.DownloadedBytes;
    }

    public static async Task DeleteObjectIfPresentAsync(
        ObjectService objectService,
        string bucketName,
        string objectKey)
    {
        try
        {
            await objectService.DeleteObjectAsync(bucketName, objectKey);
        }
        catch (ObjectNotFoundException)
        {
        }
    }

    public static async Task DeleteBucketIfPresentAsync(
        BucketService bucketService,
        string bucketName)
    {
        try
        {
            await bucketService.DeleteBucketWithObjectsAsync(bucketName);
        }
        catch (BucketDeletionException)
        {
        }
    }

    public static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout,
        string failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
                return;

            await Task.Delay(200);
        }

        throw new TimeoutException(failureMessage);
    }
}
