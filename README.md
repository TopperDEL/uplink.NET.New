# uplink.NET

A modern .NET 10+ wrapper library around the [storj/uplink-c](https://github.com/storj/uplink-c) C library, providing idiomatic async .NET access to the [Storj](https://storj.io) decentralized cloud storage network.

## Features

- **Modern P/Invoke** – uses `LibraryImport` (source-generated, AOT-compatible) instead of SWIG
- **Async-first API** – all I/O operations are `Task`-returning
- **Multipart upload** – full support for begin/upload-part/commit/abort/list
- **Upload queue** – persistent SQLite-backed upload queue with background processing
- **Cross-platform** – Windows, Linux (x64 + ARM64), macOS (x64 + ARM64), Android ARM64
- **NuGet ready** – builds a self-contained NuGet package containing all native runtimes

## Quick start

```csharp
using uplink.NET.Models;
using uplink.NET.Services;

// Parse the access grant and open a project connection
using var access = new Access("your-access-grant-here");

var buckets = new BucketService(access);
var objects = new ObjectService(access);

// Create or ensure a bucket exists
var bucket = await buckets.EnsureBucketAsync("my-bucket");

// Upload a file
byte[] data = File.ReadAllBytes("photo.jpg");
var upload = await objects.UploadObjectAsync(access, "my-bucket", "photos/photo.jpg", data);
// upload.StartUploadAsync() is called automatically (startImmediately defaults to true)

// List objects
var list = await objects.ListObjectsAsync(access, "my-bucket");
foreach (var obj in list.Items)
    Console.WriteLine($"{obj.Key} ({obj.ContentLength} bytes)");

// Download
var download = await objects.DownloadObjectAsync(access, "my-bucket", "photos/photo.jpg", startImmediately: true);
download.DownloadOperationEnded += op =>
{
    if (op.Completed)
        File.WriteAllBytes("downloaded.jpg", op.DownloadedBytes);
};
```

## Project structure

```
src/
└── uplink.NET/
    ├── Native/            # LibraryImport P/Invoke declarations (UplinkInterop.cs)
    ├── Models/            # Access, Bucket, StorjObject, upload/download operations, …
    ├── Interfaces/        # IBucketService, IObjectService, IMultipartUploadService, IUploadQueueService
    ├── Services/          # Concrete implementations
    └── Exceptions/        # Typed exceptions for every failure mode
```

## Building

The .NET wrapper builds without the native library present. The native `storj_uplink` shared
library is built by the CI pipeline (see `.github/workflows/build.yml`) and placed under
`src/uplink.NET/runtimes/<rid>/native/` before packing the NuGet package.

To build locally:

```bash
dotnet build src/uplink.NET/uplink.NET.csproj
```

## NuGet publishing

The package metadata needed for NuGet.org is defined in
`/home/runner/work/uplink.NET.New/uplink.NET.New/src/uplink.NET/uplink.NET.csproj`.
That includes the package version, author, description, tags, license, README, and
repository links.

### Requirements

- A NuGet.org account
- A NuGet.org API key
- The `NUGET_API_KEY` repository secret for GitHub Actions publishing

An image is not required for publishing. A package icon can be added later, and README
images are optional if they are hosted publicly.

### Create a local package

```bash
dotnet build src/uplink.NET/uplink.NET.csproj -c Release
dotnet pack src/uplink.NET/uplink.NET.csproj -c Release -o nupkgs/
```

### Publish manually

```bash
dotnet nuget push nupkgs/*.nupkg \
  --api-key <YOUR_NUGET_API_KEY> \
  --source https://api.nuget.org/v3/index.json
```

### Publish with GitHub Actions

The workflow in `/home/runner/work/uplink.NET.New/uplink.NET.New/.github/workflows/build.yml`
will publish automatically when you push a tag matching `v*`.

Recommended release flow:

1. Update the package version in `src/uplink.NET/uplink.NET.csproj`
2. Ensure the Git tag matches the package version, for example `v1.0.0`
3. Push the tag
4. GitHub Actions builds the native runtimes, packs the NuGet package, and publishes it using `NUGET_API_KEY`

> [!NOTE]
> Local `dotnet pack` is useful for validation, but the GitHub Actions release path is the one
> that bundles the native runtime artifacts before publishing.

## License

MIT
