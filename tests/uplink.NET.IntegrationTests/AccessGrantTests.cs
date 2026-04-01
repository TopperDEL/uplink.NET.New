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

            var allowedUpload = await objectService.UploadObjectAsync(context.Access, context.BucketName, allowedObjectKey, payload, startImmediately: false);
            await allowedUpload.StartUploadAsync()!;

            var blockedUpload = await objectService.UploadObjectAsync(context.Access, context.BucketName, blockedObjectKey, payload, startImmediately: false);
            await blockedUpload.StartUploadAsync()!;

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

            var allowedObject = await objectService.GetObjectAsync(reparsedAccess, context.BucketName, allowedObjectKey);
            Assert.Equal(allowedObjectKey, allowedObject.Key);

            await Assert.ThrowsAnyAsync<Exception>(() => objectService.GetObjectAsync(reparsedAccess, context.BucketName, blockedObjectKey));
        }
        finally
        {
            try
            {
                await objectService.DeleteObjectAsync(context.Access, context.BucketName, allowedObjectKey);
            }
            catch (ObjectNotFoundException)
            {
            }

            try
            {
                await objectService.DeleteObjectAsync(context.Access, context.BucketName, blockedObjectKey);
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

            var upload = await objectService.UploadObjectAsync(context.Access, context.BucketName, objectKey, payload, startImmediately: false);
            await upload.StartUploadAsync()!;

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
            var childObject = await objectService.GetObjectAsync(childAccess, context.BucketName, objectKey);
            Assert.Equal(objectKey, childObject.Key);

            await context.Access.RevokeAsync(childAccess);
            await AssertRevokedAsync(serializedChildAccess, context.BucketName, objectKey);
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
                await objectService.GetObjectAsync(revokedAccess, bucketName, objectKey);
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
