#!/usr/bin/env bash
# Analyze a Linux .NET 10 kernel core dump (e.g. downloaded from the
# ci-coredumps-linux artifact) using dotnet-dump + SOS.
#
# Requires Docker (uses the official mcr.microsoft.com/dotnet/sdk:10.0
# image under --platform linux/amd64 to match the CI runner). The dump
# file must have been produced on Linux x86_64.
#
# Usage:
#   scripts/ci/analyze-core.sh <path-to-core-dump> [extra sos command ...]
#
# Example:
#   gh run download <run-id> --repo TopperDEL/uplink.NET.New \
#       --name ci-coredumps-linux -D /tmp/cores
#   scripts/ci/analyze-core.sh '/tmp/cores/core..NET Long Runni.*'
#
# The script installs dotnet-dump from the dotnet-tools daily feed (the
# stable 9.0.x release can't read .NET 10 DACs) and preseeds a useful
# SOS command sequence. Add more commands as positional args to drop
# them in before `exit`.

set -euo pipefail

if [[ $# -lt 1 ]]; then
    echo "usage: $0 <core-dump> [extra-sos-command ...]" >&2
    exit 2
fi

core_path=$(realpath -- "$1")
shift
extra_cmds=("$@")

if [[ ! -f "$core_path" ]]; then
    echo "core dump not found: $core_path" >&2
    exit 2
fi

core_dir=$(dirname -- "$core_path")
core_name=$(basename -- "$core_path")

cmds_file=$(mktemp)
trap 'rm -f "$cmds_file"' EXIT
{
    # setclrpath is filled in inside the container because the exact
    # .NET 10.x version in the sdk image may differ from the runner.
    echo 'setclrpath __CLR_DIR__'
    echo 'modules'
    echo 'threads'
    echo 'clrthreads'
    echo 'clrstack -all'
    echo 'syncblk'
    echo 'pe'
    echo 'dumpheap -stat -type uplink.NET.Models.Access'
    echo 'dumpheap -stat -type uplink.NET.Models.UploadOperation'
    echo 'dumpheap -stat -type uplink.NET.Models.DownloadOperation'
    for cmd in "${extra_cmds[@]}"; do
        echo "$cmd"
    done
    echo 'exit'
} >"$cmds_file"

docker run --rm --platform linux/amd64 \
    -v "$core_dir":/core:ro \
    -v "$cmds_file":/tmp/cmds.txt:ro \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    bash -c '
        set -e
        # Daily feed — the only channel carrying a dotnet-dump build
        # that understands .NET 10 DACs (stable 9.0.x cannot).
        dotnet tool install -g dotnet-dump \
            --add-source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json \
            >/dev/null
        export PATH=$HOME/.dotnet/tools:$PATH
        CLR_DIR=$(ls -d /usr/share/dotnet/shared/Microsoft.NETCore.App/10.*/ | sort -V | tail -1)
        sed "s|__CLR_DIR__|$CLR_DIR|" /tmp/cmds.txt >/tmp/cmds-final.txt
        dotnet-dump analyze "/core/'"$core_name"'" </tmp/cmds-final.txt
    '
