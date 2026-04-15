using uplink.NET.Exceptions;
using uplink.NET.Interfaces;
using uplink.NET.Ipc;
using uplink.NET.Models;
using uplink.NET.Native;

namespace uplink.NET.Services;

public class BucketService : IBucketService
{
    private readonly Access _access;

    public BucketService(Access access)
    {
        _access = access ?? throw new ArgumentNullException(nameof(access));
    }

    public async Task<Bucket> CreateBucketAsync(string bucketName)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]         = "bucket_create",
            ["project_id"] = projectLease.Handle,
            ["name"]       = bucketName
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new BucketCreationException(bucketName, result.ErrorMessage!);

        return ParseBucket(result);
    }

    public async Task<Bucket> EnsureBucketAsync(string bucketName)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]         = "bucket_ensure",
            ["project_id"] = projectLease.Handle,
            ["name"]       = bucketName
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new BucketCreationException(bucketName, result.ErrorMessage!);

        return ParseBucket(result);
    }

    public async Task<Bucket> GetBucketAsync(string bucketName)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]         = "bucket_stat",
            ["project_id"] = projectLease.Handle,
            ["name"]       = bucketName
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new BucketNotFoundException(bucketName, result.ErrorMessage!);

        return ParseBucket(result);
    }

    public async Task<BucketList> ListBucketsAsync(ListBucketsOptions listBucketsOptions)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]         = "bucket_list",
            ["project_id"] = projectLease.Handle,
            ["cursor"]     = listBucketsOptions.Cursor ?? string.Empty
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new BucketListException(result.ErrorMessage!);

        var list = new BucketList();
        if (result.Data.TryGetProperty("buckets", out var bucketsElem))
        {
            foreach (var b in bucketsElem.EnumerateArray())
            {
                list.Items.Add(new Bucket
                {
                    Name    = b.TryGetProperty("name",    out var n) ? n.GetString() ?? string.Empty : string.Empty,
                    Created = b.TryGetProperty("created", out var c)
                        ? DateTimeOffset.FromUnixTimeSeconds(c.GetInt64()).UtcDateTime
                        : DateTime.MinValue
                });
            }
        }

        return list;
    }

    public async Task DeleteBucketAsync(string bucketName)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]         = "bucket_delete",
            ["project_id"] = projectLease.Handle,
            ["name"]       = bucketName
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new BucketDeletionException(bucketName, result.ErrorMessage!);
    }

    public async Task DeleteBucketWithObjectsAsync(string bucketName)
    {
        using var projectLease = _access.AcquireProjectLease();
        var result = await NativeWorkerProcess.Instance.SendAsync(new Dictionary<string, object?>
        {
            ["op"]         = "bucket_delete_with_objects",
            ["project_id"] = projectLease.Handle,
            ["name"]       = bucketName
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new BucketDeletionException(bucketName, result.ErrorMessage!);
    }

    private static Bucket ParseBucket(IpcResult result) => new Bucket
    {
        Name    = result.Data.TryGetProperty("bucket_name",    out var n) ? n.GetString() ?? string.Empty : string.Empty,
        Created = result.Data.TryGetProperty("bucket_created", out var c)
            ? DateTimeOffset.FromUnixTimeSeconds(c.GetInt64()).UtcDateTime
            : DateTime.MinValue
    };
}
