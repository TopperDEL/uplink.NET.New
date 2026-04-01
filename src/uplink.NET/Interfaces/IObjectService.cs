using uplink.NET.Models;

namespace uplink.NET.Interfaces;

public interface IObjectService
{
    // ── Upload ────────────────────────────────────────────────────────────────
    Task<UploadOperation> UploadObjectAsync(Access access, string bucketName, string key, byte[] objectData);
    Task<UploadOperation> UploadObjectAsync(Access access, string bucketName, string key, byte[] objectData, bool startImmediately);
    Task<UploadOperation> UploadObjectAsync(Access access, string bucketName, string key, byte[] objectData, UploadOptions uploadOptions);
    Task<UploadOperation> UploadObjectAsync(Access access, string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, bool startImmediately);
    Task<UploadOperation> UploadObjectAsync(Access access, string bucketName, string key, byte[] objectData, CustomMetadata customMetadata);
    Task<UploadOperation> UploadObjectAsync(Access access, string bucketName, string key, byte[] objectData, CustomMetadata customMetadata, bool startImmediately);
    Task<UploadOperation> UploadObjectAsync(Access access, string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, CustomMetadata customMetadata);
    Task<UploadOperation> UploadObjectAsync(Access access, string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, CustomMetadata customMetadata, bool startImmediately);
    Task<UploadOperation> UploadObjectAsync(Access access, string bucketName, string key, Stream stream, UploadOptions? uploadOptions, CustomMetadata? customMetadata, bool startImmediately);

    // ── Chunked upload ────────────────────────────────────────────────────────
    Task<ChunkedUploadOperation> UploadObjectChunkedAsync(Access access, string bucketName, string key, UploadOptions? uploadOptions, CustomMetadata? customMetadata);

    // ── List ──────────────────────────────────────────────────────────────────
    Task<ObjectList> ListObjectsAsync(Access access, string bucketName);
    Task<ObjectList> ListObjectsAsync(Access access, string bucketName, ListObjectsOptions listObjectsOptions);

    // ── Stat ──────────────────────────────────────────────────────────────────
    Task<StorjObject> GetObjectAsync(Access access, string bucketName, string key);

    // ── Download ──────────────────────────────────────────────────────────────
    Task<DownloadOperation> DownloadObjectAsync(Access access, string bucketName, string key, bool startImmediately);
    Task<DownloadOperation> DownloadObjectAsync(Access access, string bucketName, string key, DownloadOptions downloadOptions, bool startImmediately);

    // ── Delete ────────────────────────────────────────────────────────────────
    Task DeleteObjectAsync(Access access, string bucketName, string key);
}
