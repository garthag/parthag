# Start here: set up CONSTRUCT DPR

Choose the set-up that fits your site. You can move to a bigger one later.

| Set-up | For | You install |
|---|---|---|
| **1. One PC** | One planner or engineer | The Windows app. Nothing else |
| **2. One PC + phones** (recommended for a site office) | A site office PC and engineers or supervisors with Android phones | The Windows app on the PC, then the Android app on the phones |
| **3. Office network** | Several PCs in an office, plus phones | SQL Server Express on one PC, then the Windows app on every PC and the Android app on the phones |

All downloads are on the repository's **Releases** page:
* **CONSTRUCT-DPR-windows-setup.exe**: the Windows app. If *"Windows protected your PC"* appears, click **More info → Run anyway**; the files are not code-signed yet.
* **CONSTRUCT-DPR-android.apk**: the Android app. Open it on the phone, allow installing from this source, then tap **Install**.

---

## 1. One PC

Install **CONSTRUCT-DPR-windows-setup.exe** and start it. It opens with a demo project, works offline, and keeps everything in one file on this PC. To start your real job: **Projects & people → ＋ New project**, then add activities (or import them from Excel under **Reports & Excel**).

## 2. One PC + phones

1. Do step 1 on the site office PC.
2. In the Windows app: **Settings → Phones → tick "Let phones connect to this PC"**. When Windows Firewall asks, allow **private networks**. Settings now shows the address for the phones, e.g. `http://SITE-PC:5080` or `http://192.168.1.20:5080`.
3. On each phone (on the **same Wi-Fi** as the PC): install the APK, open it, enter that address, tap **Create an account instead**, and enter name, e-mail and password.
4. Back on the PC: **Projects & people** → select the project → enter the person's e-mail → choose the role (e.g. *Site engineer*, *Supervisor*) → **Add / change role**.
5. The phone now shows the project. Supervisors report from the field (quantities, crews, photos); the engineer reviews and approves on the PC or a phone. Everything appears everywhere within a few seconds.

The PC must stay on, with the Windows app open, while the phones are in use. Back up the data under **Settings → Back up database…**.

## 3. Office network (Microsoft SQL Server)

1. On the office PC or server, install **SQL Server Express** (free) and allow network connections. The steps are in [WINDOWS_APP.md](WINDOWS_APP.md), part *Office SQL Server: set-up*.
2. On every office PC, install the Windows app → **Settings → Office SQL Server** → enter the server (e.g. `OFFICE-PC\SQLEXPRESS`) and the database `ConstructDpr`. The first person creates an account and the database; colleagues create their own accounts.
3. For phones, either tick **"Let phones connect to this PC"** on one office PC that stays on (it serves the same SQL Server database), or install the standalone sync server as a Windows service: [SYNC_SERVER.md](SYNC_SERVER.md).

## Everyday use

| Who | Does |
|---|---|
| Supervisor / site engineer (phone) | **＋ Today's daily report**: yesterday's activities are already listed, so type today's quantities, crews and equipment hours, add photos (📷), then **Submit** |
| Planning / project engineer (PC) | **Daily reports** (the menu shows *● n to approve*): check, adjust the *Accepted* quantity if needed, **Approve** or **Reject** with a reason |
| Planner | **Activities & BOQ** and **Schedule**: activities, predecessors, CPM, baselines |
| Project manager | **Dashboard**; **Reports & Excel → Progress report** for the week or month (PDF or Excel) |
| Administrator | **Projects & people**: people, roles, **Reset password…** for anyone who forgot theirs |

## If something goes wrong

| You see | Do this |
|---|---|
| Phone: "Cannot reach the CONSTRUCT DPR server" | Same Wi-Fi as the PC? Windows app open with "Let phones connect" ticked? Try the IP address instead of the PC name. Allow the app in Windows Firewall (private networks) |
| Phone: "No connection" while saving | Your changes are kept on the phone. Tap Save or Submit again when you have signal |
| Phone: no project after signing in | The administrator still has to add your e-mail under Projects & people |
| "Enter today's quantity for at least one activity…" | Type at least one quantity, or describe the work done under *Site & narrative* |
| "…was changed on another device" | Someone saved the same record first. Reload (F5 on the PC, ↻ on the phone), then make your change again |
| "Wrong e-mail or password" | Passwords are case-sensitive. After 8 wrong tries the account is locked for 10 minutes; an administrator can **Reset password…** |
| Windows app: "Cannot open the SQL Server database" | See [WINDOWS_APP.md](WINDOWS_APP.md) → Troubleshooting |
| Android: "App not installed" when updating | Uninstall the old app first (see [SYNC_SERVER.md](SYNC_SERVER.md) → Android signing) |
