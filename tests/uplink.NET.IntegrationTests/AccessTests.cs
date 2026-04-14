using System.Reflection;
using System.Text;
using uplink.NET.Exceptions;
using uplink.NET.IntegrationTests.Infrastructure;
using uplink.NET.Models;
using uplink.NET.Services;

namespace uplink.NET.IntegrationTests;

public class AccessTests
{
    [StorjIntegrationFact]
    public async Task CreateValidAccess_Creates_ValidAccess()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        using var access = new Access(
            context.Access.Serialize(),
            new Config { TempDirectory = Path.Combine(context.TempDirectory, "valid-access") });

        var bucket = await new BucketService(access).EnsureBucketAsync(context.BucketName);
        Assert.Equal(context.BucketName, bucket.Name);
    }

    [StorjIntegrationFact]
    public void CreateInvalidAccess_Raises_Error()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var serialized = context.Access.Serialize();
        var replacement = serialized[^1] == 'A' ? 'B' : 'A';
        var invalidSerialized = $"{serialized[..^1]}{replacement}";

        Assert.Throws<AccessException>(() => new Access(invalidSerialized));
    }

    [StorjIntegrationFact]
    public async Task AccessShare_Creates_ValidAccess()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var prefix = StorjTestHelper.CreatePrefix("access-share-valid");
        var objectKey = $"{prefix}probe.txt";
        var payload = Encoding.UTF8.GetBytes("shared access probe");

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, objectKey, payload);

            using var sharedAccess = context.Access.Share(
                new Permission
                {
                    AllowDownload = true
                },
                new SharePrefix { Bucket = context.BucketName, Prefix = prefix });

            using var reparsedAccess = new Access(sharedAccess.Serialize());
            var sharedObject = await new ObjectService(reparsedAccess).GetObjectAsync(context.BucketName, objectKey);

            Assert.Equal(objectKey, sharedObject.Key);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, objectKey);
        }
    }

    [StorjIntegrationFact]
    public async Task AccessShare_Creates_UsableSharedAccessForUpload()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var parentObjectService = new ObjectService(context.Access);
        var prefix = StorjTestHelper.CreatePrefix("access-share-upload");
        var objectKey = $"{prefix}uploaded.bin";
        var payload = IntegrationTestEnvironment.CreatePayload(2_048);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);

            using var sharedAccess = context.Access.Share(
                new Permission
                {
                    AllowUpload = true
                },
                new SharePrefix { Bucket = context.BucketName, Prefix = prefix });

            await StorjTestHelper.UploadBytesAsync(new ObjectService(sharedAccess), context.BucketName, objectKey, payload);

            var storedObject = await parentObjectService.GetObjectAsync(context.BucketName, objectKey);
            Assert.Equal(payload.Length, storedObject.ContentLength);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(parentObjectService, context.BucketName, objectKey);
        }
    }

    [StorjIntegrationFact]
    public async Task AccessShare_Creates_UsableSharedAccessForUploadWithDisallowDeletes()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var parentObjectService = new ObjectService(context.Access);
        var prefix = StorjTestHelper.CreatePrefix("access-share-upload-nodelete");
        var objectKey = $"{prefix}uploaded.bin";
        var payload = IntegrationTestEnvironment.CreatePayload(2_048);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);

            using var sharedAccess = context.Access.Share(
                new Permission
                {
                    AllowUpload = true,
                    AllowDelete = false
                },
                new SharePrefix { Bucket = context.BucketName, Prefix = prefix });

            var sharedObjectService = new ObjectService(sharedAccess);
            await StorjTestHelper.UploadBytesAsync(sharedObjectService, context.BucketName, objectKey, payload);
            await Assert.ThrowsAnyAsync<Exception>(() => sharedObjectService.DeleteObjectAsync(context.BucketName, objectKey));

            var storedObject = await parentObjectService.GetObjectAsync(context.BucketName, objectKey);
            Assert.Equal(payload.Length, storedObject.ContentLength);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(parentObjectService, context.BucketName, objectKey);
        }
    }

    [StorjIntegrationFact]
    public async Task AccessShare_Creates_UsableSharedAccessForUploadDeep()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var parentObjectService = new ObjectService(context.Access);
        var basePrefix = StorjTestHelper.CreatePrefix("access-share-upload-deep");
        var allowedPrefix = $"{basePrefix}deep/";
        var allowedObjectKey = $"{allowedPrefix}allowed.bin";
        var blockedObjectKey = $"{basePrefix}blocked.bin";
        var payload = IntegrationTestEnvironment.CreatePayload(512);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);

            using var sharedAccess = context.Access.Share(
                new Permission
                {
                    AllowUpload = true
                },
                new SharePrefix { Bucket = context.BucketName, Prefix = allowedPrefix });

            var sharedObjectService = new ObjectService(sharedAccess);
            await StorjTestHelper.UploadBytesAsync(sharedObjectService, context.BucketName, allowedObjectKey, payload);
            await Assert.ThrowsAnyAsync<Exception>(() => StorjTestHelper.UploadBytesAsync(sharedObjectService, context.BucketName, blockedObjectKey, payload));

            var storedObject = await parentObjectService.GetObjectAsync(context.BucketName, allowedObjectKey);
            Assert.Equal(payload.Length, storedObject.ContentLength);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(parentObjectService, context.BucketName, allowedObjectKey);
            await StorjTestHelper.DeleteObjectIfPresentAsync(parentObjectService, context.BucketName, blockedObjectKey);
        }
    }

    [StorjIntegrationFact]
    public async Task AccessShare_Creates_UsableSharedAccessForDownload()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var parentObjectService = new ObjectService(context.Access);
        var prefix = StorjTestHelper.CreatePrefix("access-share-download");
        var objectKey = $"{prefix}download.bin";
        var payload = IntegrationTestEnvironment.CreatePayload(2_500);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            await StorjTestHelper.UploadBytesAsync(parentObjectService, context.BucketName, objectKey, payload);

            using var sharedAccess = context.Access.Share(
                new Permission
                {
                    AllowDownload = true
                },
                new SharePrefix { Bucket = context.BucketName, Prefix = prefix });

            var downloaded = await StorjTestHelper.DownloadBytesAsync(new ObjectService(sharedAccess), context.BucketName, objectKey);
            Assert.Equal(payload, downloaded);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(parentObjectService, context.BucketName, objectKey);
        }
    }

    [StorjIntegrationFact]
    public async Task Dispose_WaitsForActiveProjectLease()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var acquireProjectLeaseMethod = typeof(Access).GetMethod(
            "AcquireProjectLease",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(acquireProjectLeaseMethod);

        using var projectLease = (IDisposable?)acquireProjectLeaseMethod!.Invoke(context.Access, null);
        Assert.NotNull(projectLease);

        var disposeTask = Task.Run(context.Access.Dispose);

        await StorjTestHelper.WaitUntilAsync(
            () => disposeTask.Status == TaskStatus.Running,
            TimeSpan.FromSeconds(5),
            "Dispose should stay blocked while the active native project lease is held.");

        projectLease.Dispose();
        await disposeTask;
        Assert.True(disposeTask.IsCompletedSuccessfully);
    }
}
