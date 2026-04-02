# uplink.NET wiki

This wiki is the starting point for the modern `uplink.NET` library.

`uplink.NET` is a .NET 10+ wrapper around [`storj/uplink-c`](https://github.com/storj/uplink-c) for working directly with the Storj decentralized cloud storage network from C#.

## What you can do with it

- Open a project from a serialized Storj access grant
- Create, inspect, list, and delete buckets
- Upload and download objects
- Stream object contents without buffering everything first
- Run multipart uploads for large files
- Queue uploads in a local SQLite-backed background worker
- Share restricted child access grants with scoped permissions

## Package setup

Install the main NuGet package:

- [`uplink.NET`](https://www.nuget.org/packages/uplink.NET/)

The current package bundles the supported native runtimes directly, so the old platform-specific companion packages are no longer needed.

Supported runtimes currently include:

- Windows
- Linux x64 and ARM64
- macOS x64 and ARM64
- Android ARM64

## Quick start

```csharp
using uplink.NET;
using uplink.NET.Models;
using uplink.NET.Services;

using var access = new Access("your-access-grant-here");

var bucketService = new BucketService(access);
var objectService = new ObjectService(access);

await bucketService.EnsureBucketAsync("my-bucket");

byte[] bytesToUpload = File.ReadAllBytes("photo.jpg");
var upload = await objectService.UploadObjectAsync(
    "my-bucket",
    "photos/photo.jpg",
    bytesToUpload,
    startImmediately: false);

await upload.StartUploadAsync();

var objects = await objectService.ListObjectsAsync(
    "my-bucket",
    new ListObjectsOptions { Recursive = true });

foreach (var obj in objects.Items)
{
    Console.WriteLine($"{obj.Key} ({obj.ContentLength} bytes)");
}

var download = await objectService.DownloadObjectAsync(
    "my-bucket",
    "photos/photo.jpg",
    startImmediately: false);

await download.StartDownloadAsync();
File.WriteAllBytes("downloaded-photo.jpg", download.DownloadedBytes);

Console.WriteLine($"Bundled storj/uplink-c version: {Uplink.GetStorjVersion()}");
```

## Next steps

- [Documentation](Documentation.md)

