# SyncMaster — despliegue en el VPS (devlabperu.com)

Backend `ZyncMaster.Server` corriendo en el VPS sobre PostgreSQL 18, expuesto en
`https://api.devlabperu.com/zync/`. Sigue el patrón de los demás servicios (nexo, timefocus):
systemd + `User=www-data` + secretos en `/etc/default/`, nginx por path, despliegue por SSH.

| Recurso | Valor |
|---|---|
| Servicio systemd | `syncmaster` |
| Puerto interno | `127.0.0.1:5007` |
| Path público | `/zync/` (PathBase `/zync`) |
| Base de datos | `bd_syncmaster` (owner `postgres`) |
| Rol app | `syncmaster_app` (CRUD-only, sin DDL) |
| Dir binarios | `/var/www/devlabperu.com/api/syncmaster` |
| Secretos | `/etc/default/syncmaster` |

---

## Provisión one-time (manual en el VPS)

### 1. Postgres (ya hecho)
```sql
CREATE ROLE syncmaster_app  LOGIN PASSWORD '<APP_PWD>' NOSUPERUSER NOCREATEDB NOCREATEROLE;
CREATE DATABASE bd_syncmaster OWNER postgres;
```
Los `GRANT` CRUD a `syncmaster_app` **NO se corren a mano**: `deploy-syncmaster.sh` los aplica
(idempotentes) en cada deploy, después del `efbundle` y antes del restart, así el primer arranque
ya tiene permisos y cualquier tabla nueva de una migración futura queda usable de inmediato. El rol
sigue CRUD-only (sin CREATE).

### 2. Environment file con secretos
```bash
sudo install -o root -g root -m 0600 /dev/null /etc/default/syncmaster
sudo nano /etc/default/syncmaster
```
```ini
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5007
ConnectionStrings__ZyncMasterDb=Host=localhost;Port=5432;Database=bd_syncmaster;Username=syncmaster_app;Password=<APP_PWD>
Server__PathBase=/zync
Server__PublicBaseUrl=https://api.devlabperu.com/zync
Server__MicrosoftClientId=<de Azure>
Microsoft__ClientSecret=<de Azure>
Server__IdentityRedirectUri=https://api.devlabperu.com/zync/identity/connect/callback/microsoft
Server__CalendarRedirectUri=https://api.devlabperu.com/zync/calendar/connect/callback/graph
Server__RedirectUri=https://api.devlabperu.com/zync/connect/callback
Server__CronTriggerSecret=<genera uno>
# Opcional (email magic-link):
# Mailjet__ApiKey=...
# Mailjet__ApiSecret=...
```

### 3. systemd unit
```bash
sudo cp deploy/vps/syncmaster.service /etc/systemd/system/syncmaster.service
sudo mkdir -p /var/www/devlabperu.com/api/syncmaster
sudo chown www-data:www-data /var/www/devlabperu.com/api/syncmaster
sudo systemctl daemon-reload
sudo systemctl enable syncmaster
```

### 4. Script de deploy + sudoers (lo invoca el CD)
```bash
sudo cp deploy/vps/deploy-syncmaster.sh /var/www/devlabperu.com/api/.scripts/deploy-syncmaster.sh
sudo chown root:root /var/www/devlabperu.com/api/.scripts/deploy-syncmaster.sh
sudo chmod 0755 /var/www/devlabperu.com/api/.scripts/deploy-syncmaster.sh

echo 'devlab ALL=(root) NOPASSWD: /var/www/devlabperu.com/api/.scripts/deploy-syncmaster.sh' \
  | sudo tee /etc/sudoers.d/syncmaster-deploy
sudo chmod 440 /etc/sudoers.d/syncmaster-deploy
```

### 5. nginx
Pegar los bloques de `deploy/vps/nginx-syncmaster.conf` dentro del `server { server_name
api.devlabperu.com; ... }` (antes de `listen 443 ssl`), luego:
```bash
sudo nginx -t && sudo systemctl reload nginx
```

### 6. Key de GitHub Actions
```bash
cat >> ~/.ssh/authorized_keys   # pegar el contenido de id_ed25519_gh_actions.pub
chmod 600 ~/.ssh/authorized_keys
```

---

## Secrets de GitHub (repo → Settings → Secrets and variables → Actions)

| Secret | Valor |
|---|---|
| `VPS_SSH_KEY` | contenido **privado** de `id_ed25519_gh_actions` (la pública va en authorized_keys del VPS) |

Host/puerto/usuario van en el `env:` del workflow (no son secretos).

---

## Entra ID (prerequisito OAuth, fuera del VPS)

En la app registration de Microsoft, añadir como **Redirect URIs**:
- `https://api.devlabperu.com/zync/identity/connect/callback/microsoft`
- `https://api.devlabperu.com/zync/calendar/connect/callback/graph`
- `https://api.devlabperu.com/zync/connect/callback`

Sin esto, login/calendar OAuth falla con `AADSTS50011 (redirect mismatch)`.

---

## Web (zync-web)

La web Angular se sirve como estático puro desde `https://api.devlabperu.com/zync-web/`
(sin SSR, sin Node en el VPS):

- **Docroot:** `/var/www/devlabperu.com/zync-web/`. El CD compila `web/zync-web` con
  `ng build --configuration production`, sube `dist/zync-web/browser/` a
  `~/syncmaster-staging/zync-web/` y `deploy-syncmaster.sh` hace el swap (`rsync --delete`)
  hacia el docroot.
- **Bloque nginx nuevo** en `deploy/vps/nginx-syncmaster.conf` (`location /zync-web/`):
  `try_files $uri $uri/ /zync-web/index.html` para el routing client-side, bundles hasheados
  con `Cache-Control: immutable`, `index.html` con `no-cache`, y CSP estricta. Se pega a mano
  en el mismo `server` block de `api.devlabperu.com` y se recarga con
  `sudo nginx -t && sudo systemctl reload nginx`.
- **Health check del CD:** además de `/zync/health`, el workflow pide el deep-link
  `https://api.devlabperu.com/zync-web/calendar` y exige `200` — eso valida que el
  `try_files` de nginx resuelve rutas internas de la SPA hacia `index.html`.

---

## Deploy

Manual: GitHub → Actions → **Deploy SyncMaster (VPS)** → Run workflow. El job compila, genera el
`efbundle` linux-x64, lo sube + los binarios a staging, corre `deploy-syncmaster.sh` (swap +
migrate como postgres + restart) y verifica `https://api.devlabperu.com/zync/health`. Todo online,
sin artifacts.

## Rollback automático

`deploy-syncmaster.sh` **snapshotea** el build vivo (`cp -al` → `…/syncmaster.prev`) antes de tocarlo,
hace un **health-gate in-process** contra `127.0.0.1:5007/zync/health` tras el restart, y si el swap /
migración / restart / health **falla**, un `trap ERR` **restaura el build anterior y reinicia**. El
snapshot se descarta solo cuando el build nuevo prueba estar sano. (Re-instalá el script en el VPS
cuando cambie: el workflow corre la copia en `/var/www/devlabperu.com/api/.scripts/`, no la del repo.)

## Backup de la BD (crítico)

`bd_syncmaster` guarda el **key ring de DataProtection + los refresh tokens cifrados** → perder el
disco = reset total. Instalá el backup nocturno (one-time, como root):

```
sudo install -m 0755 deploy/vps/syncmaster-backup.sh /var/www/devlabperu.com/api/.scripts/syncmaster-backup.sh
sudo install -m 0644 deploy/vps/syncmaster-backup.cron /etc/cron.d/syncmaster-backup
# off-box + cifrado en /etc/default/syncmaster-backup (SYNCMASTER_BACKUP_AGE_RECIPIENT, _RCLONE_REMOTE)
sudo /var/www/devlabperu.com/api/.scripts/syncmaster-backup.sh   # probar + verificar el restore
```

`pg_dump -Fc` por peer-auth (sin password), cifra con `age` si hay recipient, copia off-box con
`rclone` si hay remote, poda local a 14 días. **Configurá el off-box** — un backup en el mismo disco
no protege de una falla de disco. Detalle en el header de `syncmaster-backup.sh`.
