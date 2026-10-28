# Security, privacy and quality

## Controls implemented

| Requirement | Implementation |
|---|---|
| Secure authentication | Supabase Auth (JWT, refresh rotation); Google OIDC or e-mail/password/magic link |
| Least privilege | 13 roles × 12 modules × 8 actions matrix; per-project overrides; workflow tables writable only via validated functions |
| Project isolation | RLS on **every** table using `has_perm(project_id, module, action)`; tested (Test H) |
| Server-side validation | All DPR arithmetic, quantities, overruns, stock, approvals, self-approval policy, versioning in PostgreSQL functions and constraints |
| Output escaping | React escapes all rendered text; no `dangerouslySetInnerHTML`; PDF text sanitised to printable Latin-1 |
| Rate limiting | Supabase API gateway limits + per-user limit in the `drive` function; add a reverse-proxy limit when self-hosting |
| CSRF | Not cookie-authenticated: API calls carry a bearer JWT in a header, so cross-site form posts cannot act as the user |
| Sessions | Supabase stores the refresh token in browser storage (standard for SPAs). Google credentials are **never** on the device. Use HTTPS only; short JWT expiry (1 h) |
| File validation | MIME allow-list per category and size limits checked on the device, again in the function, and by a DB check constraint; SHA-256 recorded; uploads streamed to Drive, never stored as Base64 in rows |
| Safe file names | `safeFileName()` strips path and control characters; attachment ID in the name prevents collisions |
| Secrets | Function secrets via `supabase secrets`; `.env` files are git-ignored; `.env.example` contains no secrets |
| Audit logging | `audit_events` (append-only) on every master, transaction and workflow table; approvals in `approval_actions` |
| Immutability | Approved DPRs, their rows, report snapshots, baselines and BOQ revisions cannot be updated (triggers), even by the database owner |
| Error monitoring without personal data | Function logs status + message only — no tokens, no file contents, no e-mails |
| Dependency checks | `npm audit` in CI; all runtime dependencies MIT/ISC/Apache-2.0 |
| Headers | `web/nginx.conf`: CSP, `X-Frame-Options DENY`, `nosniff`, `Permissions-Policy` limiting camera/mic/GPS to the app |

## Privacy by design

* Camera, microphone and location are requested **only** when the user taps the matching button; tracks are stopped when the panel closes. No background location, no hidden recording, no fingerprinting, no analytics.
* Each capture is reviewed (caption, activity, retake/discard) before queueing; nothing uploads without the user adding it to the report.
* A privacy panel explains what is captured, where it is stored and who can see it.
* Speech-to-text is opt-in per recording with a disclosure that Chrome uses Google's online service.
* Manual location entry is always available.

## Known limitations / residual risks

* **Local device data is not encrypted by the app.** IndexedDB is protected by the OS account and browser profile. Encrypting it in JavaScript would need a key that also lives on the device, which gives little real protection. Use device encryption and a screen lock (both standard on Android). Sign out on shared devices.
* Supabase anon key is public by design; security relies on RLS (tested).
* A malicious *authorised* user can enter false data; the audit trail, separation of preparer and approver, and accepted-vs-reported quantities mitigate this.
* Self-hosted operators must patch PostgreSQL/Supabase and the OS.

Report vulnerabilities privately to the project maintainers.
