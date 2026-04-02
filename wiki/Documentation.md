# Documentation

This page keeps the structure and concepts from the previous `uplink.NET` wiki, but reflects the API surface of the current library.

## Basic terminology

### Access

An `Access` represents a parsed Storj access grant and an open project connection. Most operations in the library start from it.

### Bucket

A bucket is the top-level container for your objects. Bucket names must follow S3-style naming rules.

### Object

An object is a binary blob stored in a bucket and identified by a unique key.

### Key

An object key is comparable to a path such as `photos/2026/april/image.jpg`. Prefixes are important for listing and sharing subsets of data.

## Namespace structure

Important namespaces in the current library:

- `uplink.NET.Models` for access, options, metadata, upload/download operations, and result models
- `uplink.NET.Services` for the concrete services
- `uplink.NET.Interfaces` for the service abstractions
- `uplink.NET` for helper APIs like `Uplink.GetStorjVersion()`

## Getting access

Most applications start from a serialized access grant:

```csharp
using uplink.NET.Models;

using var access = new Access("YOUR_ACCESS_GRANT");
```

You can optionally provide a `Config` to control the temp directory, user agent, or dial timeout:

```csharp
var access = new Access(
    "YOUR_ACCESS_GRANT",
    new Config
    {
        TempDirectory = Path.Combine(Path.GetTempPath(), "uplink-net"),
        UserAgent = "my-app/1.0",
        DialTimeoutMilliseconds = 10_000
    });
```

To persist or forward an access grant again:

```csharp
string serializedAccess = access.Serialize();
```

## BucketService, ObjectService, MultipartUploadService, and UploadQueueService

Create the services by passing an `Access` instance:

```csharp
using uplink.NET.Services;

var bucketService = new BucketService(access);
var objectService = new ObjectService(access);
var multipartUploadService = new MultipartUploadService(access);
```

`UploadQueueService` is different because it stores pending uploads in a local SQLite database and therefore needs a database path:

```csharp
var queueService = new UploadQueueService(
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "uplink-net-uploads.db3"));
```

## Creating buckets

```csharp
await bucketService.CreateBucketAsync("my-bucket");
```

If the bucket might already exist, use:

```csharp
await bucketService.EnsureBucketAsync("my-bucket");
```

## Getting and listing buckets

```csharp
var bucket = await bucketService.GetBucketAsync("my-bucket");

var buckets = await bucketService.ListBucketsAsync(
    new ListBucketsOptions());

foreach (var item in buckets.Items)
{
    Console.WriteLine(item.Name);
}
```

`ListBucketsOptions.Cursor` can be used to continue a paged listing.

## Deleting buckets

Delete an empty bucket:

```csharp
await bucketService.DeleteBucketAsync("my-bucket");
```

Delete a bucket together with its objects:

```csharp
await bucketService.DeleteBucketWithObjectsAsync("my-bucket");
```

## Uploading objects

The library supports `byte[]`, `Stream`, chunked uploads, multipart uploads, and a persistent upload queue.

### Uploading a `byte[]` in the foreground

```csharp
byte[] bytesToUpload = Encoding.UTF8.GetBytes("Storj is awesome!");

var upload = await objectService.UploadObjectAsync(
    "my-bucket",
    "awesome.txt",
    bytesToUpload,
    startImmediately: false);

await upload.StartUploadAsync();
```

### Uploading a stream in the foreground

```csharp
await using var fileStream = File.OpenRead("large-file.bin");

var upload = await objectService.UploadObjectAsync(
    "my-bucket",
    "large-file.bin",
    fileStream,
    uploadOptions: null,
    customMetadata: null,
    startImmediately: false);

await upload.StartUploadAsync();
```

Keep the stream alive until the upload finishes.

### Uploading in the background

If you set `startImmediately` to `true`, the upload starts right away and the returned `UploadOperation` can be used for status tracking:

```csharp
var upload = await objectService.UploadObjectAsync(
    "my-bucket",
    "background.txt",
    Encoding.UTF8.GetBytes("hello"),
    startImmediately: true);

upload.UploadOperationProgressChanged += op =>
    Console.WriteLine($"{op.PercentageCompleted:F1}%");

upload.UploadOperationEnded += op =>
    Console.WriteLine(op.Completed ? "done" : op.ErrorMessage);
```

Useful `UploadOperation` members include:

- `BytesSent`
- `TotalBytes`
- `Completed`
- `Failed`
- `Cancelled`
- `Running`
- `ErrorMessage`
- `PercentageCompleted`
- `Cancel()`

### Uploading in sequential chunks

`ChunkedUploadOperation` is useful when your data arrives incrementally:

```csharp
using var chunkedUpload = await objectService.UploadObjectChunkedAsync(
    "my-bucket",
    "streamed.bin",
    uploadOptions: null,
    customMetadata: null);

await chunkedUpload.UploadChunkAsync(firstChunk);
await chunkedUpload.UploadChunkAsync(secondChunk);
await chunkedUpload.CommitAsync();
```

Call `AbortAsync()` if you want to discard the upload instead.

## Multipart uploading

Multipart upload is useful when you want explicit control over individual parts and resumable workflows:

```csharp
var uploadInfo = await multipartUploadService.BeginUploadAsync(
    "my-bucket",
    "video.mp4",
    new UploadOptions());

await multipartUploadService.UploadPartAsync(
    "my-bucket",
    "video.mp4",
    uploadInfo.UploadId,
    1,
    firstPartBytes);

await multipartUploadService.UploadPartAsync(
    "my-bucket",
    "video.mp4",
    uploadInfo.UploadId,
    2,
    secondPartBytes);

await multipartUploadService.CommitUploadAsync(
    "my-bucket",
    "video.mp4",
    uploadInfo.UploadId,
    new CommitUploadOptions());
```

Other multipart helpers:

- `AbortUploadAsync(...)`
- `ListUploadsAsync(...)`
- `ListUploadPartsAsync(...)`
- `UploadPartSetETagAsync(...)`
- `GetPartUploadInfoAsync(...)`

## Upload queue

`UploadQueueService` stores queued uploads in SQLite so background processing can continue across app sessions.

```csharp
var queueService = new UploadQueueService(
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "uplink-net-uploads.db3"));

await queueService.AddObjectToUploadQueueAsync(
    "my-bucket",
    "queued.txt",
    access.Serialize(),
    Encoding.UTF8.GetBytes("queued upload"),
    "demo-upload");

queueService.ProcessQueueInBackground();
```

Useful queue APIs:

- `GetAwaitingUploadsAsync()`
- `GetOpenUploadCountAsync()`
- `CancelUploadAsync(key)`
- `RetryAsync(key)`
- `ProcessQueueInBackground()`
- `StopQueueInBackground()`
- `UploadQueueChangedEvent`

Note that the serialized access grant is stored in the local SQLite database together with the queued upload record.

## Metadata on objects

Custom metadata is passed as a key/value dictionary:

```csharp
var metadata = new CustomMetadata();
metadata.Entries["source"] = "camera-7";
metadata.Entries["category"] = "photos";

var upload = await objectService.UploadObjectAsync(
    "my-bucket",
    "photos/image.jpg",
    File.ReadAllBytes("image.jpg"),
    new UploadOptions(),
    metadata,
    startImmediately: false);

await upload.StartUploadAsync();
```

Object listings and object details can return:

- `SystemMetadata` like creation time, expiry time, and content length
- `CustomMetadata`

## Listing objects

List everything at the bucket root:

```csharp
var objects = await objectService.ListObjectsAsync("my-bucket");
```

Filter by prefix and recurse through subpaths:

```csharp
var objects = await objectService.ListObjectsAsync(
    "my-bucket",
    new ListObjectsOptions
    {
        Prefix = "photos/2026/",
        Recursive = true,
        System = true,
        Custom = true
    });
```

Important `ListObjectsOptions` members:

- `Prefix`
- `Cursor`
- `Delimiter`
- `Recursive`
- `System`
- `Custom`

## Getting objects

If you only need the object metadata and not the full content:

```csharp
var obj = await objectService.GetObjectAsync("my-bucket", "photos/image.jpg");
```

## Downloading objects

### Downloading into memory

```csharp
var download = await objectService.DownloadObjectAsync(
    "my-bucket",
    "photos/image.jpg",
    new DownloadOptions(),
    startImmediately: false);

download.DownloadOperationProgressChanged += op =>
    Console.WriteLine($"{op.PercentageCompleted:F1}%");

await download.StartDownloadAsync();
File.WriteAllBytes("image-copy.jpg", download.DownloadedBytes);
```

Useful `DownloadOperation` members include:

- `BytesReceived`
- `TotalBytes`
- `Completed`
- `Failed`
- `Cancelled`
- `Running`
- `ErrorMessage`
- `PercentageCompleted`
- `Cancel()`

### Downloading as a stream

```csharp
await using var downloadStream = await objectService.GetObjectAsStream(
    "my-bucket",
    "photos/image.jpg");

await using var file = File.Create("image-copy.jpg");
await downloadStream.CopyToAsync(file);
```

You can also pass `DownloadOptions` with `Offset` and `Length` for partial reads.

The current `DownloadStream` is forward-only and does not support seeking.

## Copying, moving, and deleting objects

Copy an object:

```csharp
await objectService.CopyObjectAsync(
    "my-bucket",
    "photos/image.jpg",
    "archive-bucket",
    "2026/image.jpg");
```

Move or rename an object:

```csharp
await objectService.MoveObjectAsync(
    "my-bucket",
    "photos/image.jpg",
    "my-bucket",
    "photos/renamed-image.jpg");
```

Delete an object:

```csharp
await objectService.DeleteObjectAsync(
    "my-bucket",
    "photos/renamed-image.jpg");
```

## Sharing access

To share a restricted child access grant:

```csharp
var permission = new Permission
{
    AllowDownload = true,
    AllowList = true
};

var sharedAccess = access.Share(
    permission,
    new SharePrefix
    {
        Bucket = "my-bucket",
        Prefix = "photos/2026/"
    });

string serializedSharedAccess = sharedAccess.Serialize();
```

Available `Permission` members:

- `AllowDownload`
- `AllowUpload`
- `AllowList`
- `AllowDelete`
- `NotBefore`
- `NotAfter`

## Revoking access

If you created a child access from a parent access, you can revoke it later:

```csharp
await parentAccess.RevokeAsync(childAccess);
```

## Runtime version

To inspect the bundled native `storj/uplink-c` version:

```csharp
string storjVersion = Uplink.GetStorjVersion();
```

## Need more help?

If the documentation needs another example or a new topic, open an issue or discussion in the repository.

