# Testing and acceptance status

Two automated suites run in CI:

* **Database acceptance tests** — `supabase/tests/run.sh` applies every migration to a fresh PostgreSQL database (with a small shim for the Supabase `auth` schema) and runs 43 checks **as the `authenticated` role with a JWT subject**, exactly like API requests, so row-level security is exercised.
* **Unit tests** — `cd web && npm test` (39 tests): progress maths, money, stock ledger, working calendar, CPM engine, S-curve, voice parser, Drive service against a simulated Drive API, offline sync engine against a simulated server, and the PDF generator.

## Status against the acceptance criteria

Legend: ✅ verified automatically · 🟡 logic verified, needs a run against real external services · ⬜ manual test to perform after deployment

| Test | Status | Evidence / what remains |
|---|---|---|
| **A** Daily reporting | ✅ DB | DPR created via `save_dpr`, server computes previous/cumulative/balance/% and planned %, re-read persists; next DPR builds on accepted cumulative. ⬜ click-through on a deployed instance |
| **B** Multi-device updates | 🟡 | Tables are in the `supabase_realtime` publication; UI subscribes and reloads on committed changes. ⬜ open two browsers on a deployed instance and edit an activity |
| **C** Media | 🟡 | Folder tree idempotency, race handling, chunked resumable upload, reconciliation and type/size rules tested against a simulated Drive API. ⬜ upload a real photo with real Google credentials and open it as an authorised and an unauthorised user |
| **D** Voice | 🟡 | Parser tested on the reference sentence and variants; confirmation dialog verified in a real browser. ⬜ record on an Android phone with microphone permission |
| **E** Offline | ✅ client + DB | Sync engine: offline queue, reconnection, lost-response replay, conflict + resolution (simulated server). DB: idempotency key replay, duplicate-slot refusal. ⬜ airplane-mode test on a phone |
| **F** Schedule | ✅ | CPM forward/backward pass, floats, critical path, FS/SS/FF/SF, leads/lags, constraints, actuals & data date, cycle detection, calendars/holidays, S-curve |
| **G** DPR PDF | ✅ | PDF built from a snapshot contains the same quantities, totals, revision and document-control data; snapshot SHA-256 verified in DB. 🟡 upload to Drive uses the tested upload path |
| **H** Permissions | ✅ DB | Users of project A cannot read or modify project B data (tables or RPC), cannot smuggle B's activity into A's DPR, site engineer cannot edit masters or approve; auditor read-only. Files are served only through RLS-checked attachment rows |
| **I** Approval | ✅ DB | Submit → reject (reason required) → revise → resubmit → approve with accepted qty; approved rows immutable even for DB owner; revision 1 approved while revision 0 stays intact; progress uses latest revision only |
| **J** Recovery | ✅ | Idempotent DPR save replay; Drive upload interrupted mid-chunk resumes from Google's offset; file found by attachment id instead of re-uploaded; DB-write-lost folder recovery |
| **K** Financial integrity | ✅ DB | Plain BOQ rate update blocked; authorised revision recorded with reason; approved snapshot checksum unchanged; exact decimal amounts |
| **L** Browser compatibility | 🟡 | Rendered in Chromium (Linux) at phone size without runtime errors. ⬜ Chrome on Android, Windows, macOS |

## Manual test script (after deployment)

1. Sign up two users (A: you, B: colleague). A: *Load demo project*. A: Setup → Members → add B as *Site engineer*.
2. B on a phone: New DPR → add A2010, 10 MT, take a photo, voice note "erected ten metric tonnes at the skip bridge with 20 workers" → confirm → Submit.
3. A on a laptop (Approvals page open): the report appears without refresh (Test B). Approve with accepted 9 MT. Publish PDF to Drive. Open the PDF in Drive and check 9 MT (Test G).
4. B: airplane mode → new DPR for next day → save → reconnect → status turns *Synchronised*, only one DPR exists (Test E).
5. Create user C not in the project: C sees no projects; opening a DPR URL returns "not accessible" (Test H).
