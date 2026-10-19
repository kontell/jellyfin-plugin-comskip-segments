#!/bin/bash
# Installs the built plugin into Jellyfin and restarts the server (stop, copy, start). Run with sudo.
set -euxo pipefail
REPO="$(dirname "$(readlink -f "$0")")"
SRC="$REPO/bin/Release/net10.0"
DEST=/var/lib/jellyfin/plugins/ComskipSegments
VERSION=$(sed -n 's/.*"version": "\([0-9.]*\)".*/\1/p' "$REPO/meta.json")
# Stop first: overwriting the DLL under a running server makes it fail on shutdown
# ("Bad IL range") because the runtime still reads code from the replaced file.
systemctl stop jellyfin
# Start again even if a copy step fails, so a failed install never leaves Jellyfin down.
trap 'systemctl start jellyfin' EXIT
mkdir -p "$DEST"
cp "$SRC/Jellyfin.Plugin.ComskipSegments.dll" "$SRC/icon.png" "$DEST/"
# The folder also holds the plugin's state files; an installed meta.json (from the plugin
# catalog) has more in it than the repo's, so only its version changes.
if [ -f "$DEST/meta.json" ]; then
  sed -i "s/\"version\": \"[0-9.]*\"/\"version\": \"$VERSION\"/" "$DEST/meta.json"
else
  cp "$SRC/meta.json" "$DEST/"
fi
chown -R jellyfin:jellyfin "$DEST"
ls -la "$DEST"
