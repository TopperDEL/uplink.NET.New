using uplink.NET.Models;

namespace uplink.NET.IntegrationTests.Infrastructure;

internal static class IntegrationTestEnvironment
{
    // Use a prime below 256 so the generated sequence cycles through a wide range of byte values.
    private const int PrimeModulusForPayloadPattern = 251;
    private const string EnableDiagnosticsVariableName = "UPLINK_NET_ENABLE_DIAGNOSTICS";
    private const string DiagnosticsDirectoryVariableName = "UPLINK_NET_DIAGNOSTICS_DIR";
    private const string EnableStressTestsVariableName = "UPLINK_NET_ENABLE_STRESS_TESTS";
    private const string SerializeNativeOperationsVariableName = "UPLINK_NET_SERIALIZE_NATIVE_OPERATIONS";
    private const string StressIterationsVariableName = "UPLINK_NET_STRESS_ITERATIONS";

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

    public static string? StressSkipReason
    {
        get
        {
            if (!OperatingSystem.IsLinux())
                return "Stress integration tests run on Linux only.";

            return IsStressEnabled()
                ? SkipReason
                : $"Set {EnableStressTestsVariableName}=1 to run Storj stress integration tests.";
        }
    }

    public static int StressIterations
    {
        get
        {
            var configuredValue = Environment.GetEnvironmentVariable(StressIterationsVariableName);
            return int.TryParse(configuredValue, out var parsedValue) && parsedValue > 0
                ? parsedValue
                : 10;
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

        var accessConfig = CreateAccessConfig(tempDirectory);
        if (accessConfig.EnableDiagnostics && !string.IsNullOrWhiteSpace(accessConfig.DiagnosticsLogFilePath))
            Console.WriteLine($"[uplink.NET diagnostics] {accessConfig.DiagnosticsLogFilePath}");

        return new IntegrationTestContext(
            new Access(accessGrant, accessConfig),
            bucketName,
            tempDirectory);
    }

    public static Config CreateQueueAccessConfig(string tempDirectory)
        => CreateAccessConfig(tempDirectory);

    public static byte[] CreatePayload(int sizeInBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeInBytes);

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

    private static Config CreateAccessConfig(string tempDirectory)
    {
        var config = new Config
        {
            TempDirectory = tempDirectory,
            SerializeNativeOperations = IsNativeSerializationEnabled()
        };

        if (!IsDiagnosticsEnabled())
            return config;

        var diagnosticsDirectory = Environment.GetEnvironmentVariable(DiagnosticsDirectoryVariableName);
        if (string.IsNullOrWhiteSpace(diagnosticsDirectory))
            return config;

        Directory.CreateDirectory(diagnosticsDirectory);

        config.EnableDiagnostics = true;
        config.DiagnosticsLogFilePath = Path.Combine(
            diagnosticsDirectory,
            $"uplink-net-integration-{Guid.NewGuid():N}.log");
        return config;
    }

    private static bool IsDiagnosticsEnabled()
        => string.Equals(Environment.GetEnvironmentVariable(EnableDiagnosticsVariableName), "1", StringComparison.Ordinal)
           || string.Equals(Environment.GetEnvironmentVariable(EnableDiagnosticsVariableName), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsStressEnabled()
        => string.Equals(Environment.GetEnvironmentVariable(EnableStressTestsVariableName), "1", StringComparison.Ordinal)
           || string.Equals(Environment.GetEnvironmentVariable(EnableStressTestsVariableName), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsNativeSerializationEnabled()
        => string.Equals(Environment.GetEnvironmentVariable(SerializeNativeOperationsVariableName), "1", StringComparison.Ordinal)
           || string.Equals(Environment.GetEnvironmentVariable(SerializeNativeOperationsVariableName), "true", StringComparison.OrdinalIgnoreCase);
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
