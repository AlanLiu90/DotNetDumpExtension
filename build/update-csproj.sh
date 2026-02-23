#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CSPROJ="$SCRIPT_DIR/../src/DotNetDumpExtension/DotNetDumpExtension.csproj"

# --------------------------------------------------------
# Step 1: Detect dotnet-dump version
# (local manifest has higher priority than global)
# --------------------------------------------------------
get_dump_version() {
    dotnet tool list $1 dotnet-dump 2>/dev/null \
        | tail -n +3 | awk 'NF { print $2; exit }' || true
}

VER=$(get_dump_version "")
if [ -z "$VER" ]; then
    VER=$(get_dump_version "-g")
fi

if [ -z "$VER" ]; then
    echo "ERROR: dotnet-dump is not installed. Install it with:" >&2
    echo "  dotnet tool install -g dotnet-dump" >&2
    exit 1
fi

echo "Detected dotnet-dump version: $VER"

# --------------------------------------------------------
# Step 2: Detect TFM from the .store directory
# --------------------------------------------------------
STORE_TOOLS="$HOME/.dotnet/tools/.store/dotnet-dump/$VER/dotnet-dump/$VER/tools"

if [ ! -d "$STORE_TOOLS" ]; then
    echo "ERROR: .store tools directory not found:" >&2
    echo "  $STORE_TOOLS" >&2
    exit 1
fi

TFM=$(ls "$STORE_TOOLS" | head -1)

if [ -z "$TFM" ]; then
    echo "ERROR: Could not determine TFM under:" >&2
    echo "  $STORE_TOOLS" >&2
    exit 1
fi

echo "Detected TFM: $TFM"

# --------------------------------------------------------
# Step 3: Build new DotnetDumpLibPath
# (uses MSBuild $(HOME) macro, not a literal path)
# --------------------------------------------------------
NEW_LIB_PATH='$(HOME)'"/.dotnet/tools/.store/dotnet-dump/$VER/dotnet-dump/$VER/tools/$TFM/any"

echo "New DotnetDumpLibPath: $NEW_LIB_PATH"

# --------------------------------------------------------
# Step 4: Patch the csproj using sed (preserves formatting)
# --------------------------------------------------------
if [ ! -f "$CSPROJ" ]; then
    echo "ERROR: csproj not found at:" >&2
    echo "  $CSPROJ" >&2
    exit 1
fi

if ! grep -q '<DotnetDumpLibPath>' "$CSPROJ"; then
    echo "ERROR: DotnetDumpLibPath element not found in csproj" >&2
    exit 1
fi

CURRENT=$(grep '<DotnetDumpLibPath>' "$CSPROJ" | sed 's|.*<DotnetDumpLibPath>\([^<]*\)</DotnetDumpLibPath>.*|\1|')

if [ "$CURRENT" = "$NEW_LIB_PATH" ]; then
    echo "DotnetDumpLibPath is already up to date."
else
    # Use a temp file + mv for portability across GNU sed (Linux) and BSD sed (macOS)
    sed "s|<DotnetDumpLibPath>[^<]*</DotnetDumpLibPath>|<DotnetDumpLibPath>${NEW_LIB_PATH}</DotnetDumpLibPath>|" \
        "$CSPROJ" > "$CSPROJ.tmp" && mv "$CSPROJ.tmp" "$CSPROJ"
    echo "csproj updated successfully."
fi

echo ""
echo "Done."
echo "  dotnet-dump : $VER"
echo "  TFM         : $TFM"
echo "  LibPath     : $NEW_LIB_PATH"
