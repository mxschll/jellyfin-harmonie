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

# Ensure the plugin is marked Active. Jellyfin disables a plugin if a
# previous load failed; keeping the status forced to Active means a fresh
# build always re-enables it.
META="$PLUGIN_DIR/meta.json"
if [[ -f "$META" ]] && grep -q '"status"' "$META"; then
    /usr/bin/sed -i '' 's/"status":[[:space:]]*"Disabled"/"status": "Active"/' "$META"
fi

echo "==> Starting Jellyfin..."
open -a Jellyfin

echo
echo "Done. Tail the log with:"
echo "  tail -f \"$HOME/Library/Application Support/jellyfin/log/log_\$(date +%Y%m%d).log\" | grep -i harmonie"
