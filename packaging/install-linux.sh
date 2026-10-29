#!/usr/bin/env bash
set -euo pipefail

# FolderSyncEr Ubuntu/Linux Installer
# Can be run as root (for system-wide install) or standard user (for user-level install).

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ "$EUID" -eq 0 ]; then
    INSTALL_BIN_DIR="/usr/local/bin"
    DESKTOP_DIR="/usr/local/share/applications"
    ICON_DIR="/usr/local/share/icons/hicolor/256x256/apps"
else
    INSTALL_BIN_DIR="$HOME/.local/bin"
    DESKTOP_DIR="$HOME/.local/share/applications"
    ICON_DIR="$HOME/.local/share/icons/hicolor/256x256/apps"
fi

echo "==> Installing FolderSyncEr..."
mkdir -p "$INSTALL_BIN_DIR" "$DESKTOP_DIR" "$ICON_DIR"

if [ -f "$SCRIPT_DIR/foldersyncer-client" ]; then
    cp "$SCRIPT_DIR/foldersyncer-client" "$INSTALL_BIN_DIR/foldersyncer-client"
    chmod +x "$INSTALL_BIN_DIR/foldersyncer-client"
    echo "  Installed client binary to $INSTALL_BIN_DIR/foldersyncer-client"
fi

if [ -f "$SCRIPT_DIR/foldersyncer-server" ]; then
    cp "$SCRIPT_DIR/foldersyncer-server" "$INSTALL_BIN_DIR/foldersyncer-server"
    chmod +x "$INSTALL_BIN_DIR/foldersyncer-server"
    echo "  Installed server binary to $INSTALL_BIN_DIR/foldersyncer-server"
fi

if [ -f "$SCRIPT_DIR/icon.png" ]; then
    cp "$SCRIPT_DIR/icon.png" "$ICON_DIR/foldersyncer.png"
    echo "  Installed desktop icon to $ICON_DIR/foldersyncer.png"
fi

if [ -f "$SCRIPT_DIR/foldersyncer.desktop" ]; then
    cp "$SCRIPT_DIR/foldersyncer.desktop" "$DESKTOP_DIR/foldersyncer.desktop"
    echo "  Installed desktop entry to $DESKTOP_DIR/foldersyncer.desktop"
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database "$DESKTOP_DIR" || true
    fi
fi

echo "==> Installation complete!"
echo "You can launch the client from your application menu or run 'foldersyncer-client'."

