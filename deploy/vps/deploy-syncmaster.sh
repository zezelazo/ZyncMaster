#!/usr/bin/env bash
# =============================================================================
# deploy-syncmaster.sh — privileged on-VPS deploy step for SyncMaster (Zync)
# =============================================================================
# Installed once at /var/www/devlabperu.com/api/.scripts/deploy-syncmaster.sh and
# invoked by the GitHub Actions CD via a single scoped sudoers entry:
#   devlab ALL=(root) NOPASSWD: /var/www/devlabperu.com/api/.scripts/deploy-syncmaster.sh
#
# The CD (deploy-syncmaster-vps.yml) first rsyncs the published binaries and the
# efbundle into ~devlab/syncmaster-staging, then runs this script as root.
# It: swaps in the new binaries, applies migrations as the postgres superuser
# (the app role is CRUD-only, no DDL), and restarts the service. Idempotent.
# =============================================================================
set -euo pipefail

readonly STAGING="/home/devlab/syncmaster-staging"
readonly SERVICE_DIR="/var/www/devlabperu.com/api/syncmaster"
readonly SERVICE="syncmaster"
readonly DB="bd_syncmaster"

if [[ $EUID -ne 0 ]]; then
    echo "ERROR: must run as root (via sudo)." >&2
    exit 1
fi
if [[ ! -d "$STAGING/publish" || ! -f "$STAGING/efbundle" ]]; then
    echo "ERROR: staging incomplete ($STAGING/publish + efbundle expected)." >&2
    exit 1
fi

echo "[1/4] Stopping $SERVICE..."
systemctl stop "$SERVICE" || true

echo "[2/4] Syncing binaries into $SERVICE_DIR..."
mkdir -p "$SERVICE_DIR"
rsync -a --delete "$STAGING/publish/" "$SERVICE_DIR/"
install -m 0755 "$STAGING/efbundle" "$SERVICE_DIR/efbundle"
chown -R www-data:www-data "$SERVICE_DIR"

echo "[3/4] Applying migrations as postgres (peer auth via socket)..."
# Unix-socket connection => peer auth as the postgres OS user, no password stored anywhere.
sudo -u postgres "$SERVICE_DIR/efbundle" \
    --connection "Host=/var/run/postgresql;Database=${DB};Username=postgres"

# Idempotently (re)grant CRUD to the app role. Runs every deploy so a migration that adds a
# postgres-owned table is immediately usable by syncmaster_app, and so the very first deploy
# grants before the service starts. ALTER DEFAULT PRIVILEGES covers future tables; the explicit
# GRANT ON ALL covers the ones efbundle just created. The role stays CRUD-only (no CREATE).
sudo -u postgres psql -d "$DB" -v ON_ERROR_STOP=1 <<'SQL'
GRANT USAGE ON SCHEMA public TO syncmaster_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES    IN SCHEMA public TO syncmaster_app;
GRANT USAGE, SELECT                  ON ALL SEQUENCES IN SCHEMA public TO syncmaster_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES    TO syncmaster_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT                  ON SEQUENCES TO syncmaster_app;
REVOKE CREATE ON SCHEMA public FROM syncmaster_app;
SQL

echo "[4/4] Restarting $SERVICE..."
systemctl restart "$SERVICE"
systemctl --no-pager --lines=0 status "$SERVICE" || true

echo "Done."
