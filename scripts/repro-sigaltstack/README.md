# Sigaltstack overflow reproducer

Standalone C + Go reproducer for the crash documented in
[docs/crash-investigation.md](../../docs/crash-investigation.md).

## What it does

1. A C host program installs a SIGRT_2 handler, then loads a Go
   c-shared library via `dlopen`.
2. Multiple worker pthreads make concurrent cgo calls into Go,
   causing Go to create many M threads with 16 KB sigaltstacks.
3. A "GC thread" sends `SIGRT_2` to all threads in the process every
   500 µs, mimicking CoreCLR's GC thread-suspension mechanism.
4. If Go's SIGRT_2 handler overflows the 16 KB sigaltstack, the
   process is killed by SIGSEGV (SI_KERNEL).

## Build & run

```bash
# Requires Go 1.25+ and a C compiler.
CGO_ENABLED=1 go build -buildmode=c-shared -o libgolib.so golib.go
cc -O2 -g -o repro host.c -ldl -lpthread
LD_LIBRARY_PATH=. ./repro
```

Or use the helper script:
```bash
./run.sh          # build + run
./run.sh strace   # build + run under strace (captures signal log)
```

## Expected results

- **Crash** (Go sigaltstack too small): process killed by SIGSEGV.
  Under strace, the log shows:
  ```
  --- SIGRT_2 ---
  --- SIGSEGV {si_code=SEGV_ACCERR, si_addr=<guard page>} ---
  --- SIGSEGV {si_code=SI_KERNEL} ---
  +++ killed by SIGSEGV +++
  ```

- **PASS** (Go sigaltstack large enough): prints
  `PASS — completed N × M iterations without crash`.

## Tuning

Edit the `#define` constants at the top of `host.c`:

| Constant | Default | Description |
|---|---|---|
| `NUM_WORKERS` | 8 | C threads making concurrent cgo calls |
| `GOROUTINES` | 50 | Goroutines per cgo call |
| `WORK_DURATION` | 20 | Milliseconds per cgo call |
| `ITERATIONS` | 200 | Calls per worker |
| `GC_INTERVAL_US` | 500 | SIGRT_2 send interval (µs) |

Higher concurrency + lower interval = more likely to reproduce.
