#!/usr/bin/env bash
# Daily PostgreSQL backup of the GCS database (Phase 11). Installed as /usr/local/sbin/gcs-backup, run by cron.
#
#   sudo gcs-backup            # one dump now, then delete dumps older than BACKUP_RETENTION_DAYS
#
# pg_dump in custom format (-Fc): compressed, consistent (one transaction snapshot while the API keeps writing) and
# restorable table by table with pg_restore. Each dump is checked by reading its table of contents back.
set -Eeuo pipefail

ENV_FILE=${GCS_ENV_FILE:-/etc/gcs/gcs.env}
# shellcheck source=/dev/null
. "$ENV_FILE"
BACKUP_DIR=${BACKUP_DIR:-/var/backups/gcs}
RETENTION_DAYS=${BACKUP_RETENTION_DAYS:-14}
COMPOSE=${GCS_COMPOSE:-gcs-compose}

umask 077 # dumps contain password hashes and the whole flight history: root only
mkdir -p "$BACKUP_DIR"

stamp=$(date -u +%Y%m%dT%H%M%SZ)
file=$BACKUP_DIR/gcs-$stamp.dump
partial=$file.partial
trap 'rm -f "$partial"' EXIT

# Written under a temporary name and renamed only when complete, so a half-written file never looks like a backup.
$COMPOSE exec -T postgres pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --format=custom --compress=6 > "$partial"
$COMPOSE exec -T postgres pg_restore --list < "$partial" > /dev/null
mv "$partial" "$file"
echo "backup written: $file ($(du -h "$file" | cut -f1))"

# Retention: delete dumps older than RETENTION_DAYS, but never the newest one. If backups stop for a while (a full
# disk, a stopped stack), the last good dump must not age out under us.
newest=$(ls -1t "$BACKUP_DIR"/gcs-*.dump | head -n 1)
find "$BACKUP_DIR" -maxdepth 1 -name 'gcs-*.dump' -type f -mtime +"$((RETENTION_DAYS - 1))" ! -path "$newest" -print -delete \
    | sed 's/^/deleted (older than '"$RETENTION_DAYS"' days): /'
