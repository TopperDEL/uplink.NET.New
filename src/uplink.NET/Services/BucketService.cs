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
        using var trace = _access.Trace("uplink_create_bucket", ("bucket", bucketName));
        var result = UplinkInterop.uplink_create_bucket(projectHandle, bucketName);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                trace?.NativeError(msg, code);
                throw new BucketCreationException(bucketName, msg);
            }

            if (result.bucket == nint.Zero)
            {
                trace?.Fail("Native library returned a null bucket result without an error.");
                throw new BucketCreationException(bucketName, "Native library returned a null bucket result without an error.");
            }

            trace?.Success();
            return UplinkInterop.MarshalBucket(result.bucket);
        }
        finally
        {
            UplinkInterop.uplink_free_bucket_result(result);
        }
    }

    private Bucket EnsureBucket(nint projectHandle, string bucketName)
    {
        using var trace = _access.Trace("uplink_ensure_bucket", ("bucket", bucketName));
        var result = UplinkInterop.uplink_ensure_bucket(projectHandle, bucketName);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                trace?.NativeError(msg, code);
                throw new BucketCreationException(bucketName, msg);
            }

            if (result.bucket == nint.Zero)
            {
                trace?.Fail("Native library returned a null bucket result without an error.");
                throw new BucketCreationException(bucketName, "Native library returned a null bucket result without an error.");
            }

            trace?.Success();
            return UplinkInterop.MarshalBucket(result.bucket);
        }
        finally
        {
            UplinkInterop.uplink_free_bucket_result(result);
        }
    }

    private Bucket StatBucket(nint projectHandle, string bucketName)
    {
        using var trace = _access.Trace("uplink_stat_bucket", ("bucket", bucketName));
        var result = UplinkInterop.uplink_stat_bucket(projectHandle, bucketName);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                trace?.NativeError(msg, code);
                throw new BucketNotFoundException(bucketName, msg);
            }

            if (result.bucket == nint.Zero)
            {
                trace?.Fail("Native library returned a null bucket result without an error.");
                throw new BucketNotFoundException(bucketName, "Native library returned a null bucket result without an error.");
            }

            trace?.Success();
            return UplinkInterop.MarshalBucket(result.bucket);
        }
        finally
        {
            UplinkInterop.uplink_free_bucket_result(result);
        }
    }

    private unsafe BucketList ListBuckets(nint projectHandle, ListBucketsOptions opts)
    {
        using var trace = _access.Trace("uplink_list_buckets", ("cursor", opts.Cursor ?? string.Empty));
        var nativeOpts = new UplinkInterop.UplinkListBucketsOptions
        {
            cursor = opts.Cursor != null
                ? Marshal.StringToCoTaskMemUTF8(opts.Cursor)
                : nint.Zero
        };

        nint iterator = UplinkInterop.uplink_list_buckets(projectHandle, &nativeOpts);

        if (nativeOpts.cursor != nint.Zero)
            UplinkInterop.FreeCoTaskMem(nativeOpts.cursor);

        var list = new BucketList();
        try
        {
            if (iterator == nint.Zero)
            {
                trace?.Fail("Native library returned a null bucket iterator without an error.");
                throw new BucketListException("Native library returned a null bucket iterator without an error.");
            }

            while (UplinkInterop.uplink_bucket_iterator_next(iterator))
            {
                nint bucketPtr = UplinkInterop.uplink_bucket_iterator_item(iterator);
                list.Items.Add(UplinkInterop.MarshalBucket(bucketPtr));
            }

            nint errPtr = UplinkInterop.uplink_bucket_iterator_err(iterator);
            if (errPtr != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeError(errPtr);
                trace?.NativeError(msg, code);
                throw new BucketListException(msg);
            }

            trace?.Success();
        }
        finally
        {
            UplinkInterop.uplink_free_bucket_iterator(iterator);
        }

        return list;
    }

    private void DeleteBucket(nint projectHandle, string bucketName)
    {
        using var trace = _access.Trace("uplink_delete_bucket", ("bucket", bucketName));
        var result = UplinkInterop.uplink_delete_bucket(projectHandle, bucketName);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                trace?.NativeError(msg, code);
                throw new BucketDeletionException(bucketName, msg);
            }

            trace?.Success();
        }
        finally
        {
            UplinkInterop.uplink_free_bucket_result(result);
        }
    }

    private void DeleteBucketWithObjects(nint projectHandle, string bucketName)
    {
        using var trace = _access.Trace("uplink_delete_bucket_with_objects", ("bucket", bucketName));
        var result = UplinkInterop.uplink_delete_bucket_with_objects(projectHandle, bucketName);
        try
        {
            if (result.error != nint.Zero)
            {
                var (msg, code) = UplinkInterop.ConsumeErrorAndClear(ref result.error);
                trace?.NativeError(msg, code);
                throw new BucketDeletionException(bucketName, msg);
            }

            trace?.Success();
        }
        finally
        {
            UplinkInterop.uplink_free_bucket_result(result);
        }
    }
}
