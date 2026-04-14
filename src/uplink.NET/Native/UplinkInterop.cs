namespace uplink.NET.Native;

/// <summary>
/// Date/time utility helpers retained by the main library after moving P/Invoke to the worker process.
/// </summary>
internal static class UplinkInterop
{
    /// <summary>Converts a nullable DateTime to a Unix timestamp (0 = no expiry).</summary>
    internal static long DateTimeToUnix(DateTime? dt) =>
        dt.HasValue ? new DateTimeOffset(dt.Value.ToUniversalTime()).ToUnixTimeSeconds() : 0;

    /// <summary>Converts a Unix epoch (int64) to DateTime UTC.</summary>
    internal static DateTime UnixToDateTime(long unixSeconds) =>
        unixSeconds == 0
            ? DateTime.MinValue
            : DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
}
