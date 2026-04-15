namespace uplink.NET.Worker.Native;

/// <summary>Worker-local representation of a Storj object (avoids dependency on main lib models).</summary>
internal sealed class WorkerObject
{
    internal string Key { get; set; } = string.Empty;
    internal bool IsPrefix { get; set; }
    internal long Created { get; set; }
    internal long Expires { get; set; }
    internal long ContentLength { get; set; }
    internal Dictionary<string, string> CustomMetadata { get; } = new();
}

/// <summary>Worker-local representation of a part in a multipart upload.</summary>
internal sealed class WorkerPart
{
    internal uint PartNumber { get; set; }
    internal long Size { get; set; }
    internal long Modified { get; set; }
    internal string ETag { get; set; } = string.Empty;
}
