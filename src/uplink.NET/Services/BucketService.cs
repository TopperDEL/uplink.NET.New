using System.Runtime.InteropServices;
using uplink.NET.Exceptions;
using uplink.NET.Interfaces;
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

    public Task<Bucket> CreateBucketAsync(string bucketName)
        => Task.Run(() => CreateBucket(bucketName));

    public Task<Bucket> EnsureBucketAsync(string bucketName)
        => Task.Run(() => EnsureBucket(bucketName));

    public Task<Bucket> GetBucketAsync(string bucketName)
        => Task.Run(() => StatBucket(bucketName));

    public Task<BucketList> ListBucketsAsync(ListBucketsOptions listBucketsOptions)
        => Task.Run(() => ListBuckets(listBucketsOptions));

    public Task DeleteBucketAsync(string bucketName)
        => Task.Run(() => DeleteBucket(bucketName));

    public Task DeleteBucketWithObjectsAsync(string bucketName)
        => Task.Run(() => DeleteBucketWithObjects(bucketName));

    // ── Private sync implementations ─────────────────────────────────────────

    private Bucket CreateBucket(string bucketName)
    {
        var result = UplinkInterop.uplink_create_bucket(_access._projectHandle, bucketName);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                throw new BucketCreationException(bucketName, msg);
            }
            return UplinkInterop.MarshalBucket(result.bucket);
        }
        finally
        {
            UplinkInterop.uplink_free_bucket_result(result);
        }
    }

    private Bucket EnsureBucket(string bucketName)
    {
        var result = UplinkInterop.uplink_ensure_bucket(_access._projectHandle, bucketName);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                throw new BucketCreationException(bucketName, msg);
            }
            return UplinkInterop.MarshalBucket(result.bucket);
        }
        finally
        {
            UplinkInterop.uplink_free_bucket_result(result);
        }
    }

    private Bucket StatBucket(string bucketName)
    {
        var result = UplinkInterop.uplink_stat_bucket(_access._projectHandle, bucketName);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                throw new BucketNotFoundException(bucketName, msg);
            }
            return UplinkInterop.MarshalBucket(result.bucket);
        }
        finally
        {
            UplinkInterop.uplink_free_bucket_result(result);
        }
    }

    private unsafe BucketList ListBuckets(ListBucketsOptions opts)
    {
        var nativeOpts = new UplinkInterop.UplinkListBucketsOptions
        {
            cursor = opts.Cursor != null
                ? Marshal.StringToCoTaskMemUTF8(opts.Cursor)
                : nint.Zero
        };

        nint iterator = UplinkInterop.uplink_list_buckets(_access._projectHandle, &nativeOpts);

        if (nativeOpts.cursor != nint.Zero)
            Marshal.FreeCoTaskMem(nativeOpts.cursor);

        var list = new BucketList();
        try
        {
            while (UplinkInterop.uplink_bucket_iterator_next(iterator))
            {
                nint bucketPtr = UplinkInterop.uplink_bucket_iterator_item(iterator);
                list.Items.Add(UplinkInterop.MarshalBucket(bucketPtr));
            }

            nint errPtr = UplinkInterop.uplink_bucket_iterator_err(iterator);
            if (errPtr != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeError(errPtr);
                throw new BucketListException(msg);
            }
        }
        finally
        {
            UplinkInterop.uplink_free_bucket_iterator(iterator);
        }

        return list;
    }

    private void DeleteBucket(string bucketName)
    {
        var result = UplinkInterop.uplink_delete_bucket(_access._projectHandle, bucketName);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                throw new BucketDeletionException(bucketName, msg);
            }
        }
        finally
        {
            UplinkInterop.uplink_free_bucket_result(result);
        }
    }

    private void DeleteBucketWithObjects(string bucketName)
    {
        var result = UplinkInterop.uplink_delete_bucket_with_objects(_access._projectHandle, bucketName);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, _) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                throw new BucketDeletionException(bucketName, msg);
            }
        }
        finally
        {
            UplinkInterop.uplink_free_bucket_result(result);
        }
    }
}
