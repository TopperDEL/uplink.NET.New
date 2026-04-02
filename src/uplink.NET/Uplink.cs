using System.Reflection;
using System.Runtime.InteropServices;

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

    public static string GetRuntimeInfo()
        => $".NET={RuntimeInformation.FrameworkDescription}; OS={RuntimeInformation.OSDescription}; ProcessArchitecture={RuntimeInformation.ProcessArchitecture}; OSArchitecture={RuntimeInformation.OSArchitecture}; PID={Environment.ProcessId}; storj_uplink={GetStorjVersion()}";
}
