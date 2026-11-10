# Start here: set up CONSTRUCT DPR without coding

This takes about 30 minutes, all in a web browser. When you finish, everyone on site can use CONSTRUCT DPR on a phone, a Windows PC or a web browser, and their data stays in sync.

| Step | What | Where | Cost |
|---|---|---|---|
| A | Database | supabase.com (you may already have this project) | Free plan |
| B | Server, which also serves the web app | render.com | Free plan |
| C | Use it in the browser | Chrome or Edge | Free |
| D | Android app | `.apk` from the Releases page | Free |
| E | Windows app | `.exe` from the Releases page | Free |

> **Only Windows PCs, in one office?** You don't need any of this: install the Windows app and use one **Microsoft SQL Server** database on the office network — see [WINDOWS_APP.md](WINDOWS_APP.md). **Only one PC?** Install the Windows app (step E) and use it offline.

---

## A. Database (Supabase PostgreSQL)

If you created a Supabase project earlier, reuse it. CONSTRUCT DPR keeps its tables in its own section (schema `cdpr`), so nothing that is already there is touched.

1. Open <https://supabase.com> and sign in. If you have no project yet: **New project**, name `construct-dpr`, then click **Generate** for the database password and **save it** in your password manager. Pick the region nearest your sites (e.g. Mumbai), then **Create**.
2. In the project, click the green **Connect** button at the top.
3. Choose **Session pooler** and copy the **URI**. It looks like:
   `postgresql://postgres.abcdefgh:[YOUR-PASSWORD]@aws-0-ap-south-1.pooler.supabase.com:5432/postgres`
4. In a note, replace `[YOUR-PASSWORD]` with your database password. If the password contains `@`, `#` or `/`, reset it in Supabase (*Project Settings → Database → Reset password*) to one made only of letters and numbers.

This complete line is your **DATABASE_URL**. Keep it private: it gives full access to the data.

## B. Server (Render)

1. Open <https://render.com> → **Get started** → **GitHub**, and allow access to the repository **parthag**.
2. **New ＋ → Blueprint** → choose **parthag**. Render finds `render.yaml`.
3. When it asks for **DATABASE_URL**, paste the line from step A4. Click **Apply** / **Deploy Blueprint**.
4. The first build takes 5–10 minutes. When it says **Live**, your address appears at the top, e.g. `https://construct-dpr.onrender.com`. This is your **server address**; write it down.

> **Free plan:** the server goes to sleep after 15 minutes without use. The first visit afterwards takes up to a minute to wake it. A paid plan (about US$7/month) keeps it always on.

## C. Use it in the browser

1. Open your server address in Chrome or Edge, on a PC or a phone.
2. Click **Create an account** and enter your name, e-mail and a password (8+ characters).
3. Click **Load demo project**. A blast-furnace project appears with a schedule, a week of approved reports and one report waiting for your approval.
4. To install it as an app: Chrome menu **⋮ → Install app** (or *Add to Home screen*); in Edge, **⋯ → Apps → Install**.

Try these:
* **Approvals → Review** → change an *Accepted* quantity → **Approve**. Keep the dashboard open on another device and watch it update.
* **＋ Today's DPR** → add an activity → enter today's quantity → **Submit for review**.
* **Schedule** → the Gantt chart; **PDF (A3)** downloads the schedule report.

## D. Android app

1. On the phone, open the repository's **Releases** page → **CONSTRUCT DPR … (C# / .NET)** → **CONSTRUCT-DPR-android.apk**.
2. Open the downloaded file, allow installing from this source, then tap **Install**.
3. Start the app, enter your **server address**, tap **Connect**, then sign in.

Reports you download open in the share sheet, so you can save them to Drive or send them on WhatsApp or by e-mail.

## E. Windows app

1. Download **CONSTRUCT-DPR-windows-setup.exe** from the same Releases page and run it (or use **CONSTRUCT-DPR-windows.exe** without installing). If *"Windows protected your PC"* appears, click **More info → Run anyway**: the files are not code-signed yet.
2. It starts in **This PC only** mode, offline with a demo project, which is useful for planners working without internet.
3. To sync with everyone else: **Settings → Cloud server**, then enter the server address and sign in. (Office-only teams can use **Office SQL Server** instead: [WINDOWS_APP.md](WINDOWS_APP.md).)

## F. Colleagues and real projects

Creating projects and managing people is part of the **admin portal**, which is switched off for now. Until then, each person can sign up and load their own demo project.

To switch it on: Render → your service → **Environment** → set `Features__AdminPortal` to `true` → **Save** (Render restarts the server). After that:
* **Projects → ＋ New project** creates your real project, with you as its administrator.
* Each colleague first creates an account. You then add them in **Settings → People on this project** with their role (site engineer, supervisor, planning engineer, client reviewer and so on). The project appears on their devices immediately.

## If something goes wrong

| You see | Do this |
|---|---|
| Render build fails or the server keeps restarting | Render → **Logs**. "password authentication failed" means DATABASE_URL has the wrong password; "Name or service not known" means use the **Session pooler** URI from Supabase |
| "Cannot reach the CONSTRUCT DPR server" | Check the address (it must start with `https://`). On the free plan, wait a minute for it to wake and try again |
| "Wrong e-mail or password" | Passwords are case-sensitive. After 8 wrong tries the account is locked for 10 minutes |
| "…was changed on another device" | Someone saved the same report or activity at the same time. Reload, then make your change again; nothing is lost silently |
| Android: "App not installed" after a new version | Uninstall the old app first. Builds are only installable over each other when the release is signed with your own key; see SELF_HOSTING.md |

More options, including running the server on a Windows PC in the site office or with Docker: [SELF_HOSTING.md](SELF_HOSTING.md).
