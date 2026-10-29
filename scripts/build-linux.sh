#!/usr/bin/env bash
set -euo pipefail

# Build and package FolderSyncEr for Linux / Ubuntu (x64)
# Usage: ./scripts/build-linux.sh [--self-contained]

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIST_DIR="$ROOT_DIR/dist/linux-x64"
STAGE_DIR="$DIST_DIR/package"
SELF_CONTAINED="false"
RID_ARG=""
NO_RESTORE_ARG=""

for arg in "$@"; do
    case "$arg" in
        --self-contained)
            SELF_CONTAINED="true"
            ;;
        --rid=*)
            RID_ARG="-r ${arg#*=}"
            ;;
        --no-restore)
            NO_RESTORE_ARG="--no-restore"
            ;;
    esac
done

echo "==> Building FolderSyncEr for Linux [Self-Contained: $SELF_CONTAINED, RID: ${RID_ARG:-default}]..."
rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR" "$STAGE_DIR"

# 1. Publish Server
echo "==> Publishing FolderSyncEr.Server..."
dotnet publish "$ROOT_DIR/src/FolderSyncEr.Server/FolderSyncEr.Server.csproj" \
    -c Release \
    $RID_ARG \
    --self-contained "$SELF_CONTAINED" \
    $NO_RESTORE_ARG \
    -o "$DIST_DIR/server-publish"

# 2. Publish Client App
echo "==> Publishing FolderSyncEr.Client.App..."
dotnet publish "$ROOT_DIR/src/FolderSyncEr.Client.App/FolderSyncEr.Client.App.csproj" \
    -c Release \
    $RID_ARG \
    --self-contained "$SELF_CONTAINED" \
    $NO_RESTORE_ARG \
    -o "$DIST_DIR/client-publish"

# 3. Assemble Package
echo "==> Assembling release package..."
# Detect binary names (either FolderSyncEr.Server or FolderSyncEr.Server.dll depending on RID)
if [ -f "$DIST_DIR/server-publish/FolderSyncEr.Server" ]; then
    cp "$DIST_DIR/server-publish/FolderSyncEr.Server" "$STAGE_DIR/foldersyncer-server"
else
    # Create self-executing shell wrapper for framework-dependent DLL
    cat << 'EOF' > "$STAGE_DIR/foldersyncer-server"
#!/usr/bin/env bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec dotnet "$SCRIPT_DIR/server/FolderSyncEr.Server.dll" "$@"
EOF
    mkdir -p "$STAGE_DIR/server"
    cp -r "$DIST_DIR/server-publish/"* "$STAGE_DIR/server/"
fi
chmod +x "$STAGE_DIR/foldersyncer-server"

if [ -f "$DIST_DIR/client-publish/FolderSyncEr.Client.App" ]; then
    cp "$DIST_DIR/client-publish/FolderSyncEr.Client.App" "$STAGE_DIR/foldersyncer-client"
else
    # Create self-executing shell wrapper for framework-dependent DLL
    cat << 'EOF' > "$STAGE_DIR/foldersyncer-client"
#!/usr/bin/env bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec dotnet "$SCRIPT_DIR/client/FolderSyncEr.Client.App.dll" "$@"
EOF
    mkdir -p "$STAGE_DIR/client"
    cp -r "$DIST_DIR/client-publish/"* "$STAGE_DIR/client/"
fi
chmod +x "$STAGE_DIR/foldersyncer-client"

if [ -f "$DIST_DIR/server-publish/appsettings.json" ]; then
    cp "$DIST_DIR/server-publish/appsettings.json" "$STAGE_DIR/appsettings.json"
fi

cp "$ROOT_DIR/packaging/desktop/foldersyncer.desktop" "$STAGE_DIR/foldersyncer.desktop"
cp "$ROOT_DIR/packaging/install-linux.sh" "$STAGE_DIR/install.sh"
chmod +x "$STAGE_DIR/install.sh"
cp "$ROOT_DIR/src/FolderSyncEr.Client.App/Assets/icon.png" "$STAGE_DIR/icon.png"
cp "$ROOT_DIR/packaging/systemd/foldersyncer-server.service" "$STAGE_DIR/foldersyncer-server.service"

# 4. Create Tarball
TARBALL="$ROOT_DIR/dist/FolderSyncEr-linux-x64.tar.gz"
echo "==> Creating tarball: $TARBALL"
tar -czf "$TARBALL" -C "$STAGE_DIR" .

echo "==> Linux package successfully built at:"
echo "    Archive: $TARBALL"
echo "    Staging: $STAGE_DIR"
