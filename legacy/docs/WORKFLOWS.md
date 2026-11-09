# User workflows

## Daily reporting (site engineer, on a phone)

```mermaid
flowchart TD
  A[Open app → Home] --> B[+ Today's DPR]
  B --> C[1 Report: date, shift, area, weather]
  C --> D[2 Activities: search / favourites / copy last report / voice]
  D --> E{Quantity beyond approved?}
  E -- yes --> E1[Correct, or record authorised overrun + reason]
  E -- no --> F[3 Manpower → 4 Equipment → 5 Materials]
  E1 --> F
  F --> G[6 Safety, QA/QC, constraints, actions]
  G --> H[7 Narrative & tomorrow's plan]
  H --> I[8 Review: errors block, warnings need acknowledgement]
  I --> J{Online?}
  J -- no --> K[Saved locally → queued; syncs automatically]
  J -- yes --> L[Save to server → Submit for review]
  K --> L
  D -. photos / video / voice .-> M[Evidence: review, caption, link to activity → upload queue]
```

## Voice entry

```mermaid
flowchart LR
  V[Tap Voice note] --> P[Explain + Allow microphone]
  P --> R[Record; optional speech-to-text with disclosure]
  R --> T[Correct transcript or type instead]
  T --> X[Convert to DPR entry]
  X --> C[Confirmation dialog: activity, qty, unit, location, shift, workers, equipment<br/>uncertain fields flagged]
  C -- user confirms --> A[Lines added to the draft]
  C -- cancel --> N[Nothing added]
```

## Review and approval

```mermaid
stateDiagram-v2
  [*] --> draft
  draft --> submitted: submit_dpr (checks)
  rejected --> submitted: corrected & resubmitted
  submitted --> under_review: start_review
  submitted --> approved: approve
  under_review --> approved: approve (accepted qty, not own report)
  submitted --> rejected: reject (reason required)
  under_review --> rejected: reject
  approved --> [*]
  approved --> draft_revision: start_dpr_revision (reason) — new row, rev+1
  draft_revision --> submitted
```

On approval the database: freezes accepted quantities, updates activity actual dates/status, writes an immutable JSON snapshot with SHA-256 into `report_revisions`. The PDF is built from that snapshot and published to Drive (`04_DPR/YYYY/MM/Approved PDFs`), and its file ID and checksum are attached to the revision once.

## Offline synchronisation

```mermaid
flowchart TD
  S[Edit] --> L[IndexedDB draft: Saved locally]
  L --> Q[Outbox item + new idempotency key: Waiting to synchronise]
  Q --> O{Online & signed in?}
  O -- no --> Q
  O -- yes --> R[save_dpr with expected server version]
  R -- ok --> Y[Synchronised]
  R -- network error --> B[Backoff 2 s … 5 min] --> Q
  R -- same key seen --> Y
  R -- version mismatch --> C[Conflict requires review]
  C --> K1[Keep mine → overwrite with new version] --> Q
  C --> K2[Use server → discard local]
  R -- validation / permission --> F[Sync failed — fix and retry]
  Y --> M[Media queue: register row → drive function → chunked upload → In Drive]
```

## Planning (planning engineer)

1. Setup → WBS, areas, activities (or CSV import), calendar and holidays.
2. Schedule → add FS/SS/FF/SF links with lags → review CPM (dates, floats, critical path).
3. *Apply dates to plan* (version-checked) → *Freeze baseline* → mark as approved baseline.
4. Daily: actuals arrive from approved DPRs; Schedule shows forecast vs baseline variance; Dashboards show S-curve.
5. Re-baseline only by freezing a new revision — the old one stays.
