/*
 * uplink.NET sigaltstack fix — prototype.
 *
 * Hypothesis we are testing (see docs/crash-investigation.md):
 *   When a .NET thread has done a P/Invoke into libstorj_uplink.so,
 *   Go's cgo runtime installs a ~32 KB sigaltstack on that thread.
 *   After the P/Invoke returns, the alt stack stays set. Later, when
 *   CoreCLR's JIT emits a managed null-reference probe (deliberate
 *   deref of address 0, handled via SIGSEGV), the kernel delivers
 *   SIGSEGV on Go's small alt stack. CoreCLR's SIGSEGV handler is
 *   complex and overflows that alt stack, hitting the guard page and
 *   producing si_code=SI_KERNEL — the exact signature seen in three
 *   captured cores.
 *
 * This shim exposes a single entry point that installs a large (1 MB)
 * sigaltstack on the calling thread. The managed side calls it once
 * per thread (gated by ThreadStatic) from Access.AcquireProjectLease
 * and related entry points, before any P/Invoke into libstorj_uplink.
 * Once set, the 1 MB alt stack persists for the life of the thread,
 * leaving enough headroom for CoreCLR's handler to run without
 * overflow.
 *
 * The buffer is leaked intentionally: it must outlive any signal that
 * uses it, which on .NET's thread pool means "for the life of the
 * thread". 1 MB × ~200 pool threads max is well under 256 MB and
 * strictly bounded.
 *
 * Build (Linux):
 *   cc -O2 -fPIC -shared -o libuplink_sigstack_fix.so sigstack_fix.c
 */

#include <signal.h>
#include <stdlib.h>
#include <string.h>

/*
 * Must comfortably exceed what CoreCLR's SIGSEGV handler walks in the
 * worst case: managed stack unwind, type handle lookups, exception
 * object construction, a call out to createdump if DOTNET_DbgEnableMiniDump
 * is set. Empirically ~128 KB is enough; we use 1 MB for a generous
 * safety margin.
 */
#define UPLINK_SIGALTSTACK_SIZE (1024 * 1024)

/*
 * Install a large sigaltstack on the calling thread. Returns 0 on
 * success, a negative errno on failure. Safe to call more than once
 * on the same thread; only the first call has any effect.
 *
 * The function is idempotent-per-thread via an explicit thread-local
 * flag so the managed side can call it unconditionally at the top of
 * hot paths without double-allocating.
 */
/*
 * Thread-local pointer to the large alt stack we allocated. NULL if
 * this thread has never had one installed. Re-used on subsequent
 * calls if Go's cgo runtime overwrote it with its smaller stack.
 */
static __thread void *our_stack = NULL;

__attribute__((visibility("default")))
int uplink_sigstack_install(void) {
    /*
     * Check the ACTUAL current sigaltstack rather than relying on a
     * boolean "installed" flag. Previous iteration used a fire-once
     * flag and the fix was silently overridden: we installed before
     * the cgo call, Go's minitSignalStack() replaced it with a ~32 KB
     * stack during the first cgo entry on that thread, and the flag
     * prevented us from re-installing afterwards.
     *
     * sigaltstack(NULL, &current) is cheap — reads from the kernel
     * task struct, no context switch on Linux ≥ 5.x.
     */
    stack_t current;
    if (sigaltstack(NULL, &current) == 0
            && !(current.ss_flags & SS_DISABLE)
            && current.ss_size >= UPLINK_SIGALTSTACK_SIZE) {
        return 0; /* already large enough, whether ours or someone else's */
    }

    /* Allocate once per thread; re-use on subsequent calls. */
    if (our_stack == NULL) {
        our_stack = malloc(UPLINK_SIGALTSTACK_SIZE);
        if (our_stack == NULL) {
            return -1;
        }
        /* Touch every page so the kernel commits them; avoids a soft
         * fault *during* signal delivery. */
        memset(our_stack, 0, UPLINK_SIGALTSTACK_SIZE);
    }

    stack_t ss;
    ss.ss_sp    = our_stack;
    ss.ss_size  = UPLINK_SIGALTSTACK_SIZE;
    ss.ss_flags = 0;

    if (sigaltstack(&ss, NULL) != 0) {
        return -2;
    }

    /* Intentionally leak `our_stack` — the kernel holds a reference
     * for signal delivery that persists for the life of this thread. */
    return 0;
}
