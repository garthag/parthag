# Running the server yourself

The server is one program: **ConstructDpr.Server**. It contains the web app. Choose where it runs:

| Option | Database | Good for |
|---|---|---|
| **Render** (render.yaml) | Supabase / Neon / Render PostgreSQL | Easiest cloud start; see START_HERE.md |
| **Docker** (`docker compose up -d`) | PostgreSQL container | Your own cloud VM or company server |
| **Windows site-office PC** | SQLite file next to the program | A site without reliable internet; phones connect over the site Wi-Fi |
| **Azure App Service / IIS** | Azure Database for PostgreSQL / any PostgreSQL | Company IT standards |

## Settings (environment variables or appsettings.json)

| Setting | Meaning | Default |
|---|---|---|
| `DATABASE_URL` | `postgres://user:password@host:5432/database` (Supabase, Neon, Render…). Turns on PostgreSQL | — |
| `Database__Provider` + `ConnectionStrings__Default` | Alternative: `PostgreSql` or `Sqlite` with a normal connection string | Sqlite, file `data/construct-dpr.db` |
| `Features__AdminPortal` | `true` lets people create projects and manage members | `false` |
| `Features__Registration` | `false` stops new sign-ups (create accounts first, then switch off) | `true` |
| `Site__TimeZone` | "Today" for reports and the CPM data date | `Asia/Kolkata` |

Tables are created on first start in the schema `cdpr` (PostgreSQL), so an existing database is safe to use. Sign-in keys are stored in the database, so people stay signed in after restarts and redeploys.

## Windows site-office PC

1. Download **CONSTRUCT-DPR-server-windows.zip** from Releases and unzip it, e.g. to `C:\ConstructDpr`.
2. Double-click **start-server.cmd**. When Windows Firewall asks, allow **private networks**.
3. On phones and PCs connected to the site Wi-Fi, the server address is `http://<PC-name>:8080` (or `http://<PC-IP>:8080`, found with `ipconfig`).
4. Keep the PC switched on and back up the `data` folder daily (copy `construct-dpr.db` to a USB drive or OneDrive).

`http://` is acceptable inside a closed site network. For access over the internet, always put the server behind HTTPS: a hosting platform does this automatically, or use a reverse proxy such as Caddy or IIS with a certificate.

## Docker

```bash
echo "POSTGRES_PASSWORD=$(openssl rand -hex 16)" > .env
docker compose up -d          # http://localhost:8080
docker compose logs -f app
```

Back up with `docker compose exec db pg_dump -U constructdpr constructdpr > backup.sql`.

## Backups

* **Supabase / Neon:** daily backups are included, depending on the plan. You can also run `pg_dump` with your DATABASE_URL.
* **SQLite (site-office PC or offline Windows app):** copy the `.db` file. The Windows app has **Settings → Back up database…**, which makes a consistent copy while the app is running.

## Android signing (installing updates over older versions)

Releases built without signing secrets are signed with a temporary key, so you must uninstall the old app before installing a new build. To keep one signature:

1. Create a keystore once: `keytool -genkeypair -v -keystore dpr.keystore -alias dpr -keyalg RSA -keysize 2048 -validity 10000`.
2. Add these GitHub repository secrets: `ANDROID_KEYSTORE_BASE64` (the file, base64-encoded), `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` and `ANDROID_KEY_PASSWORD`.

Keep the keystore safe: if it is lost, users must uninstall and reinstall the app.
