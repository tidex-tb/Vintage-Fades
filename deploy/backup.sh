#!/usr/bin/env sh
# Nightly PostgreSQL backup for the docker-compose stack.
# Cron example (02:30 every night):  30 2 * * * /opt/vintagefades/deploy/backup.sh >> /var/log/vintagefades-backup.log 2>&1
# Copy the backups directory off the server (e.g. rclone to object storage); a backup on the same disk is not a backup.
set -eu

cd "$(dirname "$0")"
BACKUP_DIR="${BACKUP_DIR:-./backups}"
KEEP_DAYS="${KEEP_DAYS:-14}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$BACKUP_DIR"

docker compose exec -T db pg_dump -U vintagefades -d vintagefades --format=custom --no-owner \
  > "$BACKUP_DIR/vintagefades-$STAMP.dump"

# Fail loudly if the dump is empty or unreadable.
test -s "$BACKUP_DIR/vintagefades-$STAMP.dump"
docker compose exec -T db pg_restore --list < "$BACKUP_DIR/vintagefades-$STAMP.dump" > /dev/null

find "$BACKUP_DIR" -name 'vintagefades-*.dump' -mtime +"$KEEP_DAYS" -delete
echo "Backup written: $BACKUP_DIR/vintagefades-$STAMP.dump"
