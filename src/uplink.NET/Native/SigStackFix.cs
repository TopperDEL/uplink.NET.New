using System.Runtime.InteropServices;

namespace uplink.NET.Native;

/// <summary>
/// Prototype workaround for the intermittent Linux test-host SIGSEGV
/// documented in <c>docs/crash-investigation.md</c>.
///
/// Installs a 1 MB <c>sigaltstack</c> on every thread that is about to
/// call into <c>libstorj_uplink.so</c>. Go's cgo runtime sets a ~32 KB
/// alt stack on cgo-entered threads and that alt stack persists after
/// the cgo call returns. When CoreCLR later delivers SIGSEGV on that
/// thread (via its managed null-reference probe machinery), CoreCLR's
/// handler overflows the small alt stack and produces a kernel-synthesized
/// SIGSEGV (<c>si_code=SI_KERNEL</c>) — the exact signature seen in
/// three captured cores.
///
/// Ensuring every cgo-touched thread has a 1 MB alt stack gives CoreCLR's
/// handler enough headroom to run without overflow. We pay for this
/// once per thread (<see cref="_installed"/> is <see cref="ThreadStaticAttribute"/>)
/// and the backing buffer is intentionally leaked for the life of the
/// thread because the kernel holds a reference to it for signal delivery.
///
/// This file is prototype scaffolding: if the CI evidence confirms it
/// removes the crash, we'll either promote it to a proper library
/// feature, push it upstream to <c>dotnet/runtime</c> / <c>golang/go</c>
/// as a known-bad-interaction bug, or both.
/// </summary>
internal static partial class SigStackFix
{
    private const string LibName = "uplink_sigstack_fix";

    // True once we've confirmed the shim is loadable. Once it fails
    // with DllNotFoundException, we stop trying on ALL threads.
    [ThreadStatic]
    private static bool _checkedOnThisThread;
    private static volatile bool _shimUnavailable;

    /// <summary>
    /// Ensure the current thread has a large sigaltstack. Designed to
    /// be called on every cgo entry point (before AND after the
    /// P/Invoke). Cheap: the native side reads the actual sigaltstack
    /// size from the kernel task struct and returns immediately if it's
    /// already large enough.
    ///
    /// Must be called AFTER the first cgo call on a thread too, not
    /// just before, because Go's cgo runtime replaces whatever
    /// sigaltstack was set with its own ~32 KB stack during M init.
    /// </summary>
    internal static void EnsureOnCurrentThread()
    {
        if (_shimUnavailable)
            return;

        try
        {
            uplink_sigstack_install();
            _checkedOnThisThread = true;
        }
        catch (DllNotFoundException)
        {
            _shimUnavailable = true;
        }
    }

    [LibraryImport(LibName)]
    private static partial int uplink_sigstack_install();
}
