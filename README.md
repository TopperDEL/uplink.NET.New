# uplink.NET

A modern .NET 10+ wrapper library around the [storj/uplink-c](https://github.com/storj/uplink-c) C library, providing idiomatic async .NET access to the [Storj](https://storj.io) decentralized cloud storage network.

## Features

- **Modern P/Invoke** – uses `LibraryImport` (source-generated, AOT-compatible) instead of SWIG
- **Async-first API** – all I/O operations are `Task`-returning
- **Multipart upload** – full support for begin/upload-part/commit/abort/list
- **Upload queue** – persistent SQLite-backed upload queue with background processing
- **Cross-platform** – Windows, Linux (x64 + ARM64), macOS (x64 + ARM64), Android ARM64
- **NuGet ready** – builds a self-contained NuGet package containing all native runtimes

## Wiki

Additional GitHub-wiki-compatible documentation is available in [`/wiki`](wiki/Home.md):

- [Home](wiki/Home.md)
- [Documentation](wiki/Documentation.md)

## Quick start

```csharp
using uplink.NET.Models;
using uplink.NET.Services;

// Parse the access grant and open a project connection
using var access = new Access("your-access-grant-here");

// TempDirectory is optional. By default the library uses the platform temp directory.
// var access = new Access("your-access-grant-here", new Config { TempDirectory = "/custom/temp" });

var buckets = new BucketService(access);
var objects = new ObjectService(access);
var storjVersion = Uplink.GetStorjVersion();

// Create or ensure a bucket exists
var bucket = await buckets.EnsureBucketAsync("my-bucket");

// Upload a file
byte[] data = File.ReadAllBytes("photo.jpg");
var upload = await objects.UploadObjectAsync("my-bucket", "photos/photo.jpg", data);
// upload.StartUploadAsync() is called automatically (startImmediately defaults to true)

// List objects
var list = await objects.ListObjectsAsync("my-bucket");
foreach (var obj in list.Items)
    Console.WriteLine($"{obj.Key} ({obj.ContentLength} bytes)");

// Download
var download = await objects.DownloadObjectAsync("my-bucket", "photos/photo.jpg", startImmediately: true);
download.DownloadOperationEnded += op =>
{
    if (op.Completed)
        File.WriteAllBytes("downloaded.jpg", op.DownloadedBytes);
};

// Stream download
using var stream = await objects.GetObjectAsStream("my-bucket", "photos/photo.jpg");
using var file = File.Create("downloaded-stream.jpg");
await stream.CopyToAsync(file);

// Copy / move objects
await objects.CopyObjectAsync("my-bucket", "photos/photo.jpg", "my-bucket", "photos/photo-copy.jpg");
await objects.MoveObjectAsync("my-bucket", "photos/photo-copy.jpg", "my-bucket", "photos/photo-moved.jpg");
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

tests/
└── uplink.NET.IntegrationTests/  # Real Storj integration tests using TEST_ACCESS_GRANT/TEST_BUCKET
```

## Building

The .NET wrapper builds without the native library present. The native `storj_uplink` shared
library is built by the CI pipeline (see `.github/workflows/build.yml`) and placed under
`src/uplink.NET/runtimes/<rid>/native/` before packing the NuGet package.

To build locally:

```bash
dotnet build uplink.NET.sln
```

## Testing

For a quick local validation run:

```bash
dotnet build uplink.NET.sln -c Release
dotnet test uplink.NET.sln -c Release --no-build
```

### Local integration testing

The Storj-backed integration tests expect two environment variables:

- `TEST_ACCESS_GRANT`
- `TEST_BUCKET`

Run the local bootstrap script to build the native library for the current platform, stage it under `src/uplink.NET/runtimes/<rid>/native/`, and execute the integration suite:

```bash
TEST_ACCESS_GRANT=... TEST_BUCKET=... ./scripts/run-integration-tests.sh
```

If you already have a local checkout of `storj/uplink-c`, set `UPLINK_C_DIR` to reuse it instead of cloning a temporary copy.

### CI on every commit and pull request

`.github/workflows/ci.yml` runs automatically for every branch commit and every pull request. It builds the Linux native runtime, restores and builds the solution, and runs the test suite. If `TEST_ACCESS_GRANT` or `TEST_BUCKET` are not configured in GitHub Actions, the Storj integration tests are skipped cleanly.

## NuGet publishing

The package metadata needed for NuGet.org is defined in
`src/uplink.NET/uplink.NET.csproj`.
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

The release workflow in `.github/workflows/build.yml` runs for pushed version tags in the format `v*` and for published GitHub Releases. It builds the native runtimes, runs the Linux-backed integration tests before packing, and then:

- pushes the `.nupkg` to NuGet.org when a version tag is pushed
- attaches the generated `.nupkg` to the matching GitHub Release when a GitHub Release for that tag is published

Recommended release flow:

1. Run `./scripts/run-integration-tests.sh` locally with `TEST_ACCESS_GRANT` and `TEST_BUCKET` set
2. Create or choose a Git tag in the format `v1.0.0`
3. Push the tag to GitHub (only push tags that should publish to NuGet.org)
4. GitHub Actions builds the native runtimes, runs the Linux integration test gate, packs the NuGet package, and publishes it to NuGet.org using `NUGET_API_KEY`
5. Publish a GitHub Release for the same tag when you want the generated `.nupkg` attached to the release assets

> [!NOTE]
> Local `dotnet pack` is useful for validation, but the tag/release workflow is the path that bundles the native runtime artifacts before publishing and attaching release assets.

## License

MIT
