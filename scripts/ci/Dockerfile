# Reproduces the CI Linux test environment for debugging "Test host process crashed".
#
# Matches .github/workflows/ci.yml: ubuntu-latest, .NET 10 SDK, Go 1.25, zig, uplink-c v1.14.0.
# Builds libuplink.so at image-build time so we can iterate on .NET code by bind-mounting
# the repo at run time without rebuilding native.
#
# Usage: see scripts/ci/repro.sh

FROM ubuntu:24.04

ARG UPLINK_C_REF=v1.14.0
ARG GO_VERSION=1.25.0
ARG ZIG_VERSION=0.15.2
# Set to "cgocheck2" to rebuild uplink-c with the cgocheck2 Go experiment,
# which enables deep checking of pointers passed across the cgo boundary.
# This is the replacement for runtime GODEBUG=cgocheck=2 (removed in Go 1.21).
# Leave empty for a normal build.
ARG GOEXPERIMENT=
# TARGETARCH is set automatically by buildx (amd64 / arm64).
ARG TARGETARCH

ENV DEBIAN_FRONTEND=noninteractive

# Core deps + gdb for symbolizing crashes.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl git make xz-utils python3 \
        build-essential pkg-config \
        gdb lldb strace \
        libicu-dev \
    && rm -rf /var/lib/apt/lists/*

# .NET 10 SDK via Microsoft's dotnet-install script (works on any Ubuntu, no apt repo needed).
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
    && rm /tmp/dotnet-install.sh
ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_NOLOGO=1

# Go
RUN set -eux; \
    case "${TARGETARCH}" in \
      amd64) GO_ARCH=amd64 ;; \
      arm64) GO_ARCH=arm64 ;; \
      *) echo "unsupported arch ${TARGETARCH}"; exit 1 ;; \
    esac; \
    curl -fsSL "https://go.dev/dl/go${GO_VERSION}.linux-${GO_ARCH}.tar.gz" -o /tmp/go.tgz; \
    tar -C /usr/local -xzf /tmp/go.tgz; \
    rm /tmp/go.tgz
ENV PATH=/usr/local/go/bin:$PATH

# Zig (required by uplink-c Makefile for cross-builds; harmless for native build).
# Note: Zig 0.14+ reversed the URL/filename layout to <arch>-<os> (was <os>-<arch>).
RUN set -eux; \
    case "${TARGETARCH}" in \
      amd64) ZIG_ARCH=x86_64 ;; \
      arm64) ZIG_ARCH=aarch64 ;; \
    esac; \
    curl -fsSL "https://ziglang.org/download/${ZIG_VERSION}/zig-${ZIG_ARCH}-linux-${ZIG_VERSION}.tar.xz" -o /tmp/zig.tar.xz; \
    mkdir -p /opt/zig; \
    tar -C /opt/zig --strip-components=1 -xJf /tmp/zig.tar.xz; \
    ln -s /opt/zig/zig /usr/local/bin/zig; \
    rm /tmp/zig.tar.xz

# Build uplink-c and stage the shared library where the .NET project expects it.
# Detect the native RID from TARGETARCH so arm64 hosts stage into linux-arm64/native.
RUN set -eux; \
    git clone --branch "${UPLINK_C_REF}" --depth 1 https://github.com/storj/uplink-c.git /tmp/uplink-c; \
    cd /tmp/uplink-c; \
    GOEXPERIMENT="${GOEXPERIMENT}" make build; \
    case "${TARGETARCH}" in \
      amd64) RID=linux-x64 ;; \
      arm64) RID=linux-arm64 ;; \
    esac; \
    mkdir -p "/native/${RID}"; \
    cp .build/libuplink.so "/native/${RID}/libstorj_uplink.so"; \
    echo "${RID}" > /native/rid

# Workdir will be bind-mounted from the host at run time.
WORKDIR /src

# The runtime entrypoint stages the prebuilt .so into the mounted repo, enables
# core dumps, and runs dotnet test with --blame-crash so crashes produce artifacts.
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["dotnet", "test", "uplink.NET.sln", "-c", "Debug"]
