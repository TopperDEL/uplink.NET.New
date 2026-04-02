using System.Reflection;

namespace uplink.NET.IntegrationTests;

public class LibraryMetadataTests
{
    [Fact]
    public void GetStorjVersion_returns_the_build_metadata_value()
    {
        var metadataValue = typeof(uplink.NET.Uplink).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "StorjUplinkVersion")
            .Value;

        Assert.False(string.IsNullOrWhiteSpace(metadataValue));
        Assert.Equal(metadataValue, uplink.NET.Uplink.GetStorjVersion());
    }
}
