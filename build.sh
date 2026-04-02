#!/usr/bin/env bash
# build.sh - Build vhc-monitor standalone executables
# Usage:
#   ./build.sh                  # Build all platforms
#   ./build.sh win-x64          # Build one platform
#   ./build.sh --local          # Build only current platform

set -euo pipefail

PROJECT="src/VhcMonitor/VhcMonitor.csproj"
DIST="dist"
VERSION=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$PROJECT" | tr -d '[:space:]')

ALL_RIDS=("win-x64" "linux-x64" "osx-arm64" "osx-x64")

TARGETS=()
LOCAL_ONLY=false

for arg in "$@"; do
    case "$arg" in
        --local) LOCAL_ONLY=true ;;
        *)       TARGETS+=("$arg") ;;
    esac
done

if $LOCAL_ONLY; then
    # Detect current platform RID
    OS=$(uname -s)
    ARCH=$(uname -m)
    case "$OS-$ARCH" in
        Darwin-arm64) TARGETS=("osx-arm64") ;;
        Darwin-x86_64) TARGETS=("osx-x64") ;;
        Linux-x86_64) TARGETS=("linux-x64") ;;
        Linux-aarch64) TARGETS=("linux-arm64") ;;
        *) echo "Unknown platform: $OS-$ARCH"; exit 1 ;;
    esac
elif [ ${#TARGETS[@]} -eq 0 ]; then
    TARGETS=("${ALL_RIDS[@]}")
fi

echo ""
echo "=== VHC Monitor Build v$VERSION ==="
echo ""

rm -rf "$DIST"
mkdir -p "$DIST"

FAILED=()

for RID in "${TARGETS[@]}"; do
    OUT="$DIST/$RID"
    EXE="$OUT/vhc-monitor"
    [ "$RID" = "win-x64" ] && EXE="$OUT/vhc-monitor.exe"

    echo "Building $RID..."

    dotnet publish "$PROJECT" \
        -c Release \
        -r "$RID" \
        --self-contained \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -o "$OUT" \
        --nologo -v quiet

    if [ -f "$EXE" ]; then
        SIZE=$(du -sh "$EXE" | cut -f1)
        echo "  -> $EXE ($SIZE)"
    else
        echo "  ERROR: Expected output not found at $EXE"
        FAILED+=("$RID")
    fi
done

echo ""
echo "=== Build Summary ==="
for RID in "${TARGETS[@]}"; do
    OUT="$DIST/$RID"
    EXE="$OUT/vhc-monitor"
    [ "$RID" = "win-x64" ] && EXE="$OUT/vhc-monitor.exe"
    if [ -f "$EXE" ]; then
        echo "  $RID  OK  $EXE"
    else
        echo "  $RID  FAILED"
    fi
done
echo ""

if [ ${#FAILED[@]} -gt 0 ]; then
    echo "Failed: ${FAILED[*]}"
    exit 1
fi
