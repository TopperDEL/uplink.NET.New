using System.Reflection;

namespace uplink.NET;

public static class Uplink
{
    private const string StorjUplinkVersionMetadataKey = "StorjUplinkVersion";

    public static string GetStorjVersion()
        => typeof(Uplink).Assembly
               .GetCustomAttributes<AssemblyMetadataAttribute>()
               .FirstOrDefault(attribute => attribute.Key == StorjUplinkVersionMetadataKey)
               ?.Value
           ?? string.Empty;
}
