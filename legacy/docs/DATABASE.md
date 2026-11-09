# Database design

PostgreSQL 15+ (Supabase). UUID primary keys, foreign keys everywhere, `numeric` for money (18,2) and quantities (18,4), `timestamptz` in UTC with the project's reporting timezone stored separately (`projects.timezone`). Every master/transaction table has `created_at, created_by, updated_at, updated_by, version, status` and an audit trigger.

## Entity–relationship diagram (core)

```mermaid
erDiagram
  organizations ||--o{ organization_members : has
  organizations ||--o{ clients : has
  organizations ||--o{ contractors : has
  organizations ||--o{ contracts : has
  clients ||--o{ contracts : awards
  organizations ||--o{ projects : runs
  contracts |o--o{ projects : covers
  projects ||--o{ project_members : "role per user"
  profiles ||--o{ project_members : is
  projects ||--o{ sites : has
  projects ||--o{ areas : "area / unit / system tree"
  projects ||--o{ wbs_nodes : "WBS tree"
  projects ||--o{ project_calendars : has
  projects ||--o{ boq_items : has
  boq_items ||--o{ boq_revisions : "authorised changes"
  change_orders |o--o{ boq_revisions : justifies
  wbs_nodes ||--o{ activities : groups
  areas |o--o{ activities : locates
  boq_items |o--o{ activities : measures
  activities ||--o{ activity_dependencies : "predecessor / successor"
  projects ||--o{ baseline_revisions : freezes
  baseline_revisions ||--o{ baseline_activity_dates : contains
  projects ||--o{ dpr_headers : reports
  dpr_headers |o--o| dpr_headers : "previous revision"
  dpr_headers ||--o{ dpr_activity_entries : "step 2"
  activities ||--o{ dpr_activity_entries : progressed
  dpr_headers ||--o{ manpower_entries : "step 3"
  dpr_headers ||--o{ equipment_entries : "step 4"
  equipment |o--o{ equipment_entries : used
  dpr_headers ||--o{ material_transactions : "step 5"
  materials ||--o{ material_transactions : moves
  dpr_headers |o--o{ safety_observations : "step 6"
  dpr_headers |o--o{ inspection_records : "step 6"
  dpr_headers |o--o{ quality_ncrs : "step 6"
  dpr_headers |o--o{ constraints : "step 6"
  dpr_headers |o--o{ action_items : "step 6"
  dpr_headers ||--o{ approval_actions : history
  dpr_headers ||--o| report_revisions : "approved snapshot"
  projects ||--o{ drive_folders : "folder registry"
  dpr_headers |o--o{ attachments : evidence
  activities |o--o{ attachments : evidence
  attachments ||--o| drive_upload_sessions : "server-only"
  projects ||--o{ audit_events : logs
  projects ||--o{ fabrication_items : "phase 6"
  projects ||--o{ dispatch_records : "phase 6"
  projects ||--o{ erection_records : "phase 6"
  projects ||--o{ commissioning_checklists : "phase 6"
```

## Tables (all required entities)

| Area | Tables |
|---|---|
| Identity & tenancy | `profiles` (1:1 with `auth.users`), `organizations`, `organization_members`, `project_members`, `role_permissions`, `project_role_permissions` |
| Masters | `clients`, `contracts`, `contractors`, `projects`, `sites`, `areas`, `wbs_nodes`, `activities`, `activity_dependencies`, `project_calendars`, `equipment`, `materials` |
| Planning | `baseline_revisions`, `baseline_activity_dates` (frozen; update is blocked by trigger) |
| Commercial | `boq_items`, `boq_revisions` (append-only), `change_orders` |
| DPR | `dpr_headers` (+ narrative), `dpr_activity_entries`, `manpower_entries`, `equipment_entries`, `material_transactions` |
| Registers | `safety_observations`, `inspection_records`, `quality_ncrs`, `constraints`, `action_items` |
| Workflow | `approval_steps`, `approval_actions` (no update), `report_revisions` (immutable snapshot + SHA-256; PDF reference can be set once) |
| Documents | `drive_folders` (unique per project+path → idempotent), `attachments`, `drive_upload_sessions` (no API access) |
| Sync & audit | `synchronization_jobs` (idempotency results), `audit_events` (append-only) |
| Integration | `integration_events` (append-only, ordered event feed for other software; see [INTEGRATIONS.md](INTEGRATIONS.md)) |
| Industrial (Phase 6) | `fabrication_items`, `dispatch_records`, `erection_records`, `commissioning_checklists` |

## Views (security_invoker — caller's RLS applies)

`dpr_effective` (latest *approved* revision per DPR number) · `dpr_effective_entries` · `activity_progress` · `boq_status` (amounts) · `manpower_daily` · `equipment_daily` · `material_ledger` · `material_stock`.

## Integrity rules enforced in the database

| Rule | Mechanism |
|---|---|
| One original DPR per project / date / shift / area | partial unique index `dpr_one_per_slot` |
| Approved DPRs and their rows cannot change, even by the DB owner | triggers `guard_approved_dpr`, `guard_approved_dpr_child` |
| Corrections = new revision with reason and link to previous | `start_dpr_revision()`, check constraint on `dpr_headers` |
| Cumulative ≤ approved quantity unless an authorised overrun with reason | `submit_dpr()` / `review_dpr()` + check constraint |
| Reported, inspected and accepted quantities kept separately | `qty_today`, `qty_inspected`, `qty_accepted` |
| Previous cumulative is computed by the server from approved revisions only | `prior_accepted_qty()` |
| Stock never negative; rejected ≤ received | check constraints on `material_transactions` |
| Equipment hours ≤ 24; breakdown requires a reason | check constraints |
| BOQ quantity/rate change only via `revise_boq_item()` with reason | trigger + append-only `boq_revisions` |
| Billed ≤ certified | check constraint |
| Concurrent edits detected | `version` column; workflow functions take `expected_version` |
| Retry-safe saves | `synchronization_jobs.idempotency_key` |
| Every change logged | `audit_row()` trigger → `audit_events` |

## Workflow functions (RPC)

`create_organization`, `create_project`, `add_project_member`, `set_project_member_active`, `save_dpr`, `submit_dpr`, `review_dpr` (start_review / approve / reject / comment), `start_dpr_revision`, `dpr_document`, `dpr_activity_context`, `create_baseline`, `set_current_baseline`, `apply_schedule_dates`, `revise_boq_item`, `register_drive_folder`, `set_report_pdf`, `export_project`, `my_permissions`, `load_demo_project`.

Each one checks the caller's permission first (`require_perm`) and raises `PT4xx` errors, which PostgREST maps to HTTP status codes.

## Indexes

Project id on every child table; plus `(project_id, report_date)`, `(project_id, status)`, `(project_id, activity_id)`, `(project_id, material_id, txn_date)`, `(project_id, sha256)`, audit `(project_id, occurred_at)`.
