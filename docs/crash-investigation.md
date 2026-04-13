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

## References

- `docs/crash-investigation.md` — this file.
- `scripts/ci/struct_layout_check.c` — native layout baseline.
- `tests/uplink.NET.IntegrationTests/NativeStructLayoutTests.cs` —
  struct-layout assertion.
- `tests/uplink.NET.IntegrationTests/DisposeStressTests.cs` — reliable
  reproducer workloads.
- `Directory.Build.props`, `src/uplink.NET/uplink.NET.csproj` —
  analyzer configuration.
