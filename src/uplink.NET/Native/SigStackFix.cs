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

    [ThreadStatic]
    private static bool _installed;

    /// <summary>
    /// Ensure the current thread has a large sigaltstack. Idempotent
    /// per thread; cheap to call on every cgo entry point. No-ops on
    /// platforms where the shim isn't loaded (e.g. Windows).
    /// </summary>
    internal static void EnsureOnCurrentThread()
    {
        if (_installed)
            return;

        // Flip the flag before invoking the native helper so any
        // re-entrant call from the same thread (e.g. via a debugger
        // stepping in) doesn't recurse. The native side is itself
        // idempotent via its own thread-local guard, so a duplicate
        // call would be harmless anyway.
        _installed = true;

        try
        {
            uplink_sigstack_install();
        }
        catch (DllNotFoundException)
        {
            // Shim not deployed (e.g. running against a non-Linux
            // build, or on a CI job that didn't produce the .so).
            // The workaround is a no-op; the production crash that
            // this diagnoses will still occur but the library still
            // works.
        }
    }

    [LibraryImport(LibName)]
    private static partial int uplink_sigstack_install();
}
