# Backup, restore, retention and deletion

## What to back up

| Data | Where | How |
|---|---|---|
| All structured records | PostgreSQL | Supabase Pro: automatic daily backups / PITR. Self-hosted: `pg_dump` nightly (below) |
| Files | Google Drive | Drive keeps version history and trash (30 days). For real backups use Google Vault / Workspace backup or a scheduled `rclone sync` of the project tree to another storage |
| Secrets | Supabase function secrets | Keep a sealed copy in your password manager |
| Per-project portable copy | Both | *Settings → Export project (JSON)* — includes Drive file IDs and checksums |

## Self-hosted database backup

```bash
# nightly, keep 30 days
pg_dump "$DATABASE_URL" --format=custom --no-owner --file=backup/cdpr_$(date +%F).dump
find backup -name 'cdpr_*.dump' -mtime +30 -delete
```

### Restore

```bash
createdb cdpr_restore
pg_restore --no-owner -d cdpr_restore backup/cdpr_2026-10-28.dump
# verify, then switch the application's DATABASE_URL / repoint the stack
```

Test a restore **at least quarterly**. Verify approved-report integrity after a restore:

```sql
select dpr_no, revision, snapshot_sha256 = public.sha256_hex(snapshot::text) as intact
from report_revisions order by approved_at desc;
```

## Retention policy (default — adapt to contract and law)

| Record | Retention | Notes |
|---|---|---|
| Approved DPRs, snapshots, approvals, audit log, BOQ revisions | Life of project + contractual defect-liability period + statutory period | Immutable; never deleted by the app |
| Drafts never submitted | Can be deleted by their author before submission | Server copies remain in audit |
| Evidence files | As approved records | Users set `retention_status = deletion_requested`; an administrator reviews, removes the Drive file, and sets `deleted` (the metadata row and checksum stay for traceability) |
| Personal profile data | While the user has access + audit period | Deactivate membership instead of deleting users referenced by records |
| Device data | Until synchronised | Settings → *Clear synchronised data* |

## Deletion workflow for files

1. Authorised user: *Documents → Request deletion*.
2. Project administrator reviews (legal hold? referenced by an approved report?).
3. Administrator deletes or moves the file to `15_Archive` in Drive and updates `retention_status` to `deleted` or `archived` (SQL or table editor). The attachment row is kept with its SHA-256.
