# Optional Google Sheets prototype mode — limitations

If an organisation insists that **every** record lives in Google Drive, a Sheets-based prototype is possible (one spreadsheet per project, a tab per table, written by Apps Script). This repository does **not** implement it as the main path, for these reasons:

| Need | PostgreSQL (implemented) | Google Sheets prototype |
|---|---|---|
| Several people saving at once | Transactions, row versions, conflict detection | Last write wins; Apps Script locks serialise but are slow and time out (6-min script limit) |
| Project-level security | Row-level security per user and role | Sharing is per *file*: anyone with access sees every tab and row |
| Integrity (no orphan rows, no negative stock, immutable approvals) | Foreign keys, checks, triggers | Not enforceable; anyone with edit access can change approved rows |
| Audit trail | Append-only audit table | Version history only, hard to query |
| Scale | Millions of rows | 10 million cells per spreadsheet; slows long before that |
| Offline + idempotent sync | Built in | Must be hand-built in Apps Script |

**Recommendation:** use Sheets as an *output* — import the CSV exports, or the JSON export, for analysis and sharing — not as the system of record. A minimal Apps Script that pulls `boq_status` or `activity_progress` from the Supabase REST API into a sheet on a timer is a reasonable "lightweight automation" if wanted.
