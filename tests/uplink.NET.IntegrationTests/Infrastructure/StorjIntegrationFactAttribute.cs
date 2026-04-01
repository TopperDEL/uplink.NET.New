namespace uplink.NET.IntegrationTests.Infrastructure;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class StorjIntegrationFactAttribute : FactAttribute
{
    public StorjIntegrationFactAttribute()
    {
        Skip = IntegrationTestEnvironment.SkipReason;
    }
}
