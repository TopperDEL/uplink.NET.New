namespace uplink.NET.IntegrationTests.Infrastructure;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class StorjStressFactAttribute : FactAttribute
{
    public StorjStressFactAttribute()
    {
        Skip = IntegrationTestEnvironment.StressSkipReason;
    }
}
