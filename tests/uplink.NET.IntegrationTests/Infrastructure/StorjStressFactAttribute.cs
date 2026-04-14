namespace uplink.NET.IntegrationTests.Infrastructure;

/// <summary>
/// Marks a test as part of the opt-in stress suite. Skipped by default to
/// keep the regular integration run fast; enable by setting
/// <c>UPLINK_RUN_STRESS=1</c>. Also requires the standard integration
/// credentials (<c>TEST_ACCESS_GRANT</c> / <c>TEST_BUCKET</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class StorjStressFactAttribute : FactAttribute
{
    public const string RunStressVariableName = "UPLINK_RUN_STRESS";

    public StorjStressFactAttribute()
    {
        if (IntegrationTestEnvironment.SkipReason is { } integrationSkip)
        {
            Skip = integrationSkip;
            return;
        }

        var run = Environment.GetEnvironmentVariable(RunStressVariableName);
        if (!string.Equals(run, "1", StringComparison.Ordinal)
            && !string.Equals(run, "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip = $"Set {RunStressVariableName}=1 to run stress tests.";
        }
    }
}
