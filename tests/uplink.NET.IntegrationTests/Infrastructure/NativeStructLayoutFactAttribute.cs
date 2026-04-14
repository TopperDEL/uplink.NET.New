namespace uplink.NET.IntegrationTests.Infrastructure;

/// <summary>
/// Runs only when the CSV with the authoritative native struct layout is
/// available. The CSV is produced by scripts/ci/struct_layout_check.c in CI,
/// so locally (without that generator step) the test is skipped rather than
/// spuriously failing.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class NativeStructLayoutFactAttribute : FactAttribute
{
    public const string LayoutPathVariable = "UPLINK_NATIVE_STRUCT_LAYOUT";

    public NativeStructLayoutFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable(LayoutPathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            Skip = $"{LayoutPathVariable} not set — native struct layout CSV unavailable. " +
                   "CI generates this via scripts/ci/struct_layout_check.c.";
        }
        else if (!File.Exists(path))
        {
            Skip = $"{LayoutPathVariable}={path} does not exist on disk.";
        }
    }
}
