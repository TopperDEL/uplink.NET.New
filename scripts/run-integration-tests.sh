#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TEST_PROJECT="$REPO_ROOT/tests/uplink.NET.IntegrationTests/uplink.NET.IntegrationTests.csproj"
RUNTIMES_ROOT="$REPO_ROOT/src/uplink.NET/runtimes"
UPLINK_C_REF="${UPLINK_C_REF:-v1.10.1}"
TEST_RESULTS_DIR="${TEST_RESULTS_DIR:-$REPO_ROOT/TestResults}"
CRASH_DIAGNOSTICS_DIR="${CRASH_DIAGNOSTICS_DIR:-$TEST_RESULTS_DIR/crash-diagnostics}"
CORE_DUMP_DIR="${CORE_DUMP_DIR:-$CRASH_DIAGNOSTICS_DIR/core}"
NATIVE_SYMBOL_DIR="${NATIVE_SYMBOL_DIR:-$CRASH_DIAGNOSTICS_DIR/native-symbols}"

if [[ -z "${TEST_ACCESS_GRANT:-}" || -z "${TEST_BUCKET:-}" ]]; then
  echo "TEST_ACCESS_GRANT and TEST_BUCKET must be set before running integration tests." >&2
  exit 1
fi

case "$(uname -s):$(uname -m)" in
  Linux:x86_64)
    rid="linux-x64"
    source_name="libuplink.so"
    target_name="libstorj_uplink.so"
    library_path_name="LD_LIBRARY_PATH"
    ;;
  Linux:aarch64|Linux:arm64)
    rid="linux-arm64"
    source_name="libuplink.so"
    target_name="libstorj_uplink.so"
    library_path_name="LD_LIBRARY_PATH"
    ;;
  Darwin:x86_64)
    rid="osx-x64"
    source_name="libuplink.dylib"
    target_name="libstorj_uplink.dylib"
    library_path_name="DYLD_LIBRARY_PATH"
    ;;
  Darwin:arm64)
    rid="osx-arm64"
    source_name="libuplink.dylib"
    target_name="libstorj_uplink.dylib"
    library_path_name="DYLD_LIBRARY_PATH"
    ;;
  *)
    echo "Unsupported platform $(uname -s):$(uname -m)." >&2
    exit 1
    ;;
esac

work_dir=""
uplink_c_dir="${UPLINK_C_DIR:-}"
if [[ -z "$uplink_c_dir" ]]; then
  work_dir="$(mktemp -d)"
  trap 'rm -rf "$work_dir"' EXIT
  uplink_c_dir="$work_dir/uplink-c"
  git clone --branch "$UPLINK_C_REF" --depth 1 https://github.com/storj/uplink-c.git "$uplink_c_dir"
fi

if [[ ! -d "$uplink_c_dir" ]]; then
  echo "uplink-c directory '$uplink_c_dir' does not exist." >&2
  exit 1
fi

(
  cd "$uplink_c_dir"
  make build
)

storj_uplink_version="$(git -C "$uplink_c_dir" describe --tags --always --dirty)"

target_dir="$RUNTIMES_ROOT/$rid/native"
mkdir -p "$target_dir"
cp "$uplink_c_dir/.build/$source_name" "$target_dir/$target_name"

echo "Staged $target_name in $target_dir"

existing_library_path="${!library_path_name:-}"
if [[ -n "$existing_library_path" ]]; then
  export "$library_path_name=$target_dir:$existing_library_path"
else
  export "$library_path_name=$target_dir"
fi

dotnet_test_args=(
  "$REPO_ROOT/uplink.NET.sln"
  -c Release
  -p:StorjUplinkVersion="$storj_uplink_version"
  --results-directory "$TEST_RESULTS_DIR"
  --logger "trx;LogFilePrefix=linux-integration-tests"
)

if [[ "$rid" == linux-* ]]; then
  mkdir -p "$CORE_DUMP_DIR" "$NATIVE_SYMBOL_DIR"
  cp "$uplink_c_dir/.build/$source_name" "$NATIVE_SYMBOL_DIR/$target_name"

  if command -v objcopy >/dev/null 2>&1; then
    objcopy --only-keep-debug "$uplink_c_dir/.build/$source_name" "$NATIVE_SYMBOL_DIR/$target_name.debug" || true
  fi

  export DOTNET_DbgEnableMiniDump=1
  export DOTNET_DbgMiniDumpType=4
  export DOTNET_DbgMiniDumpName="$CRASH_DIAGNOSTICS_DIR/dotnet-dump.%p.%e.%t"

  if ulimit -c unlimited 2>/dev/null; then
    echo "Enabled unlimited core dumps for the current shell."
  else
    echo "Could not enable unlimited core dumps; continuing with .NET crash dumps only." >&2
  fi

  dotnet_test_args+=(
    --diag "$CRASH_DIAGNOSTICS_DIR/vstest-diag.log"
    --blame-crash
    --blame-crash-dump-type full
  )
fi

DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_HOME=/tmp dotnet test "${dotnet_test_args[@]}"
