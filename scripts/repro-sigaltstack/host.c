/*
 * Standalone reproducer for the Go sigaltstack overflow crash.
 *
 * Mimics what happens in the .NET + uplink-c test suite:
 *   - Multiple pthreads make concurrent cgo calls into a Go c-shared
 *     library, causing Go to create many M threads.
 *   - A "GC thread" (mimicking CoreCLR's GC) periodically sends
 *     SIGRT_2 to all threads in the process, which is how CoreCLR
 *     suspends threads for garbage collection.
 *   - Go's handler for SIGRT_2 runs on Go's 16 KB sigaltstack and
 *     overflows it under heavy scheduling load, producing:
 *       SIGSEGV (SEGV_ACCERR) at the guard page → nested SIGSEGV
 *       (SI_KERNEL) → process killed.
 *
 * Build:
 *   CGO_ENABLED=1 go build -buildmode=c-shared -o libgolib.so golib.go
 *   cc -O2 -g -o repro host.c -ldl -lpthread
 *
 * Run:
 *   LD_LIBRARY_PATH=. ./repro
 *
 * Expected on a buggy Go runtime: process killed by SIGSEGV.
 * Expected if Go fixes its sigaltstack size: prints "PASS" after
 * the configured number of iterations.
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

/* How many C threads call into Go concurrently. */
#define NUM_WORKERS     16
/* How many goroutines each cgo call spawns. */
#define GOROUTINES      100
/* Duration of each cgo call in milliseconds. */
#define WORK_DURATION   50
/* How many iterations each worker runs. */
#define ITERATIONS      500
/* How often the "GC thread" sends SIGRT_2 (microseconds). */
#define GC_INTERVAL_US  100

typedef void (*work_fn)(int, int);

static volatile int running = 1;

/*
 * Enumerate all thread IDs in this process via /proc/self/task.
 * Returns count; fills tids[] up to max.
 */
static int get_thread_ids(pid_t *tids, int max) {
    DIR *dir = opendir("/proc/self/task");
    if (!dir) return 0;
    struct dirent *ent;
    int count = 0;
    while ((ent = readdir(dir)) != NULL && count < max) {
        if (ent->d_name[0] == '.') continue;
        tids[count++] = (pid_t)atoi(ent->d_name);
    }
    closedir(dir);
    return count;
}

/*
 * "GC thread": periodically sends SIGRT_2 to all threads in the
 * process, mimicking what CoreCLR does for GC thread suspension.
 */
static void *gc_thread(void *arg) {
    (void)arg;
    pid_t my_tid = (pid_t)syscall(SYS_gettid);
    pid_t tids[256];
    int sig = SIGRTMIN + 2; /* SIGRT_2 */

    while (running) {
        int n = get_thread_ids(tids, 256);
        for (int i = 0; i < n; i++) {
            if (tids[i] == my_tid) continue; /* don't signal ourselves */
            /* tgkill: send to specific thread in this thread group. */
            syscall(SYS_tgkill, getpid(), tids[i], sig);
        }
        usleep(GC_INTERVAL_US);
    }
    return NULL;
}

/*
 * Worker thread: makes repeated cgo calls into the Go library.
 */
static void *worker(void *arg) {
    work_fn fn = (work_fn)arg;
    for (int i = 0; i < ITERATIONS && running; i++) {
        fn(GOROUTINES, WORK_DURATION);
    }
    return NULL;
}

/*
 * SIGRT_2 handler — installed BEFORE dlopen so our handler is the
 * "previous" handler Go saves. Go's handler should chain to ours.
 * We just return — the point is to have a valid handler installed
 * so the signal isn't SIG_DFL (which would kill the process on
 * delivery even without the overflow bug).
 */
static void rt2_handler(int sig, siginfo_t *info, void *ctx) {
    (void)sig; (void)info; (void)ctx;
    /* no-op — just survive the signal */
}

int main(void) {
    /* Install a SIGRT_2 handler before loading Go. */
    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_sigaction = rt2_handler;
    sa.sa_flags = SA_SIGINFO | SA_RESTART;
    sigemptyset(&sa.sa_mask);
    if (sigaction(SIGRTMIN + 2, &sa, NULL) != 0) {
        perror("sigaction SIGRT_2");
        return 1;
    }

    /* Load the Go c-shared library. */
    void *lib = dlopen("./libgolib.so", RTLD_NOW | RTLD_GLOBAL);
    if (!lib) {
        fprintf(stderr, "dlopen: %s\n", dlerror());
        return 1;
    }

    work_fn fn = (work_fn)dlsym(lib, "work_iteration");
    if (!fn) {
        fprintf(stderr, "dlsym: %s\n", dlerror());
        return 1;
    }

    fprintf(stderr, "starting %d workers × %d iterations, "
                    "GC thread sending SIGRT_2 every %d µs\n",
            NUM_WORKERS, ITERATIONS, GC_INTERVAL_US);

    /* Start the GC-simulation thread. */
    pthread_t gc;
    pthread_create(&gc, NULL, gc_thread, NULL);

    /* Start worker threads. */
    pthread_t workers[NUM_WORKERS];
    for (int i = 0; i < NUM_WORKERS; i++)
        pthread_create(&workers[i], NULL, worker, fn);

    /* Wait for workers. */
    for (int i = 0; i < NUM_WORKERS; i++)
        pthread_join(workers[i], NULL);

    /* Stop GC thread. */
    running = 0;
    pthread_join(gc, NULL);

    fprintf(stderr, "PASS — completed %d × %d iterations without crash\n",
            NUM_WORKERS, ITERATIONS);

    dlclose(lib);
    return 0;
}
