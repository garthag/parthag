# Start here — set up CONSTRUCT DPR without coding

About 30–45 minutes, all in a web browser. You need: a GitHub login (you have one), an e-mail address, and an Android phone with Chrome.

| Part | What | Where | Cost |
|---|---|---|---|
| A | Database and logins | supabase.com | Free plan |
| B | Put the app online | netlify.com | Free plan |
| C | Use it on your phone | Chrome on Android | Free |
| D | Photo storage in Google Drive (later) | Google Cloud + Supabase | Free to start |

Until Part D is done everything works except uploading photos/videos/voice notes: they wait safely on the phone ("Waiting to upload").

---

## Part A — Supabase (database)

**A1. Create the project**
1. Open <https://supabase.com> → **Start your project** → **Continue with GitHub**.
2. **New project**: Name `construct-dpr` · Database password: click *Generate*, then save it in your password manager · Region: **Mumbai (South Asia)** or the one nearest your sites · Plan: Free.
3. Click **Create new project** and wait 1–2 minutes until it says the project is ready.

**A2. Create the tables (part 1 of 2)**
1. On GitHub open `supabase/setup/part1.sql` in this repository (branch `claude/construct-dpr-app-z3h0j8`).
2. Click the **Copy raw file** button (two overlapping squares, top right of the file).
3. In Supabase, left menu → **SQL Editor** → **New query** → paste → **Run** (or Ctrl+Enter).
4. If a box warns about "destructive operations", click **Run this query**. It only creates new tables.
5. Expected result: **Success. No rows returned.**

**A3. Part 2 of 2** — same steps with `supabase/setup/part2.sql`, in a **new query**. Expected: Success.

**A4. E-mail sign-up for the pilot**
Left menu → **Authentication** → **Sign In / Providers** → **Email** → turn **off** "Confirm email" → Save.
*(Supabase's built-in mail service sends only a few e-mails per hour. For the pilot it is simpler without confirmation. Turn it back on with your own SMTP mail server before going live.)*

**A5. Copy two values** (keep them in a note for Part B)
* **Project URL** — *Project Settings → Data API* (or the green **Connect** button): looks like `https://abcdefgh.supabase.co`
* **Public key** — *Project Settings → API Keys*: the **publishable** key (`sb_publishable_…`), or under *Legacy API keys* the **anon public** key.

> Never copy the **secret** / **service_role** key anywhere. It unlocks everything.

---

## Part B — Netlify (put the app online)

**B1.** Open <https://www.netlify.com> → **Sign up** → **GitHub**.

**B2.** **Add new project** (or *Add new site*) → **Import an existing project** → **GitHub** → allow access → choose the repository **parthag**.

**B3.** On the settings page:
* **Branch to deploy**: `claude/construct-dpr-app-z3h0j8`
* Base directory, build command and publish directory fill in automatically from `netlify.toml`. Leave them as they are.
* **Environment variables → Add variables**:

| Key | Value |
|---|---|
| `VITE_SUPABASE_URL` | the Project URL from A5 |
| `VITE_SUPABASE_ANON_KEY` | the public key from A5 |
| `VITE_DRIVE_ENABLED` | `false` (until Part D) |

**B4.** Click **Deploy**. After 1–3 minutes you get an address like `https://random-name-123.netlify.app`.
Optional: *Site configuration → Change site name* → e.g. `yourcompany-dpr` → `https://yourcompany-dpr.netlify.app`.

**B5. Tell Supabase the app's address**
Supabase → **Authentication → URL Configuration** → **Site URL** = your Netlify address → Save. Under **Redirect URLs** add the same address followed by `/**`.

---

## Part C — Use it

1. Open your Netlify address in **Chrome on your phone**.
2. **Create account** → name, e-mail, password (8+ characters) → then **Sign in**.
3. On Home press **Load demo project**. A sample blast-furnace project with a week of approved reports appears.
4. Install it: Chrome menu **⋮ → Install app** (or *Add to Home screen*).
5. Try it out:
   - **＋ Today's DPR** → Activities → search "skip" → enter a quantity.
   - Try **Type or paste a site statement** with the sample sentence.
   - Fill the other steps → **Submit for review**.
   - Open **Approvals** → approve it.
   - Download the PDF.
6. **Add colleagues:**
   - Ask them to open the address and create an account.
   - Then go to **Projects → Setup → Members & roles**, enter their e-mail, choose a role (e.g. Site engineer) and click **Add member**.
7. **Your own real project:** **Projects → ＋ New project**. Create your organisation, then fill in the project and add areas, WBS, activities (or import a CSV) and the BOQ.

### If something goes wrong

| You see | Do this |
|---|---|
| "Setup required" | The Netlify variables are missing or misspelt → Netlify *Site configuration → Environment variables* → fix → *Deploys → Trigger deploy* |
| Sign-in error "Invalid API key" | The key in `VITE_SUPABASE_ANON_KEY` is wrong (copied the secret key, or cut off) |
| "Email not confirmed" | Step A4 not done, or confirm the e-mail from your inbox |
| Error when running part 2 | Part 1 did not finish. Run part 1 again in a new query, then part 2 |
| Project "paused" | Free Supabase projects pause after 1 week without use → *Restore* in the dashboard |

---

## Android and Windows apps

Install the Android app (`.apk`) and the Windows program (`.exe`) from the repository's **Releases** page and connect them with the code from **Settings → Connect other devices**: see [APPS.md](APPS.md). Browser install on Windows: [WINDOWS.md](WINDOWS.md).

## Part D — Google Drive for photos (next session)

This needs a Google Cloud project, a dedicated "document controller" Google account and a small server function. It is explained in [GOOGLE_SETUP.md](GOOGLE_SETUP.md). After it, set `VITE_DRIVE_ENABLED` to `true` in Netlify and redeploy.
