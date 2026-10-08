#!/usr/bin/env bash
# Restores the GCS database from a gcs-backup dump (Phase 11). Installed as /usr/local/sbin/gcs-restore.
#
#   sudo gcs-restore /var/backups/gcs/gcs-20261009T030000Z.dump
#
# Replaces the current database contents. The API is stopped meanwhile, so nothing writes half-way through; it is
# started again afterwards (and the migrator brings the schema forward if the dump is from an older version).
set -Eeuo pipefail

file=${1:-}
[[ -f $file ]] || { echo "usage: $0 BACKUP_FILE (see /var/backups/gcs)" >&2; exit 2; }

ENV_FILE=${GCS_ENV_FILE:-/etc/gcs/gcs.env}
# shellcheck source=/dev/null
. "$ENV_FILE"
COMPOSE=${GCS_COMPOSE:-gcs-compose}

if [[ ${GCS_RESTORE_CONFIRM:-} != yes ]]; then
    read -r -p "Replace the database '$POSTGRES_DB' with $file? Type yes: " answer
    [[ $answer == yes ]] || { echo "aborted"; exit 1; }
fi

$COMPOSE exec -T postgres pg_restore --list < "$file" > /dev/null # a damaged file stops here, before anything changes
$COMPOSE stop gcs-api
trap '$COMPOSE up -d --wait gcs-api' EXIT
# --clean --if-exists: drop each object before recreating it. One transaction: all or nothing.
$COMPOSE exec -T postgres pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists --no-owner --single-transaction < "$file"
echo "restored $file into $POSTGRES_DB"
