using System.Runtime.InteropServices;

namespace uplink.NET.Native;

internal abstract class UplinkSafeHandle : SafeHandle
{
    protected UplinkSafeHandle()
        : base(nint.Zero, ownsHandle: true)
    {
    }

    protected UplinkSafeHandle(nint handle)
        : this()
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == nint.Zero;

    internal nint DangerousHandle => DangerousGetHandle();
}

internal sealed class UplinkAccessSafeHandle : UplinkSafeHandle
{
    internal UplinkAccessSafeHandle(nint handle)
        : base(handle)
    {
    }

    protected override bool ReleaseHandle()
    {
        UplinkInterop.FreeAccessHandle(handle);
        return true;
    }
}

internal sealed class UplinkProjectSafeHandle : UplinkSafeHandle
{
    internal UplinkProjectSafeHandle(nint handle)
        : base(handle)
    {
    }

    protected override bool ReleaseHandle()
    {
        UplinkInterop.FreeProjectHandle(handle);
        return true;
    }
}

internal sealed class UplinkUploadSafeHandle : UplinkSafeHandle
{
    internal UplinkUploadSafeHandle(nint handle)
        : base(handle)
    {
    }

    protected override bool ReleaseHandle()
    {
        UplinkInterop.FreeUploadHandle(handle);
        return true;
    }
}

internal sealed class UplinkDownloadSafeHandle : UplinkSafeHandle
{
    internal UplinkDownloadSafeHandle(nint handle)
        : base(handle)
    {
    }

    protected override bool ReleaseHandle()
    {
        UplinkInterop.FreeDownloadHandle(handle);
        return true;
    }
}

internal sealed class UplinkPartUploadSafeHandle : UplinkSafeHandle
{
    internal UplinkPartUploadSafeHandle(nint handle)
        : base(handle)
    {
    }

    protected override bool ReleaseHandle()
    {
        UplinkInterop.FreePartUploadHandle(handle);
        return true;
    }
}
