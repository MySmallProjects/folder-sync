#!/usr/bin/env bash
# ==============================================================================
# reset-server.sh — Reset FolderSyncEr Server to factory defaults
# Clears the SQLite database (foldersyncer.db*), blob storage (storage/),
# and server logs (logs/). On next startup, the server will automatically
# recreate the schema and seed the default admin account.
#
# Usage:
#   ./reset-server.sh                  # Reset server DB, blob storage, and logs
#   ./reset-server.sh --reset-client   # Also clear client .foldersyncer tracking DB
#   ./reset-server.sh --wipe-client    # Also delete all files in client sync folder
# ==============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVER_DIR="${SCRIPT_DIR}/src/FolderSyncEr.Server"

RESET_CLIENT=false
WIPE_CLIENT=false

for arg in "$@"; do
    case "$arg" in
        --reset-client)
            RESET_CLIENT=true
            ;;
        --wipe-client)
            RESET_CLIENT=true
            WIPE_CLIENT=true
            ;;
        -h|--help)
            echo "Usage: ./reset-server.sh [--reset-client] [--wipe-client]"
            echo ""
            echo "Options:"
            echo "  (no args)        Clear server SQLite DB, blob storage directory, and server logs."
            echo "  --reset-client   Also remove the client's local '.foldersyncer' sync state folder"
            echo "                   (reads SyncFolderPath from ~/.config/foldersyncer/settings.json)."
            echo "  --wipe-client    Also delete all synced files inside the client's SyncFolderPath."
            exit 0
            ;;
        *)
            echo "Unknown option: $arg (use --help for usage)"
            exit 1
            ;;
    esac
done

echo "========================================================"
echo " FolderSyncEr — Server Reset Utility"
echo "========================================================"

# Warn if the server process appears to be running
if pgrep -f "FolderSyncEr.Server" >/dev/null 2>&1; then
    echo "⚠️  WARNING: FolderSyncEr.Server appears to be running!"
    echo "   For a clean reset, stop the server first (or restart it after running this script)"
    echo "   so it recreates the database and seeds the default admin user."
    echo ""
fi

# 1. Remove SQLite database files (main db, -wal, -shm)
echo "[1/3] Clearing server SQLite database..."
DB_FILES=(
    "${SERVER_DIR}/foldersyncer.db"
    "${SERVER_DIR}/foldersyncer.db-wal"
    "${SERVER_DIR}/foldersyncer.db-shm"
    "${SCRIPT_DIR}/foldersyncer.db"
    "${SCRIPT_DIR}/foldersyncer.db-wal"
    "${SCRIPT_DIR}/foldersyncer.db-shm"
)

removed_db=0
for db_file in "${DB_FILES[@]}"; do
    if [ -e "$db_file" ]; then
        rm -f "$db_file"
        echo "  ✓ Removed: $db_file"
        removed_db=$((removed_db + 1))
    fi
done

if [ "$removed_db" -eq 0 ]; then
    echo "  • No existing database files found (already clean)."
fi

# 2. Clear server blob storage directory
echo "[2/3] Clearing server blob storage..."
STORAGE_DIRS=(
    "${SERVER_DIR}/storage"
    "${SCRIPT_DIR}/storage"
)

removed_storage=0
for storage_dir in "${STORAGE_DIRS[@]}"; do
    if [ -d "$storage_dir" ]; then
        rm -rf "$storage_dir"
        echo "  ✓ Removed directory: $storage_dir"
        removed_storage=$((removed_storage + 1))
    fi
done

# Recreate empty storage directory in server project
mkdir -p "${SERVER_DIR}/storage"
echo "  ✓ Recreated empty storage directory: ${SERVER_DIR}/storage"

# 3. Clear server logs
echo "[3/3] Clearing server logs..."
LOG_DIRS=(
    "${SERVER_DIR}/logs"
    "${SCRIPT_DIR}/logs"
)

for log_dir in "${LOG_DIRS[@]}"; do
    if [ -d "$log_dir" ]; then
        rm -rf "$log_dir"
        echo "  ✓ Removed log directory: $log_dir"
    fi
done

# Optional: Reset client sync state (.foldersyncer) if requested
if [ "$RESET_CLIENT" = true ]; then
    echo ""
    echo "[Client] Checking client settings for SyncFolderPath..."
    SETTINGS_FILE="${HOME}/.config/foldersyncer/settings.json"
    if [ -f "$SETTINGS_FILE" ]; then
        SYNC_FOLDER=$(python3 -c "import json, sys; data = json.load(open('$SETTINGS_FILE')); print(data.get('SyncFolderPath') or '')" 2>/dev/null || true)
        if [ -n "$SYNC_FOLDER" ] && [ -d "$SYNC_FOLDER" ]; then
            if [ "$WIPE_CLIENT" = true ]; then
                echo "  ✓ Wiping all contents of client sync folder: $SYNC_FOLDER"
                rm -rf "${SYNC_FOLDER:?}"/* "${SYNC_FOLDER:?}"/.[!.]* "${SYNC_FOLDER:?}"/..?* 2>/dev/null || true
            elif [ -d "${SYNC_FOLDER}/.foldersyncer" ]; then
                rm -rf "${SYNC_FOLDER}/.foldersyncer"
                echo "  ✓ Removed client sync tracking DB: ${SYNC_FOLDER}/.foldersyncer"
            else
                echo "  • No .foldersyncer directory found in $SYNC_FOLDER"
            fi
        else
            echo "  • No valid SyncFolderPath found in $SETTINGS_FILE"
        fi
    else
        echo "  • Client settings file not found at $SETTINGS_FILE"
    fi
fi

echo ""
echo "✅ Server reset complete!"
echo "   When you next start the server ('dotnet run --project src/FolderSyncEr.Server'),"
echo "   it will create a fresh database and seed the default admin account."

