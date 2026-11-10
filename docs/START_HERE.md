# Start here: set up CONSTRUCT DPR in your office

You need one Windows PC (or server) in the office that stays switched on. Everything else is the Windows app on office PCs and the Android app on phones.

| Step | What | Time | Cost |
|---|---|---|---|
| A | Microsoft SQL Server Express on the office PC | 15 min | Free |
| B | Windows app on every office PC | 2 min per PC | Free |
| C | Sync server on the office PC (only if phones are used) | 5 min | Free |
| D | Android app on the phones | 2 min per phone | Free |

> **Just trying it?** Install the Windows app (step B) and use it as it opens: **This PC only**, offline, with a demo project. No SQL Server needed.

---

## A. SQL Server Express (on the office PC)

1. Download **SQL Server Express** from Microsoft's website → run it → **Basic** → Accept → Install. Note the instance name, normally `SQLEXPRESS`.
2. Start **SQL Server Configuration Manager** → *SQL Server Network Configuration → Protocols for SQLEXPRESS* → **TCP/IP → Enable**. Under *SQL Server Services*, restart **SQL Server (SQLEXPRESS)** and start **SQL Server Browser** (set it to *Automatic*).
3. Windows Defender Firewall: allow inbound **TCP 1433** and **UDP 1434** on the *Private/Domain* network.

Details, logins without a company domain, and backups: [WINDOWS_APP.md](WINDOWS_APP.md).

## B. Windows app (every office PC)

1. Download **CONSTRUCT-DPR-windows-setup.exe** from the repository's **Releases** page and run it. No administrator rights are needed. If *"Windows protected your PC"* appears, click **More info → Run anyway**: the file is not code-signed yet.
2. In the app: **Settings → Office SQL Server**. Enter the server name `OFFICE-PC\SQLEXPRESS` (on the office PC itself: `.\SQLEXPRESS`) and the database `ConstructDpr`.
3. The **first person** clicks *Create an account instead*, then **Connect and create account**. The database and its tables are created automatically.
4. **Projects & people → Load demo project** to explore, or **＋ New project** for your real job.
5. Colleagues connect the same way and create their own account. The project administrator adds them under **Projects & people** with their role.

## C. Sync server (on the office PC, only for phones)

Phones never connect to SQL Server directly, which would not be safe. They talk to this small C# program, which uses the same database.

1. Download **CONSTRUCT-DPR-server-windows.zip**, unzip it to e.g. `C:\ConstructDpr\Server`.
2. Open `appsettings.json` in Notepad and point it at the office database:
   ```json
   "Database": { "Provider": "SqlServer" },
   "ConnectionStrings": { "Default": "Server=.\\SQLEXPRESS;Database=ConstructDpr;Trusted_Connection=True;TrustServerCertificate=True" },
   ```
3. Double-click **ConstructDpr.Server.exe**. When Windows Firewall asks, allow **private networks**. Leave the window open; Windows can also run it as a service, see [SYNC_SERVER.md](SYNC_SERVER.md).
4. Check from a phone's browser: `http://OFFICE-PC:5080` should say *"CONSTRUCT DPR sync server … is running"*.

For phones outside the office network (e.g. on another site), the server needs to be reachable over the internet with HTTPS. Ask IT for a VPN, or see [SYNC_SERVER.md](SYNC_SERVER.md).

## D. Android app (phones)

1. On the phone, open the Releases page → **CONSTRUCT-DPR-android.apk** → open the download → allow installing from this source → **Install**.
2. Start the app. Enter the server address (`http://OFFICE-PC:5080`, or the office PC's IP address such as `http://192.168.1.20:5080`), then sign in with the same account as on the PC, or create one.
3. Report from site: **Home → ＋ Today's daily report** → add activities and today's quantities → **📷 Take photo** → **Submit**. The engineer approves it on the PC or phone, and everyone sees it within seconds.

## If something goes wrong

| You see | Do this |
|---|---|
| Windows app: "Cannot open the SQL Server database" | Check the server name, that SQL Server and SQL Server Browser are running, TCP/IP is enabled, and the firewall (step A) |
| Phone: "Cannot reach the CONSTRUCT DPR server" | Phone and office PC on the same Wi-Fi? Sync server window open? Try the IP address instead of the PC name |
| "Wrong e-mail or password" | Passwords are case-sensitive. After 8 wrong tries the account is locked for 10 minutes |
| "…was changed on another device" | Someone saved the same record first. Reload (F5 on PC, ↻ on phone), then make your change again |
| Android: "App not installed" after an update | Uninstall the old app first (see SYNC_SERVER.md → Android signing to avoid this) |
