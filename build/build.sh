#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

"$SCRIPT_DIR/update-csproj.sh"

dotnet build "$SCRIPT_DIR/../src/DotNetDumpExtension.sln" -c Release
