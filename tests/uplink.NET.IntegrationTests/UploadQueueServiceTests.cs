using uplink.NET.IntegrationTests.Infrastructure;
using uplink.NET.Interfaces;
using uplink.NET.Models;
using uplink.NET.Services;

namespace uplink.NET.IntegrationTests;

public class UploadQueueServiceTests
{
    [StorjIntegrationFact]
    public async Task UploadObject_Uploads_2048Bytes()
    {
        await RunQueueUploadRoundtripAsync(2_048, useStreams: false, withMetadata: false);
    }

    [StorjIntegrationFact]
    public async Task UploadObject_Uploads_512KiB()
    {
        await RunQueueUploadRoundtripAsync(512 * 1_024, useStreams: false, withMetadata: false);
    }

    [StorjIntegrationFact]
    public async Task UploadObjectFromStream_Uploads_512KiB()
    {
        await RunQueueUploadRoundtripAsync(512 * 1_024, useStreams: true, withMetadata: false);
    }

    [StorjIntegrationFact]
    public async Task UploadObjectFromStreamWithMetadata_Uploads_512KiB()
    {
        await RunQueueUploadRoundtripAsync(512 * 1_024, useStreams: true, withMetadata: true);
    }

    [StorjIntegrationFact]
    public async Task UploadProvidesCorrectCount()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var accessGrant = context.Access.Serialize();
        var firstKey = StorjTestHelper.CreateObjectKey("queue-count-first");
        var secondKey = StorjTestHelper.CreateObjectKey("queue-count-second");
        var firstPayload = IntegrationTestEnvironment.CreatePayload(512 * 1_024);
        var secondPayload = IntegrationTestEnvironment.CreatePayload(512 * 1_024);
        await using var queueService = new UploadQueueService(
            Path.Combine(context.TempDirectory, "count.sqlite"),
            IntegrationTestEnvironment.CreateQueueAccessConfig(context.TempDirectory),
            Console.WriteLine);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            await queueService.AddObjectToUploadQueueAsync(context.BucketName, firstKey, accessGrant, firstPayload, "first");
            await queueService.AddObjectToUploadQueueAsync(context.BucketName, secondKey, accessGrant, secondPayload, "second");

            Assert.Equal(2, await queueService.GetOpenUploadCountAsync());

            queueService.ProcessQueueInBackground();

            await StorjTestHelper.WaitUntilAsync(
                () => queueService.UploadInProgress,
                TimeSpan.FromSeconds(30),
                "Timed out waiting for the queue to start processing uploads.");

            var countDuringProcessing = await queueService.GetOpenUploadCountAsync();
            Assert.InRange(countDuringProcessing, 1, 2);

            await StorjTestHelper.WaitUntilAsync(
                async () => await queueService.GetOpenUploadCountAsync() == 0,
                TimeSpan.FromSeconds(60),
                "Timed out waiting for the queue to finish processing uploads.");

            Assert.Equal(0, await queueService.GetOpenUploadCountAsync());
        }
        finally
        {
            queueService.StopQueueInBackground();
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, firstKey);
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, secondKey);
        }
    }

    [StorjIntegrationFact]
    public async Task UploadsWithInterruptionAndEvents()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var accessGrant = context.Access.Serialize();
        var firstKey = StorjTestHelper.CreateObjectKey("queue-interrupt-first");
        var secondKey = StorjTestHelper.CreateObjectKey("queue-interrupt-second");
        var events = new List<(QueueChangeType ChangeType, string Key)>();
        var syncRoot = new object();
        await using var queueService = new UploadQueueService(
            Path.Combine(context.TempDirectory, "interrupt.sqlite"),
            IntegrationTestEnvironment.CreateQueueAccessConfig(context.TempDirectory),
            Console.WriteLine);

        queueService.UploadQueueChangedEvent += (changeType, entry) =>
        {
            lock (syncRoot)
            {
                events.Add((changeType, entry.Key));
            }

            if (changeType == QueueChangeType.EntryRemoved && entry.Key == firstKey)
                queueService.StopQueueInBackground();
        };

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);
            await queueService.AddObjectToUploadQueueAsync(context.BucketName, firstKey, accessGrant, IntegrationTestEnvironment.CreatePayload(512 * 1_024), "first");
            await queueService.AddObjectToUploadQueueAsync(context.BucketName, secondKey, accessGrant, IntegrationTestEnvironment.CreatePayload(512 * 1_024), "second");

            queueService.ProcessQueueInBackground();

            await StorjTestHelper.WaitUntilAsync(
                async () => await queueService.GetOpenUploadCountAsync() == 1,
                TimeSpan.FromSeconds(60),
                "Timed out waiting for the queue interruption point.");

            queueService.ProcessQueueInBackground();

            await StorjTestHelper.WaitUntilAsync(
                async () => await queueService.GetOpenUploadCountAsync() == 0,
                TimeSpan.FromSeconds(60),
                "Timed out waiting for the resumed queue to finish.");

            var firstDownloaded = await StorjTestHelper.DownloadBytesAsync(objectService, context.BucketName, firstKey);
            var secondDownloaded = await StorjTestHelper.DownloadBytesAsync(objectService, context.BucketName, secondKey);

            Assert.Equal(512 * 1_024, firstDownloaded.Length);
            Assert.Equal(512 * 1_024, secondDownloaded.Length);

            lock (syncRoot)
            {
                Assert.Contains(events, entry => entry == (QueueChangeType.EntryAdded, firstKey));
                Assert.Contains(events, entry => entry == (QueueChangeType.EntryAdded, secondKey));
                Assert.Contains(events, entry => entry == (QueueChangeType.EntryRemoved, firstKey));
                Assert.Contains(events, entry => entry == (QueueChangeType.EntryRemoved, secondKey));
            }
        }
        finally
        {
            queueService.StopQueueInBackground();
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, firstKey);
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, secondKey);
        }
    }

    [StorjIntegrationFact]
    public async Task UploadsWithInterruptionAndRetry()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var accessGrant = context.Access.Serialize();
        var bucketName = StorjTestHelper.CreateBucketName("queue-retry");
        var objectKey = StorjTestHelper.CreateObjectKey("queue-retry");
        var payload = IntegrationTestEnvironment.CreatePayload(2_048);
        var events = new List<QueueChangeType>();
        var syncRoot = new object();
        await using var queueService = new UploadQueueService(
            Path.Combine(context.TempDirectory, "retry.sqlite"),
            IntegrationTestEnvironment.CreateQueueAccessConfig(context.TempDirectory),
            Console.WriteLine);

        queueService.UploadQueueChangedEvent += (changeType, _) =>
        {
            lock (syncRoot)
            {
                events.Add(changeType);
            }
        };

        try
        {
            await queueService.AddObjectToUploadQueueAsync(bucketName, objectKey, accessGrant, payload, "retry");
            queueService.ProcessQueueInBackground();

            await StorjTestHelper.WaitUntilAsync(
                async () => !queueService.UploadInProgress && await queueService.GetOpenUploadCountAsync() == 0,
                TimeSpan.FromSeconds(60),
                "Timed out waiting for the queue entry to fail.");

            await bucketService.CreateBucketAsync(bucketName);
            await queueService.RetryAsync(objectKey);
            Assert.Equal(1, await queueService.GetOpenUploadCountAsync());

            queueService.ProcessQueueInBackground();

            await StorjTestHelper.WaitUntilAsync(
                async () => await queueService.GetOpenUploadCountAsync() == 0,
                TimeSpan.FromSeconds(60),
                "Timed out waiting for the retried upload to finish.");

            var downloaded = await StorjTestHelper.DownloadBytesAsync(objectService, bucketName, objectKey);
            Assert.Equal(payload, downloaded);
            lock (syncRoot)
            {
                Assert.Contains(QueueChangeType.EntryUpdated, events);
                Assert.Contains(QueueChangeType.EntryRemoved, events);
            }
        }
        finally
        {
            queueService.StopQueueInBackground();
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, bucketName, objectKey);
            await StorjTestHelper.DeleteBucketIfPresentAsync(bucketService, bucketName);
        }
    }

    private static async Task RunQueueUploadRoundtripAsync(int payloadSize, bool useStreams, bool withMetadata)
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        var objectService = new ObjectService(context.Access);
        var accessGrant = context.Access.Serialize();
        var firstKey = StorjTestHelper.CreateObjectKey($"queue-{payloadSize}-first");
        var secondKey = StorjTestHelper.CreateObjectKey($"queue-{payloadSize}-second");
        var firstPayload = IntegrationTestEnvironment.CreatePayload(payloadSize);
        var secondPayload = IntegrationTestEnvironment.CreatePayload(payloadSize);
        var metadata = withMetadata
            ? new CustomMetadata { Entries = { ["origin"] = "queue-test", ["size"] = payloadSize.ToString() } }
            : null;
        await using var queueService = new UploadQueueService(
            Path.Combine(context.TempDirectory, $"{payloadSize}-{useStreams}-{withMetadata}.sqlite"),
            IntegrationTestEnvironment.CreateQueueAccessConfig(context.TempDirectory),
            Console.WriteLine);

        try
        {
            await bucketService.EnsureBucketAsync(context.BucketName);

            if (useStreams)
            {
                await using var firstStream = new MemoryStream(firstPayload, writable: false);
                await using var secondStream = new MemoryStream(secondPayload, writable: false);
                if (metadata == null)
                {
                    await queueService.AddObjectToUploadQueueAsync(context.BucketName, firstKey, accessGrant, firstStream, "first");
                    await queueService.AddObjectToUploadQueueAsync(context.BucketName, secondKey, accessGrant, secondStream, "second");
                }
                else
                {
                    await queueService.AddObjectToUploadQueueAsync(context.BucketName, firstKey, accessGrant, firstStream, "first", metadata);
                    await queueService.AddObjectToUploadQueueAsync(context.BucketName, secondKey, accessGrant, secondStream, "second", metadata);
                }
            }
            else
            {
                if (metadata == null)
                {
                    await queueService.AddObjectToUploadQueueAsync(context.BucketName, firstKey, accessGrant, firstPayload, "first");
                    await queueService.AddObjectToUploadQueueAsync(context.BucketName, secondKey, accessGrant, secondPayload, "second");
                }
                else
                {
                    await queueService.AddObjectToUploadQueueAsync(context.BucketName, firstKey, accessGrant, firstPayload, "first", metadata);
                    await queueService.AddObjectToUploadQueueAsync(context.BucketName, secondKey, accessGrant, secondPayload, "second", metadata);
                }
            }

            queueService.ProcessQueueInBackground();

            await StorjTestHelper.WaitUntilAsync(
                async () => await queueService.GetOpenUploadCountAsync() == 0,
                TimeSpan.FromSeconds(60),
                "Timed out waiting for queued uploads to finish.");

            var firstDownloaded = await StorjTestHelper.DownloadBytesAsync(objectService, context.BucketName, firstKey);
            var secondDownloaded = await StorjTestHelper.DownloadBytesAsync(objectService, context.BucketName, secondKey);

            Assert.Equal(firstPayload, firstDownloaded);
            Assert.Equal(secondPayload, secondDownloaded);

            if (withMetadata)
            {
                var firstObject = await objectService.GetObjectAsync(context.BucketName, firstKey);
                var secondObject = await objectService.GetObjectAsync(context.BucketName, secondKey);

                Assert.Equal("queue-test", firstObject.CustomMetadata?.Entries["origin"]);
                Assert.Equal("queue-test", secondObject.CustomMetadata?.Entries["origin"]);
            }
        }
        finally
        {
            queueService.StopQueueInBackground();
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, firstKey);
            await StorjTestHelper.DeleteObjectIfPresentAsync(objectService, context.BucketName, secondKey);
        }
    }
}
