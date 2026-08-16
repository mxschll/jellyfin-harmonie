#!/usr/bin/env bash
# Regenerates docs/playlists.png from the current cover designs.
# Requires a .NET 10 SDK (override with DOTNET=/path/to/dotnet).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
DOTNET="${DOTNET:-dotnet}"

"$DOTNET" run --project "$ROOT/tools/CoverBanner" -- "$ROOT/docs/playlists.png"
