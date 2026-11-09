# Implementation plan and status

| Phase | Scope | Status in this repository |
|---|---|---|
| **1** | Project master, authentication, roles, WBS, activity register, basic DPR forms | **Delivered** — org/client/contract/project/site/area/WBS/activity masters, 13 roles with configurable matrix, full 8-step wizard |
| **2** | PostgreSQL integration, Google Drive uploads, photographs, PDF reports, approval workflows | **Delivered** — RLS + workflow functions, Drive edge function (needs your Google credentials), camera/video/gallery/voice/location capture, 18-section PDF from immutable snapshot, submit/review/approve/reject/revise |
| **3** | Real-time collaboration, offline drafts, synchronisation recovery, audit trails | **Delivered** — Realtime subscriptions, IndexedDB drafts/outbox/media queue, idempotency keys, version conflicts with explicit resolution, backoff, audit log |
| **4** | Manpower, equipment, material, safety, QA/QC, commercial | **Delivered (core)** — DPR sections + aggregation pages, store ledger, registers, BOQ with measured/certified/billed, authorised revisions. *Next:* change-order approval screen, running-account bill generation |
| **5** | P6-style scheduling, baselines, dependencies, critical path, S-curves, analytics | **Delivered (core)** — CPM engine, look-ahead, baselines with history, variance, dashboards. *Next:* resource loading, multiple calendars per activity, XER/XML import |
| **6** | Fabrication, dispatch, erection, commissioning, advanced reporting | **Partly** — schema for all four packages; commissioning/punch-list register UI. *Next:* fabrication/dispatch/erection screens, weekly/monthly reports |

## Recommended next steps (in order)

1. Deploy to a Supabase project + static host and run the manual script in [TESTING.md](TESTING.md) (Tests B, C, D, L on real devices).
2. Pilot on one project with 3–5 users for two weeks; collect field feedback (glove use, sunlight, network).
3. Phase 4 completion: change-order workflow, measurement sheets → certified quantities → RA bill export (XLSX).
4. Phase 6 screens for fabrication → dispatch → erection tracking linked by E-mark.
5. XLSX exports (e.g. ExcelJS) and weekly/monthly consolidated PDFs.
6. Optional: push notifications for approvals (Web Push), multi-step approval chains using `approval_steps`.
