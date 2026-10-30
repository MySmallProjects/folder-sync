#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# FolderSyncEr - Automated Binary Release Builder
#
# Generates 4 standalone self-contained release archives and 1 release notes file:
#   1. Releases/FolderSyncEr-Client-linux-<version>.tar.gz
#   2. Releases/FolderSyncEr-Server-linux-<version>.tar.gz
#   3. Releases/FolderSyncEr-Client-windows-<version>.zip
#   4. Releases/FolderSyncEr-Server-windows-<version>.zip
#   5. Releases/RELEASE_NOTES.txt
#
# All intermediate build and staging files are cleaned up upon completion.
# ==============================================================================

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RELEASES_DIR="$ROOT_DIR/Releases"
TEMPLATE_FILE="$ROOT_DIR/packaging/templates/release-notes-template.txt"
TMP_STAGE="$(mktemp -d "${TMPDIR:-/tmp}/foldersyncer-release-XXXXXX")"

# Guarantee cleanup of all temporary build artifacts on exit
cleanup() {
    local exit_code=$?
    if [ -d "$TMP_STAGE" ]; then
        rm -rf "$TMP_STAGE"
    fi
    exit "$exit_code"
}
trap cleanup EXIT INT TERM

echo "================================================================================"
echo " FolderSyncEr Release Packaging Script"
echo " Working directory: $ROOT_DIR"
echo " Target directory:  $RELEASES_DIR"
echo " Temp staging:     $TMP_STAGE"
echo "================================================================================"

mkdir -p "$RELEASES_DIR"
rm -f "$RELEASES_DIR"/*

# Helper functions to extract project version components
get_server_version() {
    local major minor build
    major=$(grep '<VersionMajor>' "$ROOT_DIR/src/FolderSyncEr.Server/FolderSyncEr.Server.csproj" | sed -E 's/.*<VersionMajor>([0-9]+)<\/VersionMajor>.*/\1/')
    minor=$(grep '<VersionMinor>' "$ROOT_DIR/src/FolderSyncEr.Server/FolderSyncEr.Server.csproj" | sed -E 's/.*<VersionMinor>([0-9]+)<\/VersionMinor>.*/\1/')
    build=$(cat "$ROOT_DIR/build.version" | tr -d '[:space:]')
    echo "${major:-1}.${minor:-0}.${build:-0}"
}

get_client_version() {
    local major minor build
    major=$(grep '<VersionMajor>' "$ROOT_DIR/src/FolderSyncEr.Client.App/FolderSyncEr.Client.App.csproj" | sed -E 's/.*<VersionMajor>([0-9]+)<\/VersionMajor>.*/\1/')
    minor=$(grep '<VersionMinor>' "$ROOT_DIR/src/FolderSyncEr.Client.App/FolderSyncEr.Client.App.csproj" | sed -E 's/.*<VersionMinor>([0-9]+)<\/VersionMinor>.*/\1/')
    build=$(cat "$ROOT_DIR/build.client.version" | tr -d '[:space:]')
    echo "${major:-1}.${minor:-0}.${build:-0}"
}

# ------------------------------------------------------------------------------
# 1. Build Linux Packages (x64)
# ------------------------------------------------------------------------------
echo ""
echo "==> [1/4] Publishing Linux Desktop Client (linux-x64)..."
dotnet publish "$ROOT_DIR/src/FolderSyncEr.Client.App/FolderSyncEr.Client.App.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    --no-restore \
    -o "$TMP_STAGE/client-linux-raw" > /dev/null

CLIENT_VERSION="$(get_client_version)"
CLIENT_LINUX_PKG="$TMP_STAGE/client-linux-pkg"
mkdir -p "$CLIENT_LINUX_PKG"
cp "$TMP_STAGE/client-linux-raw/FolderSyncEr.Client.App" "$CLIENT_LINUX_PKG/foldersyncer-client"
chmod +x "$CLIENT_LINUX_PKG/foldersyncer-client"
cp "$ROOT_DIR/packaging/desktop/foldersyncer.desktop" "$CLIENT_LINUX_PKG/foldersyncer.desktop"
cp "$ROOT_DIR/packaging/install-linux.sh" "$CLIENT_LINUX_PKG/install.sh"
chmod +x "$CLIENT_LINUX_PKG/install.sh"
cp "$ROOT_DIR/src/FolderSyncEr.Client.App/Assets/icon.png" "$CLIENT_LINUX_PKG/icon.png"

CLIENT_LINUX_ARCHIVE="$RELEASES_DIR/FolderSyncEr-Client-linux-${CLIENT_VERSION}.tar.gz"
echo "  -> Packaging: $(basename "$CLIENT_LINUX_ARCHIVE")"
tar -czf "$CLIENT_LINUX_ARCHIVE" -C "$CLIENT_LINUX_PKG" .

echo ""
echo "==> [2/4] Publishing Linux Web API Server (linux-x64)..."
dotnet publish "$ROOT_DIR/src/FolderSyncEr.Server/FolderSyncEr.Server.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    --no-restore \
    -o "$TMP_STAGE/server-linux-raw" > /dev/null

SERVER_VERSION="$(get_server_version)"
SERVER_LINUX_PKG="$TMP_STAGE/server-linux-pkg"
mkdir -p "$SERVER_LINUX_PKG"
cp "$TMP_STAGE/server-linux-raw/FolderSyncEr.Server" "$SERVER_LINUX_PKG/foldersyncer-server"
chmod +x "$SERVER_LINUX_PKG/foldersyncer-server"
if [ -f "$TMP_STAGE/server-linux-raw/appsettings.json" ]; then
    cp "$TMP_STAGE/server-linux-raw/appsettings.json" "$SERVER_LINUX_PKG/appsettings.json"
fi
if [ -d "$TMP_STAGE/server-linux-raw/wwwroot" ]; then
    cp -r "$TMP_STAGE/server-linux-raw/wwwroot" "$SERVER_LINUX_PKG/wwwroot"
fi
cp "$ROOT_DIR/packaging/systemd/foldersyncer-server.service" "$SERVER_LINUX_PKG/foldersyncer-server.service"

SERVER_LINUX_ARCHIVE="$RELEASES_DIR/FolderSyncEr-Server-linux-${SERVER_VERSION}.tar.gz"
echo "  -> Packaging: $(basename "$SERVER_LINUX_ARCHIVE")"
tar -czf "$SERVER_LINUX_ARCHIVE" -C "$SERVER_LINUX_PKG" .

# ------------------------------------------------------------------------------
# 2. Build Windows Packages (win-x64)
# ------------------------------------------------------------------------------
echo ""
echo "==> [3/4] Publishing Windows Desktop Client (win-x64)..."
dotnet publish "$ROOT_DIR/src/FolderSyncEr.Client.App/FolderSyncEr.Client.App.csproj" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:SkipAutoIncrementVersion=true \
    --no-restore \
    -o "$TMP_STAGE/client-win-raw" > /dev/null

CLIENT_WIN_PKG="$TMP_STAGE/client-win-pkg"
mkdir -p "$CLIENT_WIN_PKG"
cp "$TMP_STAGE/client-win-raw/FolderSyncEr.Client.App.exe" "$CLIENT_WIN_PKG/FolderSyncEr.Client.exe"

CLIENT_WIN_ARCHIVE="$RELEASES_DIR/FolderSyncEr-Client-windows-${CLIENT_VERSION}.zip"
echo "  -> Packaging: $(basename "$CLIENT_WIN_ARCHIVE")"
(cd "$CLIENT_WIN_PKG" && zip -9 -q -r "$CLIENT_WIN_ARCHIVE" .)

echo ""
echo "==> [4/4] Publishing Windows Web API Server (win-x64)..."
dotnet publish "$ROOT_DIR/src/FolderSyncEr.Server/FolderSyncEr.Server.csproj" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:SkipAutoIncrementVersion=true \
    --no-restore \
    -o "$TMP_STAGE/server-win-raw" > /dev/null

SERVER_WIN_PKG="$TMP_STAGE/server-win-pkg"
mkdir -p "$SERVER_WIN_PKG"
cp "$TMP_STAGE/server-win-raw/FolderSyncEr.Server.exe" "$SERVER_WIN_PKG/FolderSyncEr.Server.exe"
if [ -f "$TMP_STAGE/server-win-raw/appsettings.json" ]; then
    cp "$TMP_STAGE/server-win-raw/appsettings.json" "$SERVER_WIN_PKG/appsettings.json"
fi
if [ -d "$TMP_STAGE/server-win-raw/wwwroot" ]; then
    cp -r "$TMP_STAGE/server-win-raw/wwwroot" "$SERVER_WIN_PKG/wwwroot"
fi
if [ -f "$TMP_STAGE/server-win-raw/web.config" ]; then
    cp "$TMP_STAGE/server-win-raw/web.config" "$SERVER_WIN_PKG/web.config"
fi

SERVER_WIN_ARCHIVE="$RELEASES_DIR/FolderSyncEr-Server-windows-${SERVER_VERSION}.zip"
echo "  -> Packaging: $(basename "$SERVER_WIN_ARCHIVE")"
(cd "$SERVER_WIN_PKG" && zip -9 -q -r "$SERVER_WIN_ARCHIVE" .)

# ------------------------------------------------------------------------------
# 3. Generate RELEASE_NOTES.txt from Template
# ------------------------------------------------------------------------------
echo ""
echo "==> [5/5] Generating RELEASE_NOTES.txt..."
RELEASE_DATE="$(date -u "+%Y-%m-%d %H:%M:%S UTC")"
RELEASE_NOTES_FILE="$RELEASES_DIR/RELEASE_NOTES.txt"

if [ ! -f "$TEMPLATE_FILE" ]; then
    echo "ERROR: Template file not found: $TEMPLATE_FILE" >&2
    exit 1
fi

sed -e "s/{{CLIENT_VERSION}}/${CLIENT_VERSION}/g" \
    -e "s/{{SERVER_VERSION}}/${SERVER_VERSION}/g" \
    -e "s/{{RELEASE_DATE}}/${RELEASE_DATE}/g" \
    "$TEMPLATE_FILE" > "$RELEASE_NOTES_FILE"

echo "  -> Generated: $(basename "$RELEASE_NOTES_FILE")"

# ------------------------------------------------------------------------------
# Summary Output
# ------------------------------------------------------------------------------
echo ""
echo "================================================================================"
echo " Release Build Complete! Files in $RELEASES_DIR:"
echo "================================================================================"
ls -lh "$RELEASES_DIR"
echo ""
echo "Summary of Generated Release Files:"
for f in "$RELEASES_DIR"/*; do
    if [ -f "$f" ]; then
        sha256=$(sha256sum "$f" | cut -d' ' -f1)
        size=$(du -h "$f" | cut -f1)
        printf "  %-52s  %6s  sha256:%s\n" "$(basename "$f")" "$size" "${sha256:0:16}..."
    fi
done
echo "================================================================================"
