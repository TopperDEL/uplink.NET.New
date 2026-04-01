using uplink.NET.Models;

namespace uplink.NET.IntegrationTests.Infrastructure;

internal static class IntegrationTestEnvironment
{
    // Use a prime below 256 so the generated sequence cycles through a wide range of byte values.
    private const int PrimeModulusForPayloadPattern = 251;

    public const string AccessGrantVariableName = "TEST_ACCESS_GRANT";
    public const string BucketVariableName = "TEST_BUCKET";
    public const int StorjInlinePlacementLimitBytes = 4 * 1024;

    public static string? SkipReason
    {
        get
        {
            var missingVariables = GetMissingVariables();
            return missingVariables.Length == 0
                ? null
                : $"Set {string.Join(" and ", missingVariables)} to run Storj integration tests.";
        }
    }

    public static IntegrationTestContext CreateContext()
    {
        var accessGrant = Environment.GetEnvironmentVariable(AccessGrantVariableName);
        var bucketName = Environment.GetEnvironmentVariable(BucketVariableName);

        if (string.IsNullOrWhiteSpace(accessGrant) || string.IsNullOrWhiteSpace(bucketName))
            throw new InvalidOperationException(SkipReason ?? "Missing integration test configuration.");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "uplink.NET.IntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        return new IntegrationTestContext(
            new Access(accessGrant, new Config { TempDirectory = tempDirectory }),
            bucketName,
            tempDirectory);
    }

    public static byte[] CreatePayload(int sizeInBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeInBytes);

        var payload = new byte[sizeInBytes];
        for (var index = 0; index < payload.Length; index++)
            payload[index] = (byte)(index % PrimeModulusForPayloadPattern);

        return payload;
    }

    private static string[] GetMissingVariables()
    {
        var missingVariables = new List<string>();

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AccessGrantVariableName)))
            missingVariables.Add(AccessGrantVariableName);

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BucketVariableName)))
            missingVariables.Add(BucketVariableName);

        return missingVariables.ToArray();
    }
}

internal sealed class IntegrationTestContext : IDisposable
{
    public IntegrationTestContext(Access access, string bucketName, string tempDirectory)
    {
        Access = access;
        BucketName = bucketName;
        TempDirectory = tempDirectory;
    }

    public Access Access { get; }

    public string BucketName { get; }

    public string TempDirectory { get; }

    public void Dispose()
    {
        Access.Dispose();

        try
        {
            if (Directory.Exists(TempDirectory))
                Directory.Delete(TempDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
