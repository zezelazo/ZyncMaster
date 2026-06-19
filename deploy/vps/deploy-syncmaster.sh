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

# --- Rollback safety -------------------------------------------------------------------------------
# A failed swap / migration / restart used to leave the service DOWN on a half-applied deploy with
# nothing to revert to. Snapshot the live build (cp -al = hardlinks, ~free) before touching it, and
# on ANY error restore it + restart. The snapshot is removed only after the new build proves healthy.
readonly PREV_DIR="${SERVICE_DIR}.prev"
rollback() {
    echo "ERROR: deploy failed — rolling back to the previous build." >&2
    if [[ -d "$PREV_DIR" ]]; then
        rsync -a --delete "$PREV_DIR/" "$SERVICE_DIR/"
        systemctl restart "$SERVICE" || true
        echo "Rolled back to the previous build." >&2
    else
        echo "No previous build to roll back to ($PREV_DIR missing)." >&2
    fi
}
trap rollback ERR

# In-process health gate: the service is "up" only if it answers /health on the loopback backend. A
# restart that returns 0 but then crashes on bad config would otherwise pass and bin the snapshot.
health_check() {
    local code
    for _ in $(seq 1 12); do
        code=$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5007/zync/health || true)
        [[ "$code" == "200" ]] && return 0
        sleep 3
    done
    echo "Health check failed after restart (last code: ${code:-none})." >&2
    return 1
}

if [[ -d "$SERVICE_DIR" ]]; then
    rm -rf "$PREV_DIR"
    cp -al "$SERVICE_DIR" "$PREV_DIR"
fi
# ---------------------------------------------------------------------------------------------------

echo "[1/5] Stopping $SERVICE..."
systemctl stop "$SERVICE" || true

echo "[2/5] Syncing binaries into $SERVICE_DIR..."
mkdir -p "$SERVICE_DIR"
rsync -a --delete "$STAGING/publish/" "$SERVICE_DIR/"
install -m 0755 "$STAGING/efbundle" "$SERVICE_DIR/efbundle"
chown -R www-data:www-data "$SERVICE_DIR"

echo "[3/5] Syncing static web (zync-web) into the nginx docroot..."
# Web Angular estática: swap atómico hacia el docroot de nginx. Staging may omit the folder
# when the CD ran without the web build; the server deploy still proceeds in that case.
if [ -d "$STAGING/zync-web" ]; then
    WEB_ROOT="/var/www/devlabperu.com/zync-web"
    mkdir -p "$WEB_ROOT"
    rsync -a --delete "$STAGING/zync-web/" "$WEB_ROOT/"
else
    echo "  (no $STAGING/zync-web in staging — skipping web sync)"
fi

echo "[4/5] Applying migrations as postgres (peer auth via socket)..."
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

echo "[5/5] Restarting $SERVICE..."
systemctl restart "$SERVICE"
systemctl --no-pager --lines=0 status "$SERVICE" || true

# Prove the new build is healthy BEFORE we discard the rollback snapshot. A failure here trips the
# ERR trap, which restores the previous build and restarts it.
echo "Health check (loopback /zync/health)..."
health_check

# New build is healthy: drop the rollback snapshot and disarm the trap.
trap - ERR
rm -rf "$PREV_DIR"

echo "Done."
