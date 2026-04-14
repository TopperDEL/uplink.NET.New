#!/usr/bin/env bash
# Build and run the sigaltstack overflow reproducer.
#
# Usage:
#   scripts/repro-sigaltstack/run.sh          # build + run
#   scripts/repro-sigaltstack/run.sh strace   # build + run under strace
set -euo pipefail

cd "$(dirname "$0")"

echo "=== building Go c-shared library ==="
CGO_ENABLED=1 go build -buildmode=c-shared -o libgolib.so golib.go

echo "=== building C host ==="
cc -O2 -g -o repro host.c -ldl -lpthread

if [[ "${1:-}" == "strace" ]]; then
    echo "=== running under strace ==="
    strace -f -t -e trace=signal -o strace.log \
        env LD_LIBRARY_PATH=. ./repro
    echo "strace log: $(wc -l < strace.log) lines"
    echo "signal kills:"
    grep -E "killed by|--- SIG" strace.log | tail -20
else
    echo "=== running ==="
    LD_LIBRARY_PATH=. ./repro
fi
