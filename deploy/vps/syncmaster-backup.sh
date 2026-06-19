#!/usr/bin/env bash
# =============================================================================
# syncmaster-backup.sh — nightly backup of bd_syncmaster (the recovery safety net)
# =============================================================================
# WHY THIS IS CRITICAL: bd_syncmaster holds BOTH the DataProtection key ring AND the
# Graph refresh tokens that key ring encrypts. A lost disk / bad Postgres migration is
# therefore a TOTAL product reset with no partial recovery — every device, pair, token
# and clipboard item, gone. This script is the off-box safety net.
#
# INSTALL (one-time, as root on the VPS):
#   sudo install -m 0755 syncmaster-backup.sh /var/www/devlabperu.com/api/.scripts/syncmaster-backup.sh
#   sudo install -m 0644 syncmaster-backup.cron /etc/cron.d/syncmaster-backup
#   # configure off-box + encryption in /etc/default/syncmaster-backup (see below), then test:
#   sudo /var/www/devlabperu.com/api/.scripts/syncmaster-backup.sh
#
# CONFIG (/etc/default/syncmaster-backup, sourced below; all optional but STRONGLY recommended):
#   SYNCMASTER_BACKUP_DIR=/var/backups/syncmaster          # local staging dir
#   SYNCMASTER_BACKUP_KEEP_DAYS=14                          # local retention
#   SYNCMASTER_BACKUP_AGE_RECIPIENT=age1xxxx...             # encrypt at rest (install `age`)
#   SYNCMASTER_BACKUP_RCLONE_REMOTE=b2:my-bucket/syncmaster # off-box copy (install `rclone`)
# =============================================================================
set -euo pipefail

# Optional operator config (off-box target, encryption recipient, retention).
[[ -f /etc/default/syncmaster-backup ]] && . /etc/default/syncmaster-backup

readonly DB="bd_syncmaster"
readonly DEST="${SYNCMASTER_BACKUP_DIR:-/var/backups/syncmaster}"
readonly KEEP_DAYS="${SYNCMASTER_BACKUP_KEEP_DAYS:-14}"
readonly DATE="$(date +%Y%m%d-%H%M%S)"

mkdir -p "$DEST"

# pg_dump via PEER auth on the unix socket as the postgres OS user — no password stored anywhere
# (the same auth the deploy migrations use). -Fc = compressed custom format, selectively restorable.
DUMP="$DEST/${DB}-${DATE}.dump"
sudo -u postgres pg_dump -Fc "$DB" > "$DUMP"

# Encrypt at rest when an `age` recipient is configured. The dump is a self-decrypting kit of every
# refresh token + the key ring, so it MUST NOT sit in plaintext on a shared box. Without age it falls
# back to the plain custom-format dump with a loud warning.
FINAL="$DUMP"
if command -v age >/dev/null 2>&1 && [[ -n "${SYNCMASTER_BACKUP_AGE_RECIPIENT:-}" ]]; then
    age -r "$SYNCMASTER_BACKUP_AGE_RECIPIENT" -o "${DUMP}.age" "$DUMP"
    rm -f "$DUMP"
    FINAL="${DUMP}.age"
else
    echo "WARN: backup NOT encrypted — set SYNCMASTER_BACKUP_AGE_RECIPIENT and install 'age'." >&2
fi
chmod 600 "$FINAL"

# Off-box copy. A disk failure that takes the DB also takes a same-disk backup, so THIS is the part
# that actually protects you. Configure SYNCMASTER_BACKUP_RCLONE_REMOTE + install rclone.
if [[ -n "${SYNCMASTER_BACKUP_RCLONE_REMOTE:-}" ]] && command -v rclone >/dev/null 2>&1; then
    rclone copy "$FINAL" "$SYNCMASTER_BACKUP_RCLONE_REMOTE" || echo "WARN: off-box copy failed." >&2
else
    echo "WARN: backup is ON-BOX ONLY — set SYNCMASTER_BACKUP_RCLONE_REMOTE for real protection." >&2
fi

# Local retention: prune dumps older than KEEP_DAYS.
find "$DEST" -maxdepth 1 -name "${DB}-*.dump*" -mtime "+${KEEP_DAYS}" -delete 2>/dev/null || true

echo "Backup OK: $FINAL ($(du -h "$FINAL" | cut -f1))"
echo "Restore test: sudo -u postgres pg_restore -l <decrypted-dump> | head   # verify it lists objects"
