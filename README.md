# CONSTRUCT DPR

**Construction daily progress reporting and site management** for industrial projects: structural steel, mechanical erection, piping, refractory, civil, testing and commissioning. One C# / .NET 10 code base runs everywhere:

| | Technology | Where |
|---|---|---|
| **Server** | ASP.NET Core · Identity sign-in · REST API · SignalR live updates | Cloud (Render, Azure, any Docker host) or a site-office Windows PC |
| **Database (SQL)** | Microsoft SQL Server, PostgreSQL or SQLite · EF Core | Office SQL Server Express, your Supabase/Neon/own PostgreSQL, or a file |
| **Web app** | Blazor WebAssembly, installable (PWA) | Any browser, served by the server |
| **Android app** | .NET MAUI Blazor Hybrid (same screens as the web app) | `.apk` from Releases |
| **Windows app** | WPF with a native Gantt control · installer | Three modes: **This PC only** (SQLite), **Office SQL Server** (shared Microsoft SQL Server database, accounts, roles, live refresh) or **Cloud server** — see [docs/WINDOWS_APP.md](docs/WINDOWS_APP.md) |
| **Scheduling** | CPM in C# (FS/SS/FF/SF, lag/lead, calendars, floats, critical path, baselines) | Shared by all |
| **Reports** | QuestPDF (PDF) · ClosedXML (Excel export/import) | Server, or the offline Windows app |

All devices signed in to the same server stay in **live sync**. An approval on a PC appears on the phones within a second.

![Dashboard](docs/screenshots/web-01-dashboard.png)

| Phone: daily report entry | Phone: live dashboard | Phone: Gantt chart |
|---|---|---|
| ![](docs/screenshots/web-11-phone-dpr-entry.png) | ![](docs/screenshots/web-10-phone-dashboard.png) | ![](docs/screenshots/web-12-phone-gantt.png) |

| Windows app: daily report review | Windows app: site photos | Windows app: native Gantt chart |
|---|---|---|
| ![](docs/screenshots/windows-dpr-review.png) | ![](docs/screenshots/windows-dpr-photos.png) | ![](docs/screenshots/windows-gantt.png) |

## Get started

**Not a programmer?** Follow [docs/START_HERE.md](docs/START_HERE.md). It takes about 30 minutes, all in the browser: database → server → apps.

**Windows (C# · WPF · SQL):** install **CONSTRUCT-DPR-windows-setup.exe** from the *Releases* page. It opens offline with a demo project. For a whole office on one SQL Server database, follow [docs/WINDOWS_APP.md](docs/WINDOWS_APP.md).

## What it does

| Area | Highlights |
|---|---|
| **Daily progress report** | Activities with today's quantity, live previous / cumulative / balance / actual % / planned %, overrun blocked. Also manpower by agency and trade, equipment hours and breakdowns, material stock (opening + received − issued), weather, narrative and safety notes. Crews are copied from the previous report |
| **Approvals** | Draft → submit → approve (the reviewer may lower the *accepted* quantity) or reject (reason required) → revise. No self-approval unless the project allows it. Approved reports are locked with a SHA-256 snapshot, and tampering is detected |
| **Schedule** | Activities and BOQ, predecessors, CPM with data date and actual dates, critical path, numbered baselines, custom Gantt chart (web/Android SVG, Windows native) |
| **Dashboard** | Physical progress vs plan, S-curve, forecast finish vs contract, executed value (₹), approvals pending, delayed/critical activities |
| **Site photos** | Attached to daily reports, shrunk on the device, stored in the database, printed in the PDF and part of the approval fingerprint |
| **Reports & Excel** | DPR PDF (A4), schedule PDF with Gantt (A3), Excel exports with formulas, Excel activity import that rejects the whole file if any row is wrong |
| **People & roles** | 13 roles × 7 modules × 8 actions ([docs/PERMISSIONS.md](docs/PERMISSIONS.md)), enforced on the server for every call |
| **Safety of data** | Every save carries a version: a stale copy from another device is refused instead of overwriting newer work. Unsaved report edits are kept on the device. There is a full audit trail |
| **Integration** | Plain REST/JSON API ([docs/API.md](docs/API.md)) for ERP, Power BI, Python or Java |

> **Admin portal is off for now** (creating projects and managing people). Everyone can load the demo project. Switch it on with the server setting `Features__AdminPortal=true`.

## For developers

```bash
dotnet test tests/ConstructDpr.Tests                       # 46 tests (needs the .NET 10 SDK); set CDPR_SQLSERVER to include SQL Server
dotnet run --project src/ConstructDpr.Server               # http://localhost:5080 — SQLite file in src/ConstructDpr.Server/data
DATABASE_URL=postgres://user:pass@localhost:5432/db dotnet run --project src/ConstructDpr.Server   # PostgreSQL
dotnet run --project src/ConstructDpr.App                  # Windows app (Windows only)
dotnet build src/ConstructDpr.Mobile -f net10.0-android    # Android (needs: dotnet workload install maui-android)
docker compose up -d                                       # server + PostgreSQL (set POSTGRES_PASSWORD in .env)
```

```
src/
  ConstructDpr.Shared/   entities, CPM, Gantt layout, progress maths, permissions, the API contract (no dependencies)
  ConstructDpr.Core/     EF Core (PostgreSQL schema "cdpr" / SQLite), workflow services, LocalApi = all rules in one place
  ConstructDpr.Reports/  QuestPDF and ClosedXML documents
  ConstructDpr.Server/   ASP.NET Core: sign-in, REST, SignalR hub, serves the web app
  ConstructDpr.Client/   AuthSession, RemoteApi, LiveClient (used by web, Android and Windows apps)
  ConstructDpr.UI/       Blazor screens shared by the web and Android apps
  ConstructDpr.Web/      Blazor WebAssembly host (PWA)
  ConstructDpr.Mobile/   .NET MAUI Android host
  ConstructDpr.App/      WPF Windows app (This PC / Office SQL Server / Cloud server)
tests/ConstructDpr.Tests  xUnit: CPM, workflow, permissions, conflicts, PDF/Excel, server end-to-end
tools/ConstructDpr.Preview  writes sample PDFs and workbooks
installer/               Inno Setup script for the Windows installer
legacy/                  the earlier TypeScript/Supabase, Electron, Capacitor and Google Workspace editions (kept for reference)
```

Architecture: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · Self-hosting: [docs/SELF_HOSTING.md](docs/SELF_HOSTING.md) · API: [docs/API.md](docs/API.md)

## Licence

Apache-2.0. Third-party components: [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md). QuestPDF is free under its Community licence for organisations with under USD 1 million annual revenue; larger companies need a QuestPDF commercial licence. EPPlus is not used (ClosedXML is MIT).
