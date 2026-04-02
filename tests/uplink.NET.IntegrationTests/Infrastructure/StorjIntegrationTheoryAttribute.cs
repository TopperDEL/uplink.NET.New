namespace uplink.NET.IntegrationTests.Infrastructure;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class StorjIntegrationTheoryAttribute : TheoryAttribute
{
    public StorjIntegrationTheoryAttribute()
    {
        Skip = IntegrationTestEnvironment.SkipReason;
    }
}
