# CONSTRUCT DPR

**Open-source construction Daily Progress Reporting & site management** — a mobile-first, installable web app (PWA) for industrial construction: structural steel, mechanical equipment erection, piping, refractory, civil, fabrication, supply, testing and commissioning.

Simple enough for a site supervisor on an Android phone; structured enough for project controls (BOQ, baselines, critical path, S-curves, approvals, audit).

* **Records** → PostgreSQL (Supabase, cloud or self-hosted), protected by row-level security
* **Photos, video, voice notes, drawings, PDFs** → your Google Drive, in a standard project folder tree
* **Works offline** on site; synchronises safely without duplicates when the network returns

---

## Apps for Android, Windows and the web

Download the **Android APK** and the **Windows installer** from the repository's *Releases* page (**CONSTRUCT DPR apps (latest build)**), or use any browser. All are live-synchronised with each other — see [docs/APPS.md](docs/APPS.md).

## Two editions — pick one

| | |
|---|---|
| **Web edition** (this README) | Installable web app + PostgreSQL (Supabase). Strict database-enforced controls, scales to many projects. Start: [docs/START_HERE.md](docs/START_HERE.md) |
| **Google Workspace edition** | Everything inside Google: Sheets + AppSheet + Apps Script + Drive + Looker Studio. No hosting; customise the app without code. Start: [docs/GOOGLE_WORKSPACE_EDITION.md](docs/GOOGLE_WORKSPACE_EDITION.md) |

## What you get

| Area | Highlights |
|---|---|
| **Daily DPR wizard** | 8 steps (report → activities → manpower → equipment → materials → safety & QA → narrative → review). Live previous / cumulative / balance / % / planned % / variance; overrun protection; autosave on the device |
| **Evidence** | Camera (front/rear, multi-shot, retake), short video, gallery/files, voice notes with optional speech-to-text, one-shot GPS or typed location. Nothing is activated without a tap |
| **Voice → structured entry** | "At the skip bridge, erected twelve metric tonnes … one mobile crane and twenty-four workers" → activity, 12 MT, location, shift, crane, 24 workers — confirmed by the user before anything is added |
| **Approvals** | Submit → review → approve (accepted quantity) / reject (reason) → revise. No self-approval by default. Approved reports become immutable snapshots with SHA-256 |
| **PDF reports** | 18-section A4 portrait/landscape report with photos, page numbers, sign-off and revision history; published to Drive |
| **Offline & sync** | IndexedDB drafts, outbox with idempotency keys, exponential backoff, explicit conflict resolution, separate status for data and files |
| **Live updates** | Supabase Realtime: approvals, lists and masters refresh on every device when changes are committed |
| **Scheduling (P6-style)** | WBS, FS/SS/FF/SF with lag/lead, constraints, calendars & holidays, CPM (early/late dates, total/free float, critical path), look-ahead, frozen baseline revisions, variance |
| **Resources** | Manpower by trade/agency/area/shift, equipment utilisation & certificate expiry, store ledger with shortages |
| **Commercial** | BOQ with tender/revised qty, rate (₹), executed / measured / certified / billed kept separate, authorised revisions with reasons |
| **Safety & QA/QC** | Permits with configurable checklists (hot work, height, lifting, confined space, excavation, LOTO), observations, inspections & hold points, NCRs, constraints, actions, commissioning & punch list |
| **Dashboards** | KPIs, S-curve, production, manpower, equipment, area & contractor progress, delay Pareto, materials, quantity & billing — each with range, unit, method, table view |
| **Security** | 13 roles × 12 modules × 8 actions, per-project overrides, RLS on every table, server-side validation, audit log, no secrets in the browser |
| **Integrations** | Works with other software: REST API (OpenAPI), ordered event feed, `integration` role, ready-made **Python** and **Java** clients, CLI, Excel/Power BI/Google Sheets recipes — [docs/INTEGRATIONS.md](docs/INTEGRATIONS.md) |
| **Portability** | CSV export on every register, full-project JSON export, CSV activity import |

## Quick start

**Not a programmer?** Follow [docs/START_HERE.md](docs/START_HERE.md): browser-only setup with Supabase + Netlify, about 30–45 minutes. Then install it on Windows: [docs/WINDOWS.md](docs/WINDOWS.md).

**Developers:**

```bash
# 1. Backend: create a Supabase project (or self-host), then apply the migrations
npx supabase link --project-ref <your-ref> && npx supabase db push

# 2. Web app
cd web
cp .env.example .env        # VITE_SUPABASE_URL + VITE_SUPABASE_ANON_KEY
npm ci && npm run dev       # http://localhost:5173

# 3. Sign up in the app → "Load demo project"
```

Google Drive storage and Google sign-in: [docs/GOOGLE_SETUP.md](docs/GOOGLE_SETUP.md). Full instructions incl. self-hosting with Docker: [docs/SETUP.md](docs/SETUP.md).

## Tests

```bash
cd web && npm test              # 39 unit tests
supabase/tests/run.sh           # 60 database acceptance checks (PostgreSQL 15+)
cd clients/python && python -m unittest discover -s tests   # 9 Python client tests
cd clients/java && ./build.sh test                          # 11 Java client tests
cd google-workspace && npm ci && npm test                   # 12 Google-edition end-to-end tests
```
Status against acceptance tests A–L: [docs/TESTING.md](docs/TESTING.md).

## Documentation

| | |
|---|---|
| [Architecture](docs/ARCHITECTURE.md) | System diagram, key decisions, request flow |
| [Database](docs/DATABASE.md) | ER diagram, tables, integrity rules, functions |
| [Permissions](docs/PERMISSIONS.md) | Role × module matrix |
| [Workflows](docs/WORKFLOWS.md) | Reporting, voice, approval, offline sync, planning |
| [Google setup](docs/GOOGLE_SETUP.md) | OAuth, Drive scopes, folder tree, security implications |
| [Setup & deployment](docs/SETUP.md) | Cloud, self-hosted, local dev, env vars |
| [User manual](docs/USER_MANUAL.md) | By role |
| [Integrations](docs/INTEGRATIONS.md) | Python, Java, ERP, Excel/Power BI, Google Sheets; API and event feed |
| [Costs & limits](docs/COSTS_AND_LIMITS.md) | What is free, what is not, quotas |
| [Security](docs/SECURITY.md) | Controls, privacy, residual risks |
| [Backup & retention](docs/BACKUP_AND_RETENTION.md) | Backup/restore, retention, deletion |
| [Troubleshooting](docs/TROUBLESHOOTING.md) | Common problems |
| [Implementation plan](docs/IMPLEMENTATION_PLAN.md) | Phases 1–6 status and next steps |
| [P6 exchange](docs/P6_EXCHANGE.md) · [Sheets prototype](docs/SHEETS_PROTOTYPE.md) | Interop and its limits |

## Repository layout

```
supabase/
  migrations/      11 SQL migrations: schema, RLS, workflow functions, demo loader
  functions/drive/ Google Drive gateway (Deno edge function; core logic unit-tested)
  tests/           database acceptance tests
web/
  src/lib/         sync engine, IndexedDB, CPM, calculations, voice parser, PDF, Drive client
  src/pages/       Home, Projects, DPR wizard/view, Activities, Schedule, Manpower, Equipment,
                   Materials, Safety & QA/QC, Documents, Dashboards, Approvals, Settings
  src/components/  UI kit, evidence capture, charts, master tables
native/desktop/    Windows app (Electron shell around web/)
web/android/       Android app (Capacitor project)
google-workspace/  Google Workspace edition (Apps Script source, tests, dist/Code.gs)
clients/
  python/          Python client + CLI (standard library only)
  java/            Java 11+ client (no dependencies)
docs/              architecture, guides and manuals
```

## Licence

Apache-2.0. Third-party licences: [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md).
