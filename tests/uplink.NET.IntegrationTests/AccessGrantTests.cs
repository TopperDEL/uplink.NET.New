using uplink.NET.Exceptions;
using uplink.NET.IntegrationTests.Infrastructure;
using uplink.NET.Models;
using uplink.NET.Services;

namespace uplink.NET.IntegrationTests;

public class AccessGrantTests
{
    [StorjIntegrationFact]
    public async Task Share_creates_serializable_subaccess_limited_to_prefix()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var prefix = $"integration-tests/share/{Guid.NewGuid():N}/";
        var allowedObjectKey = $"{prefix}allowed.txt";
        var blockedObjectKey = $"integration-tests/share/{Guid.NewGuid():N}/blocked.txt";
        var payload = System.Text.Encoding.UTF8.GetBytes("shared access payload");

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);

            var allowedUpload = await objectService.UploadObjectAsync(context.BucketName, allowedObjectKey, payload, startImmediately: false);
            var allowedUploadTask = allowedUpload.StartUploadAsync();
            Assert.NotNull(allowedUploadTask);
            await allowedUploadTask;

            var blockedUpload = await objectService.UploadObjectAsync(context.BucketName, blockedObjectKey, payload, startImmediately: false);
            var blockedUploadTask = blockedUpload.StartUploadAsync();
            Assert.NotNull(blockedUploadTask);
            await blockedUploadTask;

            using var sharedAccess = context.Access.Share(
                new Permission
                {
                    AllowDownload = true
                },
                new List<SharePrefix>
                {
                    new() { Bucket = context.BucketName, Prefix = prefix }
                });

            var serialized = sharedAccess.Serialize();
            Assert.False(string.IsNullOrWhiteSpace(serialized));

            using var reparsedAccess = new Access(serialized);
            var reparsedObjectService = new ObjectService(reparsedAccess);

            var allowedObject = await reparsedObjectService.GetObjectAsync(context.BucketName, allowedObjectKey);
            Assert.Equal(allowedObjectKey, allowedObject.Key);

            await Assert.ThrowsAnyAsync<Exception>(() => reparsedObjectService.GetObjectAsync(context.BucketName, blockedObjectKey));
        }
        finally
        {
            try
            {
                await objectService.DeleteObjectAsync(context.BucketName, allowedObjectKey);
            }
            catch (ObjectNotFoundException)
            {
            }

            try
            {
                await objectService.DeleteObjectAsync(context.BucketName, blockedObjectKey);
            }
            catch (ObjectNotFoundException)
            {
            }
        }
    }

    [StorjIntegrationFact]
    public async Task RevokeAsync_revokes_shared_subaccess()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var prefix = $"integration-tests/revoke/{Guid.NewGuid():N}/";
        var objectKey = $"{prefix}revoked.txt";
        var payload = System.Text.Encoding.UTF8.GetBytes("revocation payload");

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);

            var upload = await objectService.UploadObjectAsync(context.BucketName, objectKey, payload, startImmediately: false);
            var uploadTask = upload.StartUploadAsync();
            Assert.NotNull(uploadTask);
            await uploadTask;

            using var childAccess = context.Access.Share(
                new Permission
                {
                    AllowDownload = true
                },
                new List<SharePrefix>
                {
                    new() { Bucket = context.BucketName, Prefix = prefix }
                });

            var serializedChildAccess = childAccess.Serialize();
            var childObjectService = new ObjectService(childAccess);
            var childObject = await childObjectService.GetObjectAsync(context.BucketName, objectKey);
            Assert.Equal(objectKey, childObject.Key);

            await context.Access.RevokeAsync(childAccess);
            await AssertRevokedAsync(serializedChildAccess, context.BucketName, objectKey);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, objectKey);
        }
    }

    [StorjIntegrationFact]
    public async Task Share_creates_nested_subaccess_limited_to_deeper_prefix()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var parentPrefix = $"integration-tests/share-nested/{Guid.NewGuid():N}/";
        var childPrefix = $"{parentPrefix}child/";
        var allowedObjectKey = $"{childPrefix}allowed.txt";
        var blockedObjectKey = $"{parentPrefix}blocked.txt";
        var payload = System.Text.Encoding.UTF8.GetBytes("nested shared access payload");

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, allowedObjectKey, payload);
            await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, blockedObjectKey, payload);

            using var parentSharedAccess = context.Access.Share(
                new Permission
                {
                    AllowDownload = true
                },
                new SharePrefix { Bucket = context.BucketName, Prefix = parentPrefix });
            using var childSharedAccess = parentSharedAccess.Share(
                new Permission
                {
                    AllowDownload = true
                },
                new SharePrefix { Bucket = context.BucketName, Prefix = childPrefix });

            var serializedChildAccess = childSharedAccess.Serialize();
            Assert.False(string.IsNullOrWhiteSpace(serializedChildAccess));

            using var reparsedChildAccess = new Access(serializedChildAccess);
            var childObjectService = new ObjectService(reparsedChildAccess);

            var allowedObject = await childObjectService.GetObjectAsync(context.BucketName, allowedObjectKey);
            Assert.Equal(allowedObjectKey, allowedObject.Key);

            await Assert.ThrowsAnyAsync<Exception>(() => childObjectService.GetObjectAsync(context.BucketName, blockedObjectKey));
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, allowedObjectKey);
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, blockedObjectKey);
        }
    }

    [StorjIntegrationFact]
    public async Task RevokeAsync_on_subaccess_revokes_only_its_descendant()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var parentPrefix = $"integration-tests/revoke-nested/{Guid.NewGuid():N}/";
        var childPrefix = $"{parentPrefix}child/";
        var objectKey = $"{childPrefix}revoked.txt";
        var payload = System.Text.Encoding.UTF8.GetBytes("nested revocation payload");

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            await StorjTestHelper.UploadBytesAsync(objectService, context.BucketName, objectKey, payload);

            using var parentSharedAccess = context.Access.Share(
                new Permission
                {
                    AllowDownload = true
                },
                new SharePrefix { Bucket = context.BucketName, Prefix = parentPrefix });
            using var childSharedAccess = parentSharedAccess.Share(
                new Permission
                {
                    AllowDownload = true
                },
                new SharePrefix { Bucket = context.BucketName, Prefix = childPrefix });

            var serializedParentSharedAccess = parentSharedAccess.Serialize();
            var serializedChildSharedAccess = childSharedAccess.Serialize();

            Assert.Equal(objectKey, (await new ObjectService(parentSharedAccess).GetObjectAsync(context.BucketName, objectKey)).Key);
            Assert.Equal(objectKey, (await new ObjectService(childSharedAccess).GetObjectAsync(context.BucketName, objectKey)).Key);

            await parentSharedAccess.RevokeAsync(childSharedAccess);

            Assert.Equal(objectKey, (await new ObjectService(parentSharedAccess).GetObjectAsync(context.BucketName, objectKey)).Key);
            using var reparsedParentSharedAccess = new Access(serializedParentSharedAccess);
            Assert.Equal(objectKey, (await new ObjectService(reparsedParentSharedAccess).GetObjectAsync(context.BucketName, objectKey)).Key);

            await AssertRevokedAsync(serializedChildSharedAccess, context.BucketName, objectKey);
        }
        finally
        {
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, objectKey);
        }
    }

    private static async Task AssertRevokedAsync(string serializedChildAccess, string bucketName, string objectKey)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var revokedAccess = new Access(serializedChildAccess);
                var objectService = new ObjectService(revokedAccess);
                await objectService.GetObjectAsync(bucketName, objectKey);
            }
            catch (Exception ex) when (ex is AccessException or ObjectNotFoundException or IOException)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException("Timed out waiting for the revoked access grant to become unusable.");
    }
}
