# EDMS Drawing Register

A Windows desktop app (C# .NET 8 + WPF + SQL Server) that signs in to the Wrench EDMS site
(`https://eddms.cetapps.in`), collects every **Smart Folder** and the **documents/drawings** in it,
stores them in SQL Server, downloads the drawing files, and shows them in table form:

| Tab | What it shows |
|---|---|
| **Smart Folders** | The folder tree in the same order as the site, with document counts. Select a folder to see its documents in a table. |
| **Submission Register** | Every revision submitted: scheduled date, vendor submission date, responded date, days to respond, days late, reply code (AAN / KFR / KFRN...), current stage. Pending replies are highlighted. |
| **Revision Register** | Every revision of every drawing, current and superseded, with approval code, dates and the local file. |
| **Published Drawings** | Released drawings (latest released revision), released date, approval code, local file. |
| **Sync History** | Each sync run, counts, and any problems. |

Double-click a row (or **Open drawing**) to open the downloaded PDF. **Export to Excel (CSV)** saves the current table.

## Install

1. Download `EDMS-Drawing-Register-windows.zip` from the repository's **Releases** page
   (or from the latest "EDMS Drawing Register (Windows)" Actions run) and extract it.
2. You need SQL Server on the PC. Either one works:
   * **SQL Server Express LocalDB** (smallest): install "SQL Server Express" from Microsoft, choose
     *Download Media* → *LocalDB*, run `SqlLocalDB.msi`. The default connection string uses this.
   * Any SQL Server / SQL Express, e.g.
     `Server=.\SQLEXPRESS;Database=EddmsSmartFolder;Trusted_Connection=True;TrustServerCertificate=True`
3. Run `EddmsDrawingRegister.exe`, enter your EDMS login name and password, then click **Sign in and Sync**.
   The database and tables are created automatically on first run.

Drawings are saved under `Documents\EDMS Drawings\<Smart Folder path>\` (you can change this on the sign-in screen).
The login name, SQL connection and folder are remembered; **the password is never saved**.

## About revisions

The EDMS account used for testing does not have the *Handle Old Revisions/Versions* permission, so the
site only serves the **latest** revision of each drawing. The app therefore builds the revision history
itself: every sync records the current revision, and when a drawing moves to a new revision the old
revision row (and its downloaded file) stays in the Revision Register. Sync regularly (e.g. daily) to
capture every revision.

## Scheduled sync (optional)

`EddmsSync.exe` (in the same zip) does the same sync from the command line, for Windows Task Scheduler:

```
set EDMS_PASSWORD=your-password
EddmsSync.exe --login "YOUR LOGIN" --download
EddmsSync.exe --login "YOUR LOGIN" --sql "Server=.\SQLEXPRESS;Database=EddmsSmartFolder;Trusted_Connection=True;TrustServerCertificate=True" --download --download-root "D:\EDMS Drawings"
```

## Project layout

```
Database/EddmsSchema.sql      Tables, stored procedures and register views (run automatically)
src/Eddms.Core/               WrenchClient (login, folders, documents, download), EddmsDatabase, SyncService
src/Eddms.Wpf/                The WPF app
src/Eddms.SyncConsole/        Command-line sync + database self-test (--selftest)
```

### SQL you can run yourself

```sql
SELECT IndentedName, DocumentCount FROM dbo.vw_SmartFolderTree ORDER BY OrderKey;
SELECT * FROM dbo.vw_SubmissionRegister ORDER BY Discipline, DocNumber, SheetNumber, InternalRevisionNumber;
SELECT * FROM dbo.vw_RevisionRegister   ORDER BY Discipline, DocNumber, SheetNumber, InternalRevisionNumber DESC;
SELECT * FROM dbo.vw_PublishedRegister  ORDER BY Discipline, DocNumber, SheetNumber;
```

## How it talks to the site

Same requests the browser makes: `AccessControl/Login` (two steps: login name, then password),
`SmartFolder/GetSmartFoldersOnDemand` (tree), `SmartFolder/DisplaySmartFolderPartialView` +
`SmartFolder/GetSmartFolderObjectDetails` (document rows), and `Document/DownloadDocument` +
`FileManager/DownloadFileFromServerTempFolder` (files). It only reads; it never changes anything on EDMS.
