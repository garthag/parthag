# CONSTRUCT DPR for Windows (C# · WPF · SQL)

The Windows app is a native WPF program. It works in one of three ways; you choose in **Settings → Where your data lives**:

| Mode | Database | For |
|---|---|---|
| **This PC only** | SQLite file in `%LOCALAPPDATA%\ConstructDpr` | One planner or engineer, fully offline, no accounts |
| **Office SQL Server** | One Microsoft SQL Server database on the office network | Everyone in a site or head office: accounts, roles, approvals between people, changes appear on every PC within ~4 seconds |
| **Cloud server** | The CONSTRUCT DPR server (PostgreSQL) | Teams that also use phones (Android app) and browsers |

All three follow exactly the same rules: overrun checks, approvals, locked reports with SHA-256 snapshots, roles and the audit trail.

## Install

From the repository's **Releases** page:
* **CONSTRUCT-DPR-windows-setup.exe** installs it with a Start-menu entry, an optional desktop icon and an uninstaller (Settings → Apps). No administrator rights are needed.
* **CONSTRUCT-DPR-windows.exe** is the same program without installation; just double-click it.

If *"Windows protected your PC"* appears, click **More info → Run anyway**: the files are not code-signed yet. Windows 10 or 11, 64-bit.

## Office SQL Server: set-up (once, about 20 minutes)

You need **one** PC or server that stays switched on in the office. SQL Server **Express** is free (databases up to 10 GB, which is years of daily reports with photos).

### 1. Install SQL Server Express (on the office PC/server)
1. Download **SQL Server 2022 Express** from Microsoft's website → run it → **Basic** → Accept → Install.
2. At the end, note the **instance name**, normally `SQLEXPRESS`. The server name is then `PC-NAME\SQLEXPRESS` (from that PC itself: `.\SQLEXPRESS`).

### 2. Allow the other PCs to connect
1. Start **SQL Server 2022 Configuration Manager** → *SQL Server Network Configuration → Protocols for SQLEXPRESS* → **TCP/IP → Enable**.
2. In the same tool, *SQL Server Services* → restart **SQL Server (SQLEXPRESS)**, and set **SQL Server Browser** to *Automatic* and *Start* it.
3. In Windows Defender Firewall, allow **inbound TCP 1433** and **UDP 1434** on the *Private/Domain* network.

### 3. Choose how people sign in to SQL Server
* **Company domain (Active Directory):** use *Windows sign-in*. Give your staff's domain group access once in SQL Server Management Studio (*Security → Logins → New → the group → User Mapping → ConstructDpr → db_owner*), or let the first administrator create the database and then add the group.
* **No domain (workgroup PCs):** create one SQL login for the app: in SSMS → *Security → Logins → New Login* → *SQL Server authentication* → name `constructdpr` and a strong password → *Server Roles: dbcreator* (it creates its database on the first start). In the app choose **SQL Server login**.

### 4. Connect the app
1. Open **Settings → Office SQL Server**.
2. Enter the server name (e.g. `OFFICE-PC\SQLEXPRESS`) and the database name (`ConstructDpr`), then choose Windows sign-in or the SQL login.
3. The first person clicks **Create an account instead**, enters their name, e-mail and password, then **Connect and create account**. The tables are created automatically.
4. **Projects & people → Load demo project**, or **＋ New project**.
5. Each colleague connects the same way and creates their own account. The project administrator then adds them under **Projects & people** with their role (site engineer, supervisor, planning engineer, client reviewer and so on).

To try it on a single PC first, use the server name `(localdb)\MSSQLLocalDB` (SQL Server Express LocalDB) and Windows sign-in.

### Backups
In SSMS: right-click the **ConstructDpr** database → *Tasks → Back Up…*, or schedule a nightly backup. Photos are inside the database, so one backup holds everything.

## Tips

| Keys | Action |
|---|---|
| **Ctrl+S** | Save the report or activities you are editing |
| **F5** | Reload the screen |
| **Ctrl+1 … Ctrl+7** | Dashboard, Daily reports, Activities, Schedule, Reports, Projects & people, Settings |

* **Photos:** Daily reports → Photos → *Add photos…*. They are shrunk to 1600 px, stored in the database, printed in the PDF and locked once the report is approved. Double-click a photo to open it full size.
* **Two people, one report:** if a colleague saved the same report first, you get a message instead of overwriting their work. Press F5, then make your change again.
* **Moving from "This PC only" to SQL Server:** the offline file stays on your PC. Load the demo or create the project in the office database, and import activities from Excel (Reports & Excel → Import).

## Troubleshooting

| Message | Fix |
|---|---|
| "Cannot open the SQL Server database … network-related or instance-specific error" | Wrong server name, SQL Server not running, TCP/IP not enabled, SQL Server Browser stopped, or the firewall blocks 1433/1434 (steps 2.1–2.3) |
| "Login failed for user" | With Windows sign-in, your Windows account has no access (step 3). With a SQL login, the name or password is wrong |
| "CREATE DATABASE permission denied" | Give the login the *dbcreator* role, or ask IT to create an empty database called ConstructDpr and make the login its owner |
| "Wrong e-mail or password" (CONSTRUCT DPR account) | Passwords are case-sensitive. After 8 wrong tries the account is locked for 10 minutes |
