# Admin portal — switched off for now

The administration and project-setup screens are **hidden** in this phase so the app stays simple for site use. Nothing was deleted: the code, the database tables and every permission rule are unchanged, and one build setting brings everything back.

## What is hidden

| Area | Hidden while off |
|---|---|
| Projects | **＋ New project** (create organisation and project), **Setup** button and the whole Project setup page |
| Project setup | Details, Sites & areas, WBS, BOQ (and BOQ revisions), Contractors, Equipment and Material registers, **Members & roles**, Calendar |
| Activities | Add / Edit / Delete, **Import CSV** |
| Schedule | Apply dates to plan, **Freeze baseline**, add/remove relationships, change the approved baseline |
| Documents | Create Drive folder tree, share the project folder |
| Settings | Connect other devices (connection code), Integrations / API, full project export (JSON), role & permission matrix |

**Still available:**
- Home, **Load demo project**, Daily DPR (all 8 steps, evidence, voice), Approvals and PDFs.
- Activities (view, favourites), Schedule (CPM view, look-ahead, CSV export), Manpower, Equipment, Materials.
- Safety & QA/QC registers, Documents (upload, view), Dashboards.
- Settings: appearance, profile, offline storage, privacy.

## What this means in practice
- Each person who signs up can load their **own demo project** and use the full daily-reporting workflow on it.
- Inviting colleagues into one shared project and creating real projects need the admin portal. Until then, an administrator can do these directly in the database (Supabase → SQL Editor: `select add_project_member(...)`, `select create_project(...)`).
- **Security is unchanged.** Hiding screens does not grant or remove any permission; the database still checks every action.

## Switching it back on
Set the build variable **`VITE_ADMIN_PORTAL=true`** and rebuild:
- **Website on Netlify:** *Site configuration → Environment variables* → add `VITE_ADMIN_PORTAL` = `true` → *Deploys → Trigger deploy*.
- **Android and Windows apps:** add `VITE_ADMIN_PORTAL: 'true'` to the two *"Build the web app for the … shell"* steps in `.github/workflows/apps.yml` (`env:`), then push; the Releases page is rebuilt.
- **Local development:** add `VITE_ADMIN_PORTAL=true` to `web/.env`.

The switch lives in `web/src/lib/features.ts`.
