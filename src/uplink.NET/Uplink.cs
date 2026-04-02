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
    {
        var framework = RuntimeInformation.FrameworkDescription;
        var os = RuntimeInformation.OSDescription;
        var processArchitecture = RuntimeInformation.ProcessArchitecture;
        var osArchitecture = RuntimeInformation.OSArchitecture;
        var pid = Environment.ProcessId;
        var storjVersion = GetStorjVersion();

        return $".NET={framework}; OS={os}; ProcessArchitecture={processArchitecture}; OSArchitecture={osArchitecture}; PID={pid}; storj_uplink={storjVersion}";
    }
}
