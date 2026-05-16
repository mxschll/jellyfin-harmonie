#!/usr/bin/env bash
# Build the plugin and install it into the local macOS Jellyfin data dir.
# Stops Jellyfin first so the DLL isn't held open by a running server,
# then restarts it.
set -euo pipefail

DOTNET="${DOTNET:-/opt/homebrew/opt/dotnet@9/bin/dotnet}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PLUGIN_VERSION="0.1.0.0"
PLUGIN_DIR="$HOME/Library/Application Support/jellyfin/plugins/Jellyfin.Plugin.Harmonie_${PLUGIN_VERSION}"

cd "$ROOT"

echo "==> Building..."
"$DOTNET" publish Jellyfin.Plugin.Harmonie.sln -c Release -f net9.0 | tail -4

if pgrep -f 'Jellyfin.app' > /dev/null; then
    echo "==> Stopping Jellyfin..."
    pkill -f 'Jellyfin.app' || true
    # Wait for it to actually exit so the DLL handle is released.
    for _ in $(seq 1 30); do
        if ! pgrep -f 'Jellyfin.app' > /dev/null; then
            break
        fi
        sleep 0.2
    done
fi

echo "==> Installing to $PLUGIN_DIR"
mkdir -p "$PLUGIN_DIR"
cp Jellyfin.Plugin.Harmonie/bin/Release/net9.0/publish/Jellyfin.Plugin.Harmonie.* "$PLUGIN_DIR/"
# Ship the catalog/dashboard banner next to the DLL.
cp Jellyfin.Plugin.Harmonie/banner.png "$PLUGIN_DIR/"

# Write a meta.json that mirrors the production zip layout. We always
# overwrite — Jellyfin only re-reads it on plugin load anyway, and an
# explicit Active status survives a previous load failure.
cat > "$PLUGIN_DIR/meta.json" <<EOF
{
  "category": "Music",
  "changelog": "",
  "description": "Generate Jellyfin playlists from harmonie audio similarity.",
  "guid": "485e9b6f-f623-4c97-9679-ad33c1db0d18",
  "name": "Harmonie",
  "overview": "Build playlists from harmonie audio similarity.",
  "owner": "mxschll",
  "targetAbi": "10.11.0.0",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)",
  "version": "${PLUGIN_VERSION}",
  "status": "Active",
  "autoUpdate": false,
  "imagePath": "banner.png",
  "assemblies": [
    "Jellyfin.Plugin.Harmonie.dll"
  ]
}
EOF

echo "==> Starting Jellyfin..."
open -a Jellyfin

echo
echo "Done. Tail the log with:"
echo "  tail -f \"$HOME/Library/Application Support/jellyfin/log/log_\$(date +%Y%m%d).log\" | grep -i harmonie"
