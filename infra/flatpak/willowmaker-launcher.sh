#!/bin/bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

# Note: This flatpak was inspired by manu311
set -e

echo "[Kongor:Flatpak] Starting Project KONGOR WILLOWMAKER Launcher..."

# Determine internal userdata directory (self-contained, zero host pollution)
APP_DATA_DIR="${XDG_DATA_HOME:-$HOME/.var/app/net.kongor.Willowmaker/data}"
USERDATA_DIR="$APP_DATA_DIR/userdata"
mkdir -p "$USERDATA_DIR/game"
mkdir -p "$USERDATA_DIR/override"
mkdir -p "$USERDATA_DIR/.Heroes of Newerth/game"
mkdir -p "$USERDATA_DIR/.Heroes of Newerth/override"

# Ensure XWayland / Mesa modesetting crash avoidance in internal userdata (both game/ and override/ active mod tiers)
for SUBMOD in "game" "override"; do
    STARTUP_CFG="$USERDATA_DIR/.Heroes of Newerth/$SUBMOD/startup.cfg"
    if [ ! -f "$STARTUP_CFG" ]; then
        echo "[Kongor:Flatpak] Creating internal $SUBMOD/startup.cfg with gl_modesetting \"\""
        echo 'SetSave "gl_modesetting" ""' > "$STARTUP_CFG"
    elif ! grep -q 'SetSave "gl_modesetting" ""' "$STARTUP_CFG" 2>/dev/null; then
        echo "[Kongor:Flatpak] Appending gl_modesetting \"\" to internal $SUBMOD/startup.cfg"
        echo 'SetSave "gl_modesetting" ""' >> "$STARTUP_CFG"
    fi
done

# Export runtime environment - isolate HOME to internal userdata so K2 saves internally
export HOME="$USERDATA_DIR"
export LD_LIBRARY_PATH="/app/lib:${LD_LIBRARY_PATH}"
export XDG_DATA_HOME="$APP_DATA_DIR"

# If bundled WILLOWMAKER binary is installed in /app/bin/WILLOWMAKER, run it directly
if [ -x "/app/bin/WILLOWMAKER" ]; then
    exec /app/bin/WILLOWMAKER "$@"
elif [ -f "$XDG_DATA_HOME/WILLOWMAKER" ]; then
    chmod +x "$XDG_DATA_HOME/WILLOWMAKER"
    exec "$XDG_DATA_HOME/WILLOWMAKER" "$@"
else
    echo "[Kongor:Flatpak] Error: WILLOWMAKER executable not found in /app/bin or $XDG_DATA_HOME."
    exit 1
fi
