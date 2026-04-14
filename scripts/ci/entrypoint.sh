#!/usr/bin/env bash
# Runtime entrypoint for the CI-repro container. Stages the prebuilt native
# library into the bind-mounted repo, enables core dumps, and then runs
# whatever command was passed (defaults to `dotnet test ...`).

set -euo pipefail

RID="$(cat /native/rid)"
TARGET_DIR="/src/src/uplink.NET/runtimes/${RID}/native"
mkdir -p "$TARGET_DIR"
cp "/native/${RID}/libstorj_uplink.so" "$TARGET_DIR/libstorj_uplink.so"
echo "[entrypoint] staged libstorj_uplink.so for ${RID} into $TARGET_DIR"

# Where managed/native dumps go. TestResults is bind-mounted out by repro.sh.
export CI_DIAGNOSTICS_DIR="${CI_DIAGNOSTICS_DIR:-/src/TestResults/diagnostics}"
COREDUMP_DIR="/src/TestResults/coredumps"
mkdir -p "$CI_DIAGNOSTICS_DIR" "$COREDUMP_DIR"
chmod 1777 "$COREDUMP_DIR"

# Enable kernel core dumps.
#
# The kernel's core_pattern decides where dumps land. Docker-Desktop-for-Mac
# inherits /proc/sys/kernel/core_pattern from the Linux VM, which on recent
# versions pipes into systemd-coredump or is empty — either way, dumps won't
# show up in our bind-mounted dir unless we override it.
#
# Writing to /proc/sys/kernel/core_pattern requires CAP_SYS_ADMIN. repro.sh
# passes --cap-add SYS_ADMIN; if that succeeded we can set a writable pattern.
# Best-effort: silently fall through to whatever the host has if we can't.
ulimit -c unlimited || true
if echo "${COREDUMP_DIR}/core.%e.%p.%t" > /proc/sys/kernel/core_pattern 2>/dev/null; then
    echo "[entrypoint] core_pattern = $(cat /proc/sys/kernel/core_pattern)"
else
    echo "[entrypoint] could not set core_pattern (need --cap-add SYS_ADMIN); current: $(cat /proc/sys/kernel/core_pattern 2>/dev/null || echo unknown)"
    echo "[entrypoint] fallback: running from $COREDUMP_DIR so relative-path cores land there"
fi

# .NET in-process crash dumps (managed crashes). Both DOTNET_* and COMPlus_*
# env-var forms are recognized by the .NET runtime; setting both guarantees
# any runtime version honors it.
export DOTNET_DbgEnableMiniDump=1
export DOTNET_DbgMiniDumpType=4                    # 4 = full (with heap)
export DOTNET_DbgMiniDumpName="${COREDUMP_DIR}/managed-coredump.%p"
export COMPlus_DbgEnableMiniDump=1
export COMPlus_DbgMiniDumpType=4
export COMPlus_DbgMiniDumpName="${COREDUMP_DIR}/managed-coredump.%p"
export COMPlus_EnableDiagnostics=1

# THE single most useful env var for diagnosing uplink-c crashes:
# GOTRACEBACK=crash makes the Go runtime
#   (1) print all goroutine stacks to stderr, AND
#   (2) call abort() which produces a core dump (honoring ulimit -c / core_pattern).
# Without this, a runtime.throw or fatal panic inside uplink-c just calls
# exit(2) with no usable artifact.
export GOTRACEBACK=crash

# cgocheck=2 was removed from runtime in Go 1.21 (now a build-time
# GOEXPERIMENT). cgocheck=1 is the default; keep invalidptr/clobberfree on.
export GODEBUG="${GODEBUG:-cgocheck=1,invalidptr=1,clobberfree=1}"
export MALLOC_CHECK_="${MALLOC_CHECK_:-3}"

# Native library search path for P/Invoke.
export LD_LIBRARY_PATH="$TARGET_DIR:${LD_LIBRARY_PATH:-}"

# Diagnostics hooks that the uplink.NET project already reads (see ci.yml).
export UPLINK_NET_ENABLE_DIAGNOSTICS=1
export UPLINK_NET_DIAGNOSTICS_DIR="$CI_DIAGNOSTICS_DIR"

echo "[entrypoint] GOTRACEBACK=$GOTRACEBACK"
echo "[entrypoint] GODEBUG=$GODEBUG"
echo "[entrypoint] LD_LIBRARY_PATH=$LD_LIBRARY_PATH"
echo "[entrypoint] COREDUMP_DIR=$COREDUMP_DIR"
echo "[entrypoint] running: $*"

# Run from the coredump dir so any core file written with a relative filename
# (the Linux default when core_pattern is just "core") lands where we collect.
cd "$COREDUMP_DIR"

# After the command exits, list any artifacts we produced. This runs even on
# failure (trap EXIT, no `set -e` exit on the command itself) so we can see
# whether a dump was actually captured.
report_artifacts() {
    echo
    echo "[entrypoint] post-run artifact scan:"
    find "$COREDUMP_DIR" -maxdepth 2 -type f \( -name 'core*' -o -name 'coredump*' -o -name 'managed-coredump*' -o -name '*.dmp' \) -printf '  %p (%s bytes)\n' 2>/dev/null || true
    # Also check /tmp in case something dumped there
    find /tmp -maxdepth 2 -type f \( -name 'core*' -o -name 'coredump*' \) -printf '  %p (%s bytes)\n' 2>/dev/null | head -20 || true
}
trap report_artifacts EXIT

# If the command is `dotnet test` without --blame-crash, inject it so the
# crashing test and a full dump are always captured. Run from /src so project
# paths resolve, but TestResults is absolute.
if [[ "$1" == "dotnet" && "${2:-}" == "test" && "$*" != *"--blame-crash"* ]]; then
    cd /src
    extra=(--blame-crash --blame-crash-dump-type full \
           --blame-hang-timeout 10m \
           --results-directory /src/TestResults \
           --diag:/src/TestResults/diag/vstest.log \
           --logger "trx;LogFilePrefix=linux-repro")
    "$@" "${extra[@]}"
    exit $?
fi

cd /src
"$@"
