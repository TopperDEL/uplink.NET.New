/*
 * Minimal reproducer for Go sigaltstack race.
 *
 * Root cause (from strace of real crashes):
 *   Thread A: dropm → sigaltstack(SS_DISABLE) → frees/recycles memory
 *   Thread B: needm → sigaltstack(same address, 16384)
 *   Thread C: tgkill(B, SIGRT_2)
 *   Thread B: handler runs on stale/freed memory → SEGV_ACCERR → SI_KERNEL
 *
 * This reproducer drives that race with:
 *   - Many pthreads calling a trivial Go function in a tight loop.
 *     Each call does needm on entry (installs sigaltstack) and dropm
 *     on return (disables sigaltstack). Rapid cycling = rapid
 *     sigaltstack enable/disable/reuse.
 *   - One thread sending SIGRT_2 to all other threads continuously.
 *     This is what CoreCLR does for GC thread suspension.
 *
 * Build:
 *   CGO_ENABLED=1 go build -buildmode=c-shared -o libgolib.so golib.go
 *   cc -O2 -o repro host.c -ldl -lpthread
 *
 * Run:
 *   LD_LIBRARY_PATH=. ./repro
 */

#define _GNU_SOURCE
#include <dirent.h>
#include <dlfcn.h>
#include <pthread.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <unistd.h>

#define NUM_WORKERS    32
#define ITERATIONS     1000000
#define GC_INTERVAL_US 50

typedef int (*ping_fn)(void);

static volatile int running = 1;

static int get_tids(pid_t *tids, int max) {
    DIR *d = opendir("/proc/self/task");
    if (!d) return 0;
    struct dirent *e;
    int n = 0;
    while ((e = readdir(d)) != NULL && n < max) {
        if (e->d_name[0] == '.') continue;
        tids[n++] = (pid_t)atoi(e->d_name);
    }
    closedir(d);
    return n;
}

/* Sends SIGRT_2 to all threads in the process. */
static void *signal_sender(void *arg) {
    (void)arg;
    pid_t my_tid = (pid_t)syscall(SYS_gettid);
    pid_t tids[512];
    int sig = SIGRTMIN + 2;

    while (running) {
        int n = get_tids(tids, 512);
        for (int i = 0; i < n; i++) {
            if (tids[i] == my_tid) continue;
            syscall(SYS_tgkill, getpid(), tids[i], sig);
        }
        usleep(GC_INTERVAL_US);
    }
    return NULL;
}

/* Calls ping() in a tight loop → needm/dropm churn per call. */
static void *worker(void *arg) {
    ping_fn fn = (ping_fn)arg;
    for (int i = 0; i < ITERATIONS && running; i++) {
        fn();
    }
    return NULL;
}

/* No-op handler so SIGRT_2 doesn't kill the process via SIG_DFL. */
static void rt2_handler(int sig, siginfo_t *info, void *ctx) {
    (void)sig; (void)info; (void)ctx;
}

int main(void) {
    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_sigaction = rt2_handler;
    sa.sa_flags = SA_SIGINFO | SA_RESTART;
    sigemptyset(&sa.sa_mask);
    sigaction(SIGRTMIN + 2, &sa, NULL);

    void *lib = dlopen("./libgolib.so", RTLD_NOW | RTLD_GLOBAL);
    if (!lib) { fprintf(stderr, "dlopen: %s\n", dlerror()); return 1; }
    ping_fn fn = (ping_fn)dlsym(lib, "ping");
    if (!fn) { fprintf(stderr, "dlsym: %s\n", dlerror()); return 1; }

    fprintf(stderr, "%d workers × %d calls, SIGRT_2 every %d µs\n",
            NUM_WORKERS, ITERATIONS, GC_INTERVAL_US);

    pthread_t sender;
    pthread_create(&sender, NULL, signal_sender, NULL);

    pthread_t workers[NUM_WORKERS];
    for (int i = 0; i < NUM_WORKERS; i++)
        pthread_create(&workers[i], NULL, worker, fn);

    for (int i = 0; i < NUM_WORKERS; i++)
        pthread_join(workers[i], NULL);

    running = 0;
    pthread_join(sender, NULL);

    fprintf(stderr, "PASS\n");
    dlclose(lib);
    return 0;
}
