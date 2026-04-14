/*
 * Test whether Go's cgo signal handler correctly chains SIGSEGV to a
 * previously-installed handler after a cgo call.
 *
 * Mimics what happens in the .NET + uplink-c test process:
 *   1. Install our own SIGSEGV handler (representing CoreCLR's).
 *   2. dlopen libstorj_uplink.so → Go's cgo runtime installs its
 *      SIGSEGV handler, saving ours as the "previous" handler.
 *   3. Call a simple uplink function so Go's M init runs on this
 *      thread (installs sigaltstack, etc).
 *   4. Deliberately dereference NULL to trigger SIGSEGV.
 *   5. If our handler runs and we survive → Go chained correctly.
 *      If the process dies with SI_KERNEL → Go's handler ate the
 *      signal and called dieFromSignal instead of forwarding.
 *
 * Build:
 *   cc -O0 -g -o test_signal_chain test_signal_chain.c -ldl -lpthread
 *
 * Run (libstorj_uplink.so must be findable via LD_LIBRARY_PATH):
 *   LD_LIBRARY_PATH=path/to/runtimes/linux-x64/native ./test_signal_chain
 *
 * Expected output on success:
 *   [1] installed our SIGSEGV handler
 *   [2] loaded libstorj_uplink.so
 *   [3] called uplink_internal_UniverseIsEmpty (cgo init done)
 *   [4] triggering SIGSEGV via NULL deref...
 *   [handler] caught SIGSEGV! si_code=1 (SEGV_MAPERR) si_addr=0x0
 *   [5] survived — Go correctly chained to our handler
 *   PASS
 *
 * Expected output on failure (Go doesn't chain):
 *   [1] installed our SIGSEGV handler
 *   [2] loaded libstorj_uplink.so
 *   [3] called uplink_internal_UniverseIsEmpty (cgo init done)
 *   [4] triggering SIGSEGV via NULL deref...
 *   <process killed, exit signal 11>
 */

#define _GNU_SOURCE
#include <dlfcn.h>
#include <setjmp.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ucontext.h>

static volatile int handler_called = 0;
static volatile int fault_expected = 0;

/* A label we jump past on recovery. */
static sigjmp_buf recovery_point;

static void our_sigsegv_handler(int sig, siginfo_t *info, void *ucontext) {
    if (!fault_expected) {
        /* Unexpected SIGSEGV — let it kill us. */
        fprintf(stderr, "[handler] unexpected SIGSEGV si_code=%d si_addr=%p\n",
                info->si_code, info->si_addr);
        signal(SIGSEGV, SIG_DFL);
        raise(SIGSEGV);
        return;
    }

    fprintf(stderr, "[handler] caught SIGSEGV! si_code=%d si_addr=%p\n",
            info->si_code, info->si_addr);
    handler_called = 1;

    /* Skip past the faulting instruction by longjmp. This is what CoreCLR
     * effectively does (via ucontext RIP manipulation) to convert the
     * fault into a managed NullReferenceException. We use siglongjmp for
     * simplicity. */
    siglongjmp(recovery_point, 1);
}

int main(void) {
    /* 1. Install our SIGSEGV handler with SA_SIGINFO so we get siginfo. */
    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_sigaction = our_sigsegv_handler;
    sa.sa_flags = SA_SIGINFO | SA_RESTART;
    sigemptyset(&sa.sa_mask);
    if (sigaction(SIGSEGV, &sa, NULL) != 0) {
        perror("sigaction");
        return 1;
    }
    fprintf(stderr, "[1] installed our SIGSEGV handler\n");

    /* 2. Load libstorj_uplink.so — Go's cgo init will run, installing
     *    Go's signal handlers (saving ours as the previous handler). */
    void *lib = dlopen("libstorj_uplink.so", RTLD_NOW | RTLD_GLOBAL);
    if (!lib) {
        fprintf(stderr, "dlopen: %s\n", dlerror());
        return 1;
    }
    fprintf(stderr, "[2] loaded libstorj_uplink.so\n");

    /* 3. Call a simple function so Go's M init runs on this thread. */
    typedef int (*universe_fn)(void);
    universe_fn is_empty = (universe_fn)dlsym(lib, "uplink_internal_UniverseIsEmpty");
    if (!is_empty) {
        fprintf(stderr, "dlsym: %s\n", dlerror());
        return 1;
    }
    int empty = is_empty();
    fprintf(stderr, "[3] called uplink_internal_UniverseIsEmpty=%d (cgo init done)\n", empty);

    /* 4. Trigger SIGSEGV by dereferencing NULL. If Go chains correctly,
     *    our handler (above) will run, set handler_called, and longjmp
     *    back. If Go doesn't chain, the process dies here. */
    fprintf(stderr, "[4] triggering SIGSEGV via NULL deref...\n");
    fflush(stderr);

    fault_expected = 1;
    if (sigsetjmp(recovery_point, 1) == 0) {
        /* First return from sigsetjmp — do the faulting access. */
        volatile int *p = NULL;
        int v = *p;  /* SIGSEGV */
        (void)v;
        /* Should not reach here. */
        fprintf(stderr, "FAIL — NULL deref did not fault?!\n");
        return 1;
    }

    /* Returned from siglongjmp in the handler. */
    fault_expected = 0;

    if (handler_called) {
        fprintf(stderr, "[5] survived — Go correctly chained to our handler\n");
        fprintf(stderr, "PASS\n");
        dlclose(lib);
        return 0;
    } else {
        fprintf(stderr, "FAIL — jumped back but handler_called not set\n");
        dlclose(lib);
        return 1;
    }
}
