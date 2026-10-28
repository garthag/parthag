# Architecture

## 1. System architecture

```mermaid
flowchart LR
  subgraph Device["Phone / tablet / laptop — Chrome (installable PWA)"]
    UI["React UI<br/>DPR wizard, schedule, dashboards"]
    IDB[("IndexedDB<br/>drafts · outbox · media queue · cache")]
    SW["Service worker<br/>app shell offline"]
    HW["Camera · mic · GPS<br/>(only on explicit tap)"]
    UI <--> IDB
    UI --- HW
    SW --- UI
  end

  subgraph Supabase["Backend — Supabase (cloud or self-hosted, open source)"]
    AUTH["Auth (GoTrue)<br/>Google sign-in / e-mail"]
    REST["PostgREST API"]
    RT["Realtime<br/>(committed row changes, RLS-filtered)"]
    FN["Edge function `drive`<br/>holds Google credential"]
    PG[("PostgreSQL<br/>tables · RLS · workflow functions<br/>audit log · snapshots")]
    REST --> PG
    RT --> PG
    FN --> PG
  end

  GD[("Google Drive<br/>project folder tree<br/>photos · video · audio · PDFs")]

  UI -- "JWT" --> AUTH
  UI -- "RPC / table API (JWT)" --> REST
  UI <-- "websocket" --> RT
  UI -- "file chunks (JWT)" --> FN
  FN -- "drive.file scope<br/>resumable upload" --> GD
```

**Key decisions**

| Decision | Why |
|---|---|
| PostgreSQL for all structured records; Google Drive for files only | Relational integrity, transactions, row-level security, audit — none of which Sheets/Drive provide. Drive is cheap, familiar storage for large media. |
| Business rules live **in the database** (RLS + security-definer functions) | The browser is never trusted. The same rules apply to the app, the API, scripts and any future client. |
| Approved DPR = immutable JSON snapshot + SHA-256 | A report, once approved, can always be reproduced exactly, even after BOQ/masters change. |
| Drive access through a server function holding one document-controller credential | With the narrow `drive.file` scope each *user's* token can only see files *that user's* session created, which breaks a shared project tree. A single server-held credential keeps one tree, keeps secrets off phones, and lets the database decide who may upload/view. |
| Offline-first client with outbox + idempotency keys | Site networks drop; retries must never duplicate a DPR. |
| CPM engine in TypeScript on the client | Deterministic, unit-tested, works offline; results are written back only through `apply_schedule_dates()` with version checks. |

## 2. Request flow for a DPR

```mermaid
sequenceDiagram
  autonumber
  participant SE as Site engineer (phone)
  participant IDB as IndexedDB
  participant API as PostgREST
  participant DB as PostgreSQL
  participant RT as Realtime
  participant PM as Project manager (laptop)

  SE->>IDB: autosave draft every keystroke (offline OK)
  SE->>IDB: "Save draft" → outbox item {payload, idempotency_key}
  IDB->>API: rpc save_dpr(p) when online (retry with backoff)
  API->>DB: one transaction: permission check, version check,<br/>header + 9 child sections, server-side quantities
  DB-->>API: {id, dpr_no, version}  (stored against idempotency key)
  DB-->>RT: committed change
  RT-->>PM: list refreshes live
  SE->>API: rpc submit_dpr(id, version)  — overrun & completeness checks
  PM->>API: rpc review_dpr(id, 'approve', accepted quantities)
  API->>DB: freeze: status approved, activity actuals, report_revisions snapshot + SHA-256
  PM->>API: (browser) build PDF from snapshot → drive function → Drive
```

## 3. Components

| Layer | Path | Notes |
|---|---|---|
| Database schema & rules | `supabase/migrations/*.sql` | 8 ordered migrations; see [DATABASE.md](DATABASE.md) |
| DB acceptance tests | `supabase/tests/` | Run against plain PostgreSQL with a Supabase shim |
| Drive gateway | `supabase/functions/drive/` | `core.ts` is runtime-agnostic and unit-tested |
| Web app | `web/src` | `lib/` logic (sync, CPM, calc, voice, pdf), `pages/`, `components/` |
| Unit tests | `web/src/__tests__` | progress maths, money, stock, CPM, S-curve, voice, Drive, sync |

## 4. Data freshness and status shown to users

The status bar always shows **database** sync and **file upload** status separately:
*Saved locally → Waiting to synchronise → Synchronising → Synchronised*, or *Sync failed / Conflict requires review*; files: *Waiting / Uploading n% / In Drive / Upload failed*.
Dashboards state their date range, unit, method and data time.

## 5. What the system deliberately does not claim

* No "instant" sync or "zero data loss": devices can be offline for days and browsers can evict storage.
* No regulatory certification from permit checklists.
* No proof of authenticity from device time, GPS or EXIF.
* No native Primavera P6 compatibility (CSV exchange is documented and tested only for the columns listed in [P6_EXCHANGE.md](P6_EXCHANGE.md)).
