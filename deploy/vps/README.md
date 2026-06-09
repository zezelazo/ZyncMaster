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
Los `GRANT` CRUD se aplican **después** del primer deploy (cuando existan las tablas):
```sql
\c bd_syncmaster
GRANT USAGE ON SCHEMA public TO syncmaster_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES    IN SCHEMA public TO syncmaster_app;
GRANT USAGE, SELECT                  ON ALL SEQUENCES IN SCHEMA public TO syncmaster_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES    TO syncmaster_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT                  ON SEQUENCES TO syncmaster_app;
REVOKE CREATE ON SCHEMA public FROM syncmaster_app;
```
> `ALTER DEFAULT PRIVILEGES` hace que las tablas que cree una migración futura (como `postgres`)
> hereden CRUD automáticamente. Solo se corre una vez.

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

## Deploy

Manual: GitHub → Actions → **Deploy SyncMaster (VPS)** → Run workflow. El job compila, genera el
`efbundle` linux-x64, lo sube + los binarios a staging, corre `deploy-syncmaster.sh` (swap +
migrate como postgres + restart) y verifica `https://api.devlabperu.com/zync/health`. Todo online,
sin artifacts. Rollback: el deploy detiene el servicio antes de sincronizar; si el health falla,
revertí el commit y re-dispará.
