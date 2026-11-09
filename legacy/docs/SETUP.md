# Setup, build and deployment

You need: Node.js 20+ (22 recommended), a Supabase backend (cloud free tier or self-hosted), optionally a Google Cloud project for Drive / Google sign-in.

## A. Fastest path — Supabase Cloud + any static host

1. **Create a Supabase project** at <https://supabase.com> (free tier is enough to start).
2. **Apply the database migrations** (in order). Either:
   * Supabase CLI: `npx supabase link --project-ref <ref>` then `npx supabase db push`; or
   * Dashboard → SQL editor → paste `supabase/setup/part1.sql`, run, then `supabase/setup/part2.sql` (generated from the migrations by `supabase/setup/build.sh`).
3. **Enable Realtime** is automatic (the migration adds tables to the `supabase_realtime` publication).
4. **Auth**: Dashboard → Authentication → URL configuration → Site URL = your app URL. Optionally enable Google (see [GOOGLE_SETUP.md](GOOGLE_SETUP.md)).
5. **Drive function** (optional but needed for photos/documents): follow [GOOGLE_SETUP.md](GOOGLE_SETUP.md), then `npx supabase functions deploy drive`.
6. **Configure the web app**:
   ```bash
   cd web
   cp .env.example .env      # set VITE_SUPABASE_URL and VITE_SUPABASE_ANON_KEY (Dashboard → Project settings → API)
   npm ci
   npm run dev               # http://localhost:5173
   npm run build             # static files in web/dist
   ```
7. **Host `web/dist`** on any static host with HTTPS (Netlify, Cloudflare Pages, GitHub Pages with SPA fallback, Vercel, nginx). Camera, microphone, GPS and installation as an app require HTTPS.
8. Open the app, create an account, press **Load demo project**.

## B. Self-hosted (no paid subscription)

1. Run the official open-source Supabase stack with Docker: <https://supabase.com/docs/guides/self-hosting/docker> (PostgreSQL, Auth, PostgREST, Realtime, Storage, Edge Runtime, Studio).
2. Apply `supabase/migrations/*.sql` with `psql` in filename order:
   ```bash
   for f in supabase/migrations/*.sql; do psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f "$f"; done
   ```
3. Copy `supabase/functions/drive` into the self-hosted `volumes/functions/` directory and set the Google variables in the stack's `.env` (see `supabase/functions/.env.example`).
4. Build and run the frontend container:
   ```bash
   VITE_SUPABASE_URL=https://api.yourdomain VITE_SUPABASE_ANON_KEY=... docker compose up -d --build
   ```
   Put both behind a TLS reverse proxy (Caddy/nginx/Traefik). Edit `connect-src` in `web/nginx.conf` to your API domain.

## C. Local development with the Supabase CLI

```bash
npx supabase start            # needs Docker; applies migrations; prints local URL + anon key
cd web && cp .env.example .env  # use the printed values (http://localhost:54321)
npm run dev
npx supabase functions serve drive --env-file supabase/functions/.env
```

## Environment variables

| Where | Variable | Secret? |
|---|---|---|
| web | `VITE_SUPABASE_URL`, `VITE_SUPABASE_ANON_KEY`, `VITE_DRIVE_ENABLED` | **No** — public by design |
| drive function | `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `GOOGLE_REFRESH_TOKEN` or `GOOGLE_SERVICE_ACCOUNT_JSON`, `GOOGLE_SHARED_DRIVE_ID`, `DRIVE_ROOT_FOLDER_ID`, `ALLOWED_ORIGINS` | **Yes** |
| drive function (auto) | `SUPABASE_URL`, `SUPABASE_ANON_KEY`, `SUPABASE_SERVICE_ROLE_KEY` | service role **yes** |

Never place the service-role key or any Google secret in a `VITE_` variable.

## Running the tests

```bash
cd web && npm test                         # 37 unit tests (calculations, CPM, voice, Drive, sync)
cd web && npx tsc -b && npm run build      # type-check and production build
supabase/tests/run.sh                      # database acceptance tests (needs createdb/psql access to PostgreSQL 15+)
```
CI runs all three on every push (`.github/workflows/ci.yml`).

## Installing on Android

Open the app URL in Chrome → menu → *Install app* (or *Add to Home screen*). It then opens full-screen, works offline for drafts, and updates when you accept the "new version" prompt.
