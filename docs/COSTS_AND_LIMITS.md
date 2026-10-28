# Costs, quotas and limits — what is free and what is not

Nothing here is "free forever". This is an honest map as of late 2026; check each provider's current pricing page before committing.

| Component | Free option | When you will pay | Limits that matter |
|---|---|---|---|
| CONSTRUCT DPR code | Apache-2.0, free | never | — |
| Supabase (cloud) | Free tier: small database, limited egress, projects pause after ~1 week of inactivity | Pro plan per project per month for production use, larger DB, no pausing, daily backups | Database size, egress, Realtime concurrent connections, Edge Function invocations |
| Supabase (self-hosted) | Software is free and open source | Your server/VPS (≈ a small 2 vCPU / 4 GB VM), domain, TLS, your time for updates and backups | You operate backups, security patches and monitoring |
| Google Drive (document-controller account) | 15 GB shared across Gmail/Drive/Photos | Google One or Workspace storage once photos/videos grow (video fills space quickly) | Drive API per-user rate limits; 750 GB/day upload cap per user; files ≤ 5 TB |
| Google Workspace + Shared Drive (option B) | — | Per-user Workspace licence | Shared Drive limits (e.g. 400k items) |
| Google Cloud project / Drive API | Free to enable; Drive API calls are not billed | — | Per-minute quotas; the function retries 429/5xx with backoff |
| Google sign-in | Free | — | Consent screen must be published (else 7-day token expiry) |
| Chrome speech-to-text | Free to use | — | Requires internet; audio processed by Google; language support varies |
| Static hosting | Free tiers on Netlify / Cloudflare Pages / GitHub Pages | Traffic beyond free tier | — |
| Domain + TLS | Let's Encrypt is free | Domain registration (~yearly) | — |

## App-level limits (configurable)

| Limit | Default | Where |
|---|---|---|
| Photo size | 25 MB | `supabase/functions/drive/core.ts` `MAX_BYTES` |
| Video size / recording length | 500 MB / 60 s | `MAX_BYTES`, `web/src/lib/media.ts` `LIMITS` |
| Voice note | 50 MB / 300 s | same |
| Documents / drawings | 100 MB / 200 MB | same |
| Absolute file limit | 1 GB | `attachments_size_limit` check |
| Upload chunk | 4 MB | `CHUNK_SIZE` (multiple of 256 KiB) |
| Photos embedded in a PDF | 24 | `DprView.tsx` |
| Function rate limit | 240 requests / user / minute / instance | `index.ts` (add a gateway limit for production) |

## Practical sizing guide

* A DPR with 10 activities and 5 photos ≈ 20 KB of database rows + ~10–20 MB of photos in Drive.
* 1 project × 1 report/day × 1 year ≈ 7 MB database, 4–8 GB Drive (photos) — **video multiplies this quickly**.
* Offer "Compress" for photos to cut size ~5–10× when originals are not required.
