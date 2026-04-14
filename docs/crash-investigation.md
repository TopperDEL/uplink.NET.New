# Linux "Test host process crashed" — investigation notes

## Symptom

`dotnet test` on Linux intermittently aborts with:

```
The active test run was aborted. Reason: Test host process crashed
```

Reproduction is non-deterministic. The test Blame reports as "running when
the crash occurred" varies between runs, and the list of concurrent
candidate tests differs each time.

## Artifacts examined

Three kernel core dumps from CI (`ci-coredumps-linux` artifact), produced
by `core_pattern` + `coredump_filter=0x7F`:

| Run | Job | Tests passed before crash |
| --- | --- | --- |
| 24357164079 | 71127034022 | 37 |
| 24358771497 | 71132613411 | 40 |
| 24359053297 | 71133590038 | 84 |

Tool: `dotnet-dump analyze` (10.0.x from the dnceng daily feed) + `gdb`.

## What we ruled out

### Struct layout mismatch between C# and uplink-c

Added `scripts/ci/struct_layout_check.c` — compiles against
`uplink_definitions.h`, emits CSV of every struct's `sizeof` and field
`offsetof`. `NativeStructLayoutTests` compares against `Marshal.SizeOf<T>`
and `Marshal.OffsetOf<T>` for every P/Invoke struct.

**Result**: all 111 entries match. Not a layout issue.

### Buffer retention across the cgo boundary

Traced `uplink_upload_write` → `uplink.Upload.Write` → `streams.Upload.Write`
→ `splitter.Write` → `baseSplitter.Write` → `encryptedBuffer.Write`
→ `transformedWriter.Write`. Every path either `copy(...)` or
`append(..., p...)` — the C# caller's pinned buffer is fully consumed
before `Write` returns.

**Result**: `io.Writer`'s "must not retain p" contract is honored. Not a
buffer-lifetime issue.

### Double-free of a native handle

Walked `Access.ProjectHandleLease.Dispose` — uses `Interlocked.Exchange`
to atomically clear `_owner`, so `ReleaseProjectLease(Handle)` is called
at most once per lease. `FreeProjectHandle` therefore runs at most once
per native project pointer.

**Result**: not a double-free.

### Shared Go state in uplink-c

Read uplink-c source: `handles.go`, `project.go`, `access.go`, `upload.go`,
`config.go`. Every exported function takes `universe.lock` around handle
lookup. Each `Project` has its own `context.Background()` + `WithCancel`,
so cancelling one project does not touch another. `Access` is immutable
after construction.

**Result**: no cross-project shared mutable state that could race.

### Race in uplink / uplink-c Go code

Tried `go build -race -buildmode=c-shared`, loaded via `LD_PRELOAD` with
`TSAN_OPTIONS=handle_segv=0 handle_sigill=0 handle_sigfpe=0 handle_sigbus=0`.
Several iterations chasing env-var and shell-scope bugs in the CI workflow;
the job is in place but at the point of writing we have not captured a
race report. **If the hypothesis below is correct we never will**: the
crash is not a data race.

### C# `IDisposable` / analyzer findings

Added `IDisposableAnalyzers` and `Microsoft.VisualStudio.Threading.Analyzers`.
Surfaced 43 warnings; triaged:

- Empty `Dispose(bool)` on `UploadOperation` / `DownloadOperation` leaks
  `ProjectHandleLease` on early-exit paths. Real correctness issue; does
  not cause SIGSEGV, only resource pressure.
- `Access` finalizer takes a lock and makes P/Invokes. Real hygiene issue;
  not a memory-safety violation.
- `UploadQueueService.Dispose` uses sync-over-async. Real correctness
  issue; causes deadlocks, not crashes.
- Unobserved task returned by `StartUploadAsync` / `StartDownloadAsync`.
  Real correctness issue; swallows exceptions.

**Result**: legitimate fixes to land, but none of them explain SIGSEGV
on their own.

## The shared crash signature

All three cores produce identical `siginfo` output:

```
si_signo = 11   (SIGSEGV)
si_code  = 128  (SI_KERNEL)
si_addr  = 0x0
```

And identical register context on the faulting thread:

```
rip at libc offset 0xd40    (futex wait wrapper prologue; only ASLR base differs)
rbp - rsp = 0x40            (same stack frame across all three)
```

`si_code = SI_KERNEL` is the critical clue. That is **not** a normal
page fault: Linux reports `SEGV_MAPERR` (1) or `SEGV_ACCERR` (2) for
those. It is **not** from userspace `kill()` / `raise()`: those give
`SI_USER` (0) or `SI_TKILL` (-6). `SI_KERNEL` is what the kernel emits
when it calls `force_sig()` itself, for reasons that do not fit any of
the normal SIGSEGV sub-codes.

The most common scenario that produces `SI_KERNEL` SIGSEGV with
`si_addr=0x0`:

> A signal handler ran on a `sigaltstack`, overflowed that stack, hit
> the guard page below it, and the kernel synthesized a second SIGSEGV
> because it could not deliver one the normal way while the first was
> still being handled.

The faulting-thread register state we see is not the original fault
site — it is CoreCLR's signal handler already running a futex_wait to
coordinate with the crash-dump writer. The *real* fault happened
earlier, on this same thread, inside the signal handler itself.

## Hypothesis

### Chain of events

1. A thread enters `libstorj_uplink.so` via P/Invoke (upload_write,
   upload_commit, download_read, etc.).
2. Go's cgo runtime installs a `sigaltstack` on that thread — an
   alternate stack for signal handlers, default size approximately 32 KB.
3. The thread returns to managed code. The `sigaltstack` configuration
   persists on the thread.
4. CoreCLR's JIT emits code containing **managed null-reference probes**:
   a deliberate dereference of address 0 whose SIGSEGV is caught and
   translated to `NullReferenceException`. These fire constantly; every
   `.Method()` call on a reference type has one.
5. A null-ref probe fires. The kernel delivers SIGSEGV. Because
   `sigaltstack` is set on this thread, the signal handler runs on the
   Go-provided alt stack.
6. **CoreCLR's SIGSEGV handler overflows the 32 KB alt stack.** CoreCLR's
   handler is complex: walks managed stacks, touches type tables,
   allocates scratch memory, may call further into the runtime.
7. The overflow hits the guard page below the alt stack. The kernel
   detects a page fault during signal delivery — a double fault.
8. The kernel synthesizes SIGSEGV with `SI_KERNEL` and kills the process.

### Evidence that fits this chain

| Observation | Fits the hypothesis? |
| --- | --- |
| `si_code = SI_KERNEL` | Yes — double fault on alt stack |
| `si_addr = 0x0` | Yes — synthesized kernel signal has no associated address |
| Identical `rip` offset across all three cores | Yes — all three land in CoreCLR handler's internal futex |
| Different faulting test every run | Yes — the null-ref probe fires whenever CoreCLR feels like it |
| Always involves a thread that has done cgo | Yes — only those threads have the sigaltstack |
| Every Blame candidate set includes at least one test doing concurrent uploads / downloads / dispose-mid-transfer | Yes — those are the cgo-heavy paths |
| No TSan race reports (once the job was wired up correctly) | Yes — it is not a race |
| No managed minidump ever produced, even with `DOTNET_DbgEnableMiniDump=1` | Yes — createdump runs *inside* the broken signal handler |
| Not reproducible in pure Go reproducers against `storj.io/uplink` | Yes — requires CoreCLR's large handler to overflow |
| 10–20 live `Access` and 20–35 live `ProjectHandleLease` at crash time | Consistent — not required, but high cgo activity widens the window |

### Evidence against other hypotheses

- **Not a buffer race** — verified by reading every transitive `Write`.
- **Not a struct layout bug** — verified by `NativeStructLayoutTests`.
- **Not a double-free** — `ProjectHandleLease.Dispose` is `Interlocked`.
- **Not a data race in Go** — data races do not produce `SI_KERNEL`
  SIGSEGV; they produce `runtime.throw` with goroutine tracebacks.
- **Not a Dispose-ordering bug** — the C# lease-refcount pattern is
  correct and would produce `ObjectDisposedException` or use-after-free
  with `SEGV_MAPERR` / `SEGV_ACCERR`, not `SI_KERNEL`.

## Root cause identified: SIGRT_2 handler overflows Go's 16 KB sigaltstack

### The strace evidence (job 71263554933, run 24398819014)

Running `dotnet test` under `strace -f -e trace=signal` captured the
exact signal sequence at the moment of the crash:

```
5962  12:28:00 sigaltstack({ss_sp=NULL, ss_flags=SS_DISABLE, ss_size=2048},
                          {ss_sp=0x7fd4600ab000, ss_flags=0, ss_size=16384}) = 0
5962  12:28:00 +++ exited with 0 +++
5911  12:28:00 tgkill(5771, 5963, SIGRT_2)
5963  12:28:00 --- SIGRT_2 {si_signo=SIGRT_2, si_code=SI_TKILL} ---
5963  12:28:00 --- SIGSEGV {si_signo=SIGSEGV, si_code=SEGV_ACCERR, si_addr=0x7fd4600a7ff0} ---
5963  12:28:00 --- SIGSEGV {si_signo=SIGSEGV, si_code=SI_KERNEL, si_addr=NULL} ---
              ... 20+ threads killed by SIGSEGV (core dumped) ...
```

Three signals on thread 5963 in rapid succession:

1. **`SIGRT_2`** — Go's internal preemption signal, sent by thread 5911
   via `tgkill`. Go uses SIGRT_2 (not SIGURG with `asyncpreemptoff=1`)
   for cooperative preemption scheduling.
2. **`SIGSEGV` with `si_code=SEGV_ACCERR` at `0x7fd4600a7ff0`** — a real
   access-permission fault. This address is at the guard page immediately
   below a 16 KB sigaltstack whose top was at `0x7fd4600ab000`:
   - stack base = `0x7fd4600ab000 - 0x4000 = 0x7fd4600a7000`
   - fault addr = `0x7fd4600a7ff0`
   - offset below base = `0x10` (16 bytes into the guard page)
3. **`SIGSEGV` with `si_code=SI_KERNEL`** — the kernel cannot deliver a
   nested SIGSEGV while the previous one is being handled on the same
   alt stack. It calls `force_sig(SIGSEGV)` which kills the process.

### The mechanism

1. Thread 5963 is a Go M thread with a 16 KB sigaltstack.
2. Thread 5911 sends `SIGRT_2` to 5963 for goroutine preemption.
3. The kernel delivers SIGRT_2 on thread 5963's sigaltstack (16 KB).
4. Go's SIGRT_2 handler runs and **overflows the 16 KB alt stack**,
   writing past the bottom into the guard page.
5. The guard-page write triggers `SIGSEGV (SEGV_ACCERR)` at the
   guard address.
6. The kernel tries to deliver this SIGSEGV but thread 5963 is already
   on the sigaltstack handling SIGRT_2. The kernel cannot set up another
   signal frame → calls `force_sig(SIGSEGV)` with `SI_KERNEL`.
7. Process is killed. Core dump is written.

### Why previous sigaltstack fix didn't work

The 1 MB sigaltstack fix (via `SigStackFix.EnsureOnCurrentThread()`)
was installed on .NET managed threads from `Access.AcquireProjectLease`
and the `Access` constructor. But thread 5963 is a **Go runtime M
thread** — it was created by Go's scheduler, not by .NET. Our fix never
ran on it. Go sets its own 16 KB sigaltstack on all its threads via
`minitSignalStack()`.

### Why `asyncpreemptoff=1` didn't help

`asyncpreemptoff=1` disables SIGURG-based async preemption (Go 1.14+).
But Go also uses SIGRT_2 for other internal signaling between Ms. The
SIGRT_2 in the strace is `SI_TKILL` (sent explicitly by thread 5911),
not the async preemption path. `asyncpreemptoff` does not disable all
inter-M signaling.

### Earlier hypotheses — tested and ruled out

| Hypothesis | How tested | Result |
| --- | --- | --- |
| Sigaltstack overflow on .NET threads | 1 MB shim on managed threads, pre+post cgo | Crash unchanged — the overflowing thread is a Go thread, not .NET |
| Go handler not chaining SIGSEGV to CoreCLR | Standalone C test on arm64 + amd64 | PASS — Go chains correctly |
| Go SIGURG preemption causing signal failure | `GODEBUG=asyncpreemptoff=1` | Crash persists — it's SIGRT_2, not SIGURG |
| CoreCLR handler double-fault | `DOTNET_EnableAlternateStackCheck=1`, `DOTNET_EnableWriteXorExecute=0`, stderr capture | No CoreCLR output; crash is on a Go thread |
| W^X JIT page protection | `DOTNET_EnableWriteXorExecute=0` | Crash unchanged |

### Correlation with concurrency

The crash requires heavy concurrent cgo activity because:
- More concurrent cgo calls → more Go M threads → more sigaltstacks.
- More inter-M signaling (SIGRT_2 for work-stealing, goroutine handoff).
- Higher chance that one SIGRT_2 handler call exceeds 16 KB on the alt
  stack. The overflow depends on what the handler touches: Go's scheduler
  state, goroutine context save, etc. Deeper scheduler call chains →
  more stack usage → higher overflow probability.

### Fix

The root fix is upstream in Go: Go's sigaltstack of 16 KB is too small
for its own SIGRT_2 handler under heavy scheduling load. Options:

1. **Increase Go's sigaltstack size.** Go 1.25 uses 16384 bytes.
   `MINSIGSTKSZ` on Linux x86_64 is 2048, `SIGSTKSZ` is 8192, but
   Go's handler needs more under load. Increasing to 64 KB or 128 KB
   would provide headroom. This requires a Go runtime change.

2. **Reduce SIGRT_2 handler stack usage.** Go's scheduler code runs in
   the handler context; deep call chains can overflow. Making the
   handler thinner (defer work to a goroutine) would reduce stack
   pressure.

3. **Workaround in uplink-c or uplink.NET**: intercept Go's thread
   creation and install a larger sigaltstack on Go-spawned threads.
   This is fragile — Go doesn't expose thread-creation hooks.

4. **Reduce concurrency** to lower the number of active Go M threads
   and thus the frequency of inter-M signaling. Pragmatic but doesn't
   fix the underlying issue.

## Proposed experiments (ordered by cost)

### 1. Install a larger `sigaltstack` before any cgo call

Write a tiny native C startup helper or use `pthread_attr_setstack` /
`sigaltstack` from a `[ModuleInitializer]` in C#. Make the alt stack
256 KB or 1 MB.

If the crash disappears: hypothesis confirmed. The fix is to ship such
a helper as part of the library's initialization.

### 2. Re-assert CoreCLR's signal handler after Go has claimed it

CoreCLR's handler setup can be re-run. One approach: call a dummy Go
function at process startup (before tests), then trigger an operation
that CoreCLR uses to probe its own handler state. If CoreCLR chains on
top of Go's instead of vice-versa, CoreCLR's handler runs first and
owns the alt stack.

### 3. Disable CoreCLR's SIGSEGV-based null-ref handling

If feasible (environment variable or runtime switch), run the test
process with managed null-ref checks performed explicitly in IL rather
than via SIGSEGV. This is likely slower but removes the trigger.

### 4. Upstream report

If (1) or (2) confirm the hypothesis, file an issue with `dotnet/runtime`
(and/or `golang/go`) describing the signal-handler-size interaction when
Go c-shared libraries are loaded into a CoreCLR process.

## Instrumentation in place (usable for any follow-up)

- `scripts/ci/struct_layout_check.c` + `NativeStructLayoutTests` —
  validates every P/Invoke struct matches the C header. Runs on every
  Linux CI job.
- `scripts/ci/analyze-core.sh` — drop-in local reproducer of the CI
  `dotnet-dump analyze` step. Usage:
  `scripts/ci/analyze-core.sh /path/to/core.<...>`.
- `.github/workflows/ci.yml` pipe-`core_pattern` helper — every SIGSEGV
  produces a kernel core with `coredump_filter=0x7F` (full memory),
  uploaded as `ci-coredumps-linux`.
- Race-detector job (`build-and-test-linux-race`) — `go build -race` +
  LD_PRELOAD + GORACE/TSAN_OPTIONS. Currently produces no race reports,
  which is itself informative given the hypothesis.
- `DisposeStressTests` — five tests that deliberately exercise
  rapid-create/dispose and dispose-mid-transfer patterns. Reproduces the
  crash more reliably than the regular suite.

## Related upstream issues

### Go runtime — signal handler + sigaltstack

**Directly relevant to our crash:**

- [go#43853](https://github.com/golang/go/issues/43853) — `morestack on
  gsignal` due to g0 stack misattribution. When cgo is enabled, Go
  estimates g0 stack bounds incorrectly, causing signal-handling functions
  to run out of stack space. Closest match to our mechanism.
- [go#60007](https://github.com/golang/go/issues/60007) — Signal
  delivered on shallow g0 stack by TSan. TSan queues signals and delivers
  them from the normal thread stack instead of the sigaltstack, causing
  overflow. Same pattern as ours: "signal handler overflows its
  designated stack."
- [go#77588](https://github.com/golang/go/issues/77588) — "signal 27
  received on thread with no signal stack" (February 2026, Go 1.25.7).
  Current Go version, signal stack problems persist.
- [go#7227](https://github.com/golang/go/issues/7227) — Crash when C
  library resets sigaltstack/sigaction settings. Directly about
  interaction between Go and another runtime modifying signal state.

**Signal handler chaining / double-fault:**

- [go#13978](https://github.com/golang/go/issues/13978) — `SIGSEGV
  while handling SIGSEGV` — exactly our SI_KERNEL pattern (nested
  SIGSEGV during handler execution).
- [go#17641](https://github.com/golang/go/issues/17641) — `sigfwd`
  calls C handlers with improper stack alignment, causing a second
  SIGSEGV inside the first handler. Same class of double-fault.
- [go#14899](https://github.com/golang/go/issues/14899) — c-shared
  library's signal handler overrides the default handler.
- [go#16468](https://github.com/golang/go/issues/16468) — "non-Go code
  disabled sigaltstack" panic — Go detects another runtime modified the
  sigaltstack.

### .NET runtime — signal handler conflicts

- [dotnet/runtime#43642](https://github.com/dotnet/runtime/issues/43642)
  — "CoreCLR taking over signal handlers." Discusses signal handler
  conflicts when CoreCLR coexists with other native runtimes.
- [dotnet/runtime#12798](https://github.com/dotnet/runtime/issues/12798)
  — "Possible crash relating to signal handling." CoreCLR's signal
  handler interaction with third-party libraries.
- [dotnet/runtime#99151](https://github.com/dotnet/runtime/issues/99151)
  — Segmentation fault in libcoreclr since .NET 8. Signal handling
  regression in newer .NET versions.
- [dotnet/runtime#121581](https://github.com/dotnet/runtime/issues/121581)
  — Crash/deadlock in CoreCLR's inject signal handler on Linux with
  glibc ≥ 2.40. Shows CoreCLR's signal handler does non-async-safe work
  that can crash.

### Most useful for filing upstream

[go#43853](https://github.com/golang/go/issues/43853) is the closest
match — it describes Go's signal handler running out of stack space on
the gsignal stack during cgo. Our strace evidence (SIGRT_2 → SEGV_ACCERR
at guard page → SI_KERNEL) would be a strong data point for a new issue
referencing it.

[go#77588](https://github.com/golang/go/issues/77588) is the freshest
(Go 1.25, February 2026) and shows the problem is actively present in
the Go version we're using.

## References

- `docs/crash-investigation.md` — this file.
- `scripts/ci/struct_layout_check.c` — native layout baseline.
- `scripts/ci/test_signal_chain.c` — standalone signal-chaining test.
- `scripts/repro-sigaltstack/` — standalone C + Go reproducer.
- `tests/uplink.NET.IntegrationTests/NativeStructLayoutTests.cs` —
  struct-layout assertion.
- `tests/uplink.NET.IntegrationTests/DisposeStressTests.cs` — reliable
  reproducer workloads.
- `Directory.Build.props`, `src/uplink.NET/uplink.NET.csproj` —
  analyzer configuration.
