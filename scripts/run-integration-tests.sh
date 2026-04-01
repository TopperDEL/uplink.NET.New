#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TEST_PROJECT="$REPO_ROOT/tests/uplink.NET.IntegrationTests/uplink.NET.IntegrationTests.csproj"
RUNTIMES_ROOT="$REPO_ROOT/src/uplink.NET/runtimes"

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
  git clone --depth 1 https://github.com/storj/uplink-c.git "$uplink_c_dir"
fi

if [[ ! -d "$uplink_c_dir" ]]; then
  echo "uplink-c directory '$uplink_c_dir' does not exist." >&2
  exit 1
fi

(
  cd "$uplink_c_dir"
  make build
)

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

DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_HOME=/tmp dotnet test "$TEST_PROJECT" -c Release
