# The sync server (for the Android app)

Office PCs work directly on the SQL Server database. Phones don't: they connect to the **sync server**, a small C# program (ASP.NET Core) that checks every sign-in and every permission, then reads and writes the same database. It also tells phones about changes within seconds, including changes made on the office PCs.

## Install on the office PC

1. Unzip **CONSTRUCT-DPR-server-windows.zip** to `C:\ConstructDpr\Server`. It is self-contained: no .NET installation needed.
2. Edit `appsettings.json`:

| Setting | Meaning | Example |
|---|---|---|
| `Urls` | Address and port it listens on | `http://0.0.0.0:5080` |
| `Database:Provider` | `SqlServer` (shares data with the office PCs) or `Sqlite` | `SqlServer` |
| `ConnectionStrings:Default` | The same database the Windows app uses | `Server=.\\SQLEXPRESS;Database=ConstructDpr;Trusted_Connection=True;TrustServerCertificate=True` |
| `Features:AdminPortal` | `true` lets people create projects and members from the server too (normally done in the Windows app) | `false` |
| `Features:Registration` | `false` stops new accounts from phones | `true` |
| `Site:TimeZone` | "Today" for reports and the CPM data date | `Asia/Kolkata` |

3. Test it by double-clicking **ConstructDpr.Server.exe**, then opening `http://localhost:5080` in a browser. Allow it through Windows Firewall on *private networks* (TCP 5080).

### Run it as a Windows service (starts with the PC, no window)
Open **Command Prompt as administrator**:
```bat
sc create ConstructDprSync binPath= "C:\ConstructDpr\Server\ConstructDpr.Server.exe" start= auto DisplayName= "CONSTRUCT DPR sync server"
sc start ConstructDprSync
```
The service runs as *Local System*. With `Trusted_Connection=True`, give the login `NT AUTHORITY\SYSTEM` access to the ConstructDpr database in SQL Server Management Studio (*Security → Logins → NT AUTHORITY\SYSTEM → User Mapping → ConstructDpr → db_owner*), or use a SQL login in the connection string (`User Id=constructdpr;Password=…`).

## Phones outside the office network

Inside the office Wi-Fi, `http://OFFICE-PC:5080` is enough. From other sites or mobile data, the server must be reachable over the internet, and then only with **HTTPS**:
* the simplest is the company **VPN** on the phones; or
* publish it through IIS or a reverse proxy with a certificate (IT), e.g. `https://dpr.yourcompany.com`; or
* run it on a cloud Windows/Linux VM next to Azure SQL / SQL Server.

## Linux

**CONSTRUCT-DPR-server-linux.zip** needs the .NET 10 ASP.NET Core runtime: `dotnet ConstructDpr.Server.dll`. The settings are the same.

## Backups
Everything, photos included, is in the SQL Server database: back it up with SQL Server Management Studio (*right-click ConstructDpr → Tasks → Back Up…*) or a scheduled job. The server itself holds no data. Its sign-in keys are stored in the database as well, so phones stay signed in after a restart.

## Android signing (installing updates over older versions)

Builds without signing secrets are signed with a temporary key, so you must uninstall the old app before installing a new one. To keep one signature, create a keystore once (`keytool -genkeypair -v -keystore dpr.keystore -alias dpr -keyalg RSA -keysize 2048 -validity 10000`) and add these GitHub repository secrets: `ANDROID_KEYSTORE_BASE64` (the file, base64-encoded), `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` and `ANDROID_KEY_PASSWORD`. Keep the keystore safe.
