using uplink.NET.Models;

namespace uplink.NET.Interfaces;

public interface IObjectService
{
    // ── Upload ────────────────────────────────────────────────────────────────
    Task<UploadOperation> UploadObjectAsync(string bucketName, string key, byte[] objectData);
    Task<UploadOperation> UploadObjectAsync(string bucketName, string key, byte[] objectData, bool startImmediately);
    Task<UploadOperation> UploadObjectAsync(string bucketName, string key, byte[] objectData, UploadOptions uploadOptions);
    Task<UploadOperation> UploadObjectAsync(string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, bool startImmediately);
    Task<UploadOperation> UploadObjectAsync(string bucketName, string key, byte[] objectData, CustomMetadata customMetadata);
    Task<UploadOperation> UploadObjectAsync(string bucketName, string key, byte[] objectData, CustomMetadata customMetadata, bool startImmediately);
    Task<UploadOperation> UploadObjectAsync(string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, CustomMetadata customMetadata);
    Task<UploadOperation> UploadObjectAsync(string bucketName, string key, byte[] objectData, UploadOptions uploadOptions, CustomMetadata customMetadata, bool startImmediately);
    Task<UploadOperation> UploadObjectAsync(string bucketName, string key, Stream stream, UploadOptions? uploadOptions, CustomMetadata? customMetadata, bool startImmediately);

    // ── Chunked upload ────────────────────────────────────────────────────────
    Task<ChunkedUploadOperation> UploadObjectChunkedAsync(string bucketName, string key, UploadOptions? uploadOptions, CustomMetadata? customMetadata);

    // ── List ──────────────────────────────────────────────────────────────────
    Task<ObjectList> ListObjectsAsync(string bucketName);
    Task<ObjectList> ListObjectsAsync(string bucketName, ListObjectsOptions listObjectsOptions);

    // ── Stat ──────────────────────────────────────────────────────────────────
    Task<StorjObject> GetObjectAsync(string bucketName, string key);
    Task<DownloadStream> GetObjectAsStream(string bucketName, string key);
    Task<DownloadStream> GetObjectAsStream(string bucketName, string key, DownloadOptions downloadOptions);

    // ── Download ──────────────────────────────────────────────────────────────
    Task<DownloadOperation> DownloadObjectAsync(string bucketName, string key, bool startImmediately);
    Task<DownloadOperation> DownloadObjectAsync(string bucketName, string key, DownloadOptions downloadOptions, bool startImmediately);

    // ── Delete ────────────────────────────────────────────────────────────────
    Task DeleteObjectAsync(string bucketName, string key);
}
