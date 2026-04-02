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
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            try
            {
                return CreateBucket(projectLease.Handle, bucketName);
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    public Task<Bucket> EnsureBucketAsync(string bucketName)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            try
            {
                return EnsureBucket(projectLease.Handle, bucketName);
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    public Task<Bucket> GetBucketAsync(string bucketName)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            try
            {
                return StatBucket(projectLease.Handle, bucketName);
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    public Task<BucketList> ListBucketsAsync(ListBucketsOptions listBucketsOptions)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            try
            {
                return ListBuckets(projectLease.Handle, listBucketsOptions);
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    public Task DeleteBucketAsync(string bucketName)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            try
            {
                DeleteBucket(projectLease.Handle, bucketName);
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    public Task DeleteBucketWithObjectsAsync(string bucketName)
    {
        var projectLease = _access.AcquireProjectLease();
        return Task.Run(() =>
        {
            try
            {
                DeleteBucketWithObjects(projectLease.Handle, bucketName);
            }
            finally
            {
                projectLease.Dispose();
            }
        });
    }

    // ── Private sync implementations ─────────────────────────────────────────

    private Bucket CreateBucket(nint projectHandle, string bucketName)
    {
        var result = UplinkInterop.uplink_create_bucket(projectHandle, bucketName);
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

    private Bucket EnsureBucket(nint projectHandle, string bucketName)
    {
        var result = UplinkInterop.uplink_ensure_bucket(projectHandle, bucketName);
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

    private Bucket StatBucket(nint projectHandle, string bucketName)
    {
        var result = UplinkInterop.uplink_stat_bucket(projectHandle, bucketName);
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

    private unsafe BucketList ListBuckets(nint projectHandle, ListBucketsOptions opts)
    {
        var nativeOpts = new UplinkInterop.UplinkListBucketsOptions
        {
            cursor = opts.Cursor != null
                ? Marshal.StringToCoTaskMemUTF8(opts.Cursor)
                : nint.Zero
        };

        nint iterator = UplinkInterop.uplink_list_buckets(projectHandle, &nativeOpts);

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

    private void DeleteBucket(nint projectHandle, string bucketName)
    {
        var result = UplinkInterop.uplink_delete_bucket(projectHandle, bucketName);
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

    private void DeleteBucketWithObjects(nint projectHandle, string bucketName)
    {
        var result = UplinkInterop.uplink_delete_bucket_with_objects(projectHandle, bucketName);
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
