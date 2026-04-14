#!/usr/bin/env bash
# Reproduces the Linux CI test crash locally via Docker.
#
# Defaults to linux/amd64 to match GitHub's ubuntu-latest runners. On Apple
# Silicon this uses QEMU emulation (slow but architecturally faithful). Pass
# PLATFORM=linux/arm64 to run natively on arm64 Macs — much faster, but may
# not reproduce arch-specific crashes.
#
# Env vars:
#   PLATFORM         linux/amd64 (default) or linux/arm64
#   UPLINK_C_REF     uplink-c tag to build (default: v1.14.0, matches ci.yml)
#   TEST_ACCESS_GRANT, TEST_BUCKET   pass through if you want integration tests to run
#   REBUILD          set to 1 to force docker build --no-cache
#   SHELL_ONLY       set to 1 to drop into an interactive shell instead of running tests
#
# Examples:
#   scripts/ci/repro.sh
#   PLATFORM=linux/arm64 scripts/ci/repro.sh
#   SHELL_ONLY=1 scripts/ci/repro.sh
#   scripts/ci/repro.sh --filter FullyQualifiedName~StorjSmokeTests

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

PLATFORM="${PLATFORM:-linux/amd64}"
UPLINK_C_REF="${UPLINK_C_REF:-v1.14.0}"
# Set CGOCHECK2=1 to rebuild uplink-c with GOEXPERIMENT=cgocheck2 for deep
# cgo pointer-checking. Uses a separate image tag so it doesn't clobber the
# normal image cache.
CGOCHECK2="${CGOCHECK2:-0}"
GOEXPERIMENT_ARG=""
IMAGE_SUFFIX=""
if [[ "$CGOCHECK2" == "1" ]]; then
    GOEXPERIMENT_ARG="cgocheck2"
    IMAGE_SUFFIX="-cgocheck2"
fi
IMAGE_TAG="uplink-net-ci-repro:${UPLINK_C_REF//\//_}-${PLATFORM//\//_}${IMAGE_SUFFIX}"

build_args=(
    --platform "$PLATFORM"
    --build-arg "UPLINK_C_REF=${UPLINK_C_REF}"
    --build-arg "GOEXPERIMENT=${GOEXPERIMENT_ARG}"
    -t "$IMAGE_TAG"
    -f "$SCRIPT_DIR/Dockerfile"
    "$SCRIPT_DIR"
)
if [[ "${REBUILD:-0}" == "1" ]]; then
    build_args=(--no-cache "${build_args[@]}")
fi

echo "[repro] building $IMAGE_TAG for $PLATFORM (uplink-c $UPLINK_C_REF)"
docker build "${build_args[@]}"

# Clean previous TestResults so the next run's artifacts are unambiguous.
rm -rf "$REPO_ROOT/TestResults"
mkdir -p "$REPO_ROOT/TestResults"

run_args=(
    --rm
    --platform "$PLATFORM"
    # Core dumps need a writable, large-ish ulimit. --ulimit is the one docker
    # flag that still works reliably for this.
    --ulimit core=-1
    # SYS_PTRACE makes createdump (managed dumps) and gdb work.
    --cap-add SYS_PTRACE
    # SYS_ADMIN lets entrypoint.sh override /proc/sys/kernel/core_pattern so
    # native cores from Go's GOTRACEBACK=crash land in our bind-mounted dir.
    --cap-add SYS_ADMIN
    -v "$REPO_ROOT":/src
    -w /src
    -e UPLINK_C_REF="$UPLINK_C_REF"
)

# Pass through integration-test secrets and opt-in flags if set.
[[ -n "${TEST_ACCESS_GRANT:-}" ]] && run_args+=(-e TEST_ACCESS_GRANT)
[[ -n "${TEST_BUCKET:-}" ]]       && run_args+=(-e TEST_BUCKET)
[[ -n "${UPLINK_RUN_STRESS:-}" ]] && run_args+=(-e UPLINK_RUN_STRESS)

if [[ "${SHELL_ONLY:-0}" == "1" ]]; then
    echo "[repro] dropping into shell; 'dotnet test ...' to run, 'gdb' available"
    exec docker run "${run_args[@]}" -it --entrypoint /bin/bash "$IMAGE_TAG"
fi

set +e
docker run "${run_args[@]}" "$IMAGE_TAG" \
    dotnet test uplink.NET.sln -c Release "$@"
rc=$?
set -e

echo
echo "[repro] dotnet test exit code: $rc"
echo "[repro] artifacts:"
echo "  TestResults/              trx, sequence.xml (names the crashing test)"
echo "  TestResults/coredumps/    managed (.dmp) + native core dumps"
echo "  TestResults/diag/         vstest.log"
echo "  TestResults/diagnostics/  uplink.NET runtime diagnostics"
echo
echo "[repro] to symbolize a core dump:"
echo "  PLATFORM=$PLATFORM SHELL_ONLY=1 scripts/ci/repro.sh"
echo "  # inside container:"
echo "  gdb \$(which dotnet) TestResults/coredumps/<dumpfile>"
echo "  (gdb) thread apply all bt"

exit $rc
