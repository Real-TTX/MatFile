#!/bin/sh
set -eu

DATA_DIR="${MATFILE_DATA:-/data}"

mkdir -p "$DATA_DIR/config" "$DATA_DIR/keys" "$DATA_DIR/thumbs" "$DATA_DIR/temp"

# Optional als unprivilegierter Benutzer laufen (PUID/PGID wie bei üblichen NAS-Images).
# Ohne PUID läuft MatFile als root - nötig, wenn SMB/NFS im Container gemountet werden soll.
if [ -n "${PUID:-}" ] && [ "$(id -u)" = "0" ]; then
    PGID="${PGID:-$PUID}"
    chown -R "$PUID:$PGID" "$DATA_DIR" 2>/dev/null || true
    echo "MatFile: starte als UID $PUID / GID $PGID"
    exec setpriv --reuid "$PUID" --regid "$PGID" --clear-groups "$@"
fi

exec "$@"
