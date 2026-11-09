# Google Cloud, OAuth and Google Drive setup

CONSTRUCT DPR uses Google for two separate things:

| Purpose | Who signs in | Credential | Scope |
|---|---|---|---|
| **Sign in with Google** (optional; e-mail login also works) | every user | Supabase Auth Google provider | `openid email profile` |
| **File storage in Drive** | nobody interactively — a server function | one *document-controller* Google account (refresh token) **or** a service account + Shared Drive | `https://www.googleapis.com/auth/drive.file` |

## Why one server-held Drive credential (and not each user's token)?

`drive.file` is the narrowest Drive scope: the app can see **only files and folders it created itself**. If each user uploaded with their own token, user B's app could not see the project folders created by user A, so a shared project tree would split into per-user copies. Requesting full `drive` access to avoid this would let the app read *everything* in every user's Drive, which we refuse to do.

Instead the `drive` edge function holds one credential, creates and owns the whole project tree, and checks the **database** permission of the calling user on every upload or download. Results:
* one clean folder tree per project, created idempotently;
* no Google tokens or secrets on phones;
* project roles control in-app access; Drive sharing remains a separate deliberate step.

## Folder structure created per project

```
<Organisation>/
  <PROJECT CODE> - <Project name>/
    01_Contracts/  02_Drawings/  03_Baseline_and_Schedules/
    04_DPR/<YYYY>/<MM>/{Daily Reports, Photographs, Videos, Voice Notes, Approved PDFs}
    05_Manpower/  06_Equipment/  07_Materials/  08_QA_QC/  09_Safety/
    10_Inspection_and_Test_Records/  11_Fabrication_and_Dispatch/
    12_Billing_and_BOQ/  13_Commissioning/  14_Client_Correspondence/  15_Archive/
```

Folders carry `appProperties` (`cdprProjectId`, `cdprPath`) and are registered in `drive_folders` with a unique (project, path) key; retries and concurrent devices converge on one folder and any losing duplicate is trashed. Files carry `cdprAttachmentId` so an interrupted upload is found and reconciled instead of re-uploaded.

## Step by step

### 1. Google Cloud project
1. Go to <https://console.cloud.google.com/> → create a project (e.g. `construct-dpr`).
2. *APIs & Services → Library* → enable **Google Drive API**.
3. *APIs & Services → OAuth consent screen* → User type *External* (or *Internal* on Workspace) → app name, support e-mail, authorised domain (your app's domain and `supabase.co` if using Supabase Cloud).
   Add scopes: `openid`, `email`, `profile`, `.../auth/drive.file`. `drive.file` is a non-sensitive scope.
4. **Publish the app** (*Publishing status → In production*). While the app is in *Testing*, Google expires refresh tokens after 7 days, which would stop uploads.

### 2. Sign in with Google (optional)
1. *Credentials → Create credentials → OAuth client ID → Web application*.
2. Authorised redirect URI: `https://<your-project>.supabase.co/auth/v1/callback` (self-hosted: `https://<your-api-domain>/auth/v1/callback`).
3. Supabase dashboard → *Authentication → Providers → Google* → paste client ID and secret → enable.
4. *Authentication → URL configuration*: site URL = your app URL.

### 3. Drive credential — option A: document-controller account (free Gmail or Workspace)
1. Create or choose a dedicated account, e.g. `dpr-documents@yourcompany.com`. Its Drive quota holds the files (15 GB free; buy Google One / Workspace storage for more).
2. Create a second OAuth client (*Web application*) with redirect URI `https://developers.google.com/oauthplayground`.
3. Open <https://developers.google.com/oauthplayground> → ⚙ → *Use your own OAuth credentials* → paste this client ID/secret.
4. Step 1: enter scope `https://www.googleapis.com/auth/drive.file` → *Authorize APIs* → sign in **as the document-controller account**.
5. Step 2: *Exchange authorization code for tokens* → copy the **refresh token**.
6. Set secrets (never in the web app):
   ```bash
   supabase secrets set GOOGLE_CLIENT_ID=... GOOGLE_CLIENT_SECRET=... GOOGLE_REFRESH_TOKEN=... \
     ALLOWED_ORIGINS=https://dpr.yourcompany.com
   ```
   Optionally create a top folder in that Drive and set `DRIVE_ROOT_FOLDER_ID` — note that with `drive.file` the app can only use a folder **it created**, so let the app create it (leave empty) unless you created it through the app.

### 4. Drive credential — option B: service account + Shared Drive (Workspace)
1. *IAM → Service accounts → Create*; create a JSON key.
2. In Google Drive create a **Shared Drive** (service accounts have no own storage quota) and add the service account's e-mail as *Content manager*.
3. `supabase secrets set GOOGLE_SERVICE_ACCOUNT_JSON="$(cat key.json)" GOOGLE_SHARED_DRIVE_ID=<id from the URL>`.
4. Workspace admins can then manage sharing and retention with organisational policies (recommended for companies).

### 5. Deploy the function
```bash
supabase functions deploy drive
```
Then in the app: *Documents → Create / verify folder tree*.

## Security implications to understand

| Topic | Implication | Recommendation |
|---|---|---|
| Refresh token / service-account key | Anyone holding it can act on files the app created | Store only as function secrets; rotate on staff change; restrict who can read Supabase secrets |
| `drive.file` scope | Limits blast radius to app-created files | Never widen to `drive` without a documented need |
| Sharing folders with users | Gives Drive-level access independent of the app's roles | Share the project root only with people who must work in Drive; remove on exit; prefer Shared Drive membership |
| Public links ("anyone with the link") | Uncontrolled distribution of site photos | Do not use; Workspace admins can disable it |
| Third-party transcription | Chrome's speech recognition streams audio to Google | Opt-in per recording, disclosed in the UI; leave off for sensitive content |
| Device metadata | Timestamps/GPS/EXIF can be wrong or altered | Treated as reported data only; SHA-256 is recorded for integrity of the uploaded bytes |

## Optional Google Sheets export

Every register has CSV export, and *Settings → Export project (JSON)* exports everything. Import CSVs into Google Sheets (*File → Import*) for ad-hoc analysis. Sheets is not used as a database — see [SHEETS_PROTOTYPE.md](SHEETS_PROTOTYPE.md).
