#!/usr/bin/env bash
# Build and install the current checkout into a Jellyfin Server DMG installation.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/Jellyfin.Plugin.Harmonie/Jellyfin.Plugin.Harmonie.csproj"
BUILD_PROPS="$ROOT/Directory.Build.props"
JELLYFIN_APP="${JELLYFIN_APP:-/Applications/Jellyfin.app}"
JELLYFIN_DATA_DIR="${JELLYFIN_DATA_DIR:-$HOME/Library/Application Support/jellyfin}"
PLUGINS_DIR="$JELLYFIN_DATA_DIR/plugins"
APP_ID="Jellyfin.Server"

if [[ ! -d "$JELLYFIN_APP" ]]; then
    echo "error: Jellyfin Server was not found at $JELLYFIN_APP" >&2
    echo "Set JELLYFIN_APP to the path of Jellyfin.app and try again." >&2
    exit 1
fi

DOTNET="${DOTNET:-$(command -v dotnet || true)}"
if [[ -z "$DOTNET" ]]; then
    for candidate in \
        /opt/homebrew/opt/dotnet@9/bin/dotnet \
        /usr/local/opt/dotnet@9/bin/dotnet
    do
        if [[ -x "$candidate" ]]; then
            DOTNET="$candidate"
            break
        fi
    done
fi
if [[ -z "$DOTNET" || ! -x "$DOTNET" ]]; then
    echo "error: dotnet is not on PATH" >&2
    echo "Install the .NET SDK used by this project, add it to PATH, or set DOTNET." >&2
    exit 1
fi

server_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' \
    "$JELLYFIN_APP/Contents/Info.plist")"
case "$server_version" in
    10.10.*) framework="net8.0"; target_abi="10.10.0.0" ;;
    10.11.*) framework="net9.0"; target_abi="10.11.0.0" ;;
    *)
        echo "error: unsupported Jellyfin version: $server_version" >&2
        echo "This plugin currently supports Jellyfin 10.10.x and 10.11.x." >&2
        exit 1
        ;;
esac

plugin_version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$BUILD_PROPS" | head -1)"
if [[ -z "$plugin_version" ]]; then
    echo "error: could not read <Version> from $BUILD_PROPS" >&2
    exit 1
fi

publish_dir="$ROOT/Jellyfin.Plugin.Harmonie/bin/Release/$framework/publish"
plugin_dir="$PLUGINS_DIR/Jellyfin.Plugin.Harmonie_${plugin_version}"
stage_dir="$PLUGINS_DIR/.harmonie-install.$$"
server_was_running=false
restart_needed=false

cleanup() {
    rm -rf "$stage_dir"
    if [[ "$restart_needed" == true ]]; then
        echo "==> Restarting Jellyfin Server after failed installation" >&2
        open "$JELLYFIN_APP" || true
    fi
}
trap cleanup EXIT

echo "==> Building Harmonie $plugin_version for Jellyfin $server_version ($framework)"
"$DOTNET" publish "$PROJECT" --configuration Release --framework "$framework" --nologo

if [[ ! -f "$publish_dir/Jellyfin.Plugin.Harmonie.dll" ]]; then
    echo "error: publish did not produce Jellyfin.Plugin.Harmonie.dll" >&2
    exit 1
fi

if pgrep -f "$JELLYFIN_APP/Contents/MacOS/(Jellyfin Server|jellyfin)" >/dev/null 2>&1; then
    server_was_running=true
    echo "==> Stopping Jellyfin Server"
    osascript -e "tell application id \"$APP_ID\" to quit" >/dev/null 2>&1 || true

    for _ in {1..50}; do
        if ! pgrep -f "$JELLYFIN_APP/Contents/MacOS/(Jellyfin Server|jellyfin)" >/dev/null 2>&1; then
            break
        fi
        sleep 0.2
    done

    if pgrep -f "$JELLYFIN_APP/Contents/MacOS/(Jellyfin Server|jellyfin)" >/dev/null 2>&1; then
        echo "error: Jellyfin Server did not stop; installation was not changed" >&2
        exit 1
    fi

    restart_needed=true
fi

echo "==> Installing into $plugin_dir"
mkdir -p "$stage_dir"
cp "$publish_dir/Jellyfin.Plugin.Harmonie.dll" "$stage_dir/"
find "$publish_dir" -maxdepth 1 -type f \
    \( -name 'Jellyfin.Plugin.Harmonie.deps.json' \
       -o -name 'Jellyfin.Plugin.Harmonie.pdb' \
       -o -name 'Jellyfin.Plugin.Harmonie.xml' \) \
    -exec cp {} "$stage_dir/" \;
cp "$ROOT/Jellyfin.Plugin.Harmonie/banner.png" "$stage_dir/"

cat > "$stage_dir/meta.json" <<EOF
{
  "category": "Music",
  "changelog": "Local development build",
  "description": "Generate Jellyfin playlists from harmonie audio similarity.",
  "guid": "485e9b6f-f623-4c97-9679-ad33c1db0d18",
  "name": "Harmonie",
  "overview": "Build playlists from harmonie audio similarity.",
  "owner": "mxschll",
  "targetAbi": "$target_abi",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)",
  "version": "$plugin_version",
  "status": "Active",
  "autoUpdate": false,
  "imagePath": "banner.png",
  "assemblies": ["Jellyfin.Plugin.Harmonie.dll"]
}
EOF

# Jellyfin may otherwise discover an older copy of the same plugin first.
find "$PLUGINS_DIR" -maxdepth 1 -type d -name 'Jellyfin.Plugin.Harmonie_*' \
    -exec rm -rf {} +
mv "$stage_dir" "$plugin_dir"

if [[ "$server_was_running" == true ]]; then
    echo "==> Starting Jellyfin Server"
    open "$JELLYFIN_APP"
    restart_needed=false
else
    echo "==> Jellyfin Server was not running; leaving it stopped"
fi

echo
echo "Installed Harmonie $plugin_version for Jellyfin $server_version."
echo "Logs: $JELLYFIN_DATA_DIR/log/"
