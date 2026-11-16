# Drawing Register – Windows app (C# + SQL Server)

A Windows desktop app that **unzips drawing packages**, files every drawing **Plant → Unit / Area → Drawing type → Drawing no.**, keeps every **revision**, and shows the drawings in a built-in **viewer**. The register is stored in **SQL Server** (LocalDB, Express or a shared server). The drawing files are kept in a **library folder** laid out in the same order.

## What it does

| | |
|---|---|
| **Import ZIP** | Toolbar **Import ZIP…** (Ctrl+I), **Add files…**, or drag ZIPs / PDFs / DWGs onto the window. ZIPs inside ZIPs are opened. System junk (`__MACOSX`, `Thumbs.db`, `.dwl`, `~$…`) is skipped. Files already in the register (same drawing, revision and file type) are skipped. |
| **Plant → Unit → Type** | Read automatically from folder names and drawing numbers (see the table below). A plant name you enter is used when the ZIP doesn't name one. |
| **Ordered by type** | Fixed register order: **GN** General / Plot plan, **PR** Process, **CV** Civil, **ST** Structural, **AR** Architectural, **ME** Mechanical / Equipment, **PP** Piping, **EL** Electrical, **IN** Instrumentation & Control, **HV** HVAC, **FP** Fire protection, **PL** Plumbing / Utilities, **OT** Other. Drawing numbers sort naturally (`-2` before `-10`). |
| **Revisions** | All revisions are kept. The highest is current: lettered revisions (A, B …, for review / approval) rank below numbered ones (0, 1, 2 …, for construction). **Show superseded** lists older revisions under each drawing. |
| **Register screen** | Left: a Plant → Unit → Type tree with counts. Middle: the register grid (S.No, Drawing No., Title, Type, Rev, Status, Earlier revs, Unit, Plant, Sheet kind, Received, File). Search (Ctrl+F) and filter by type. |
| **Viewer** | PDFs and images open in the Microsoft Edge (WebView2) viewer: zoom, pan, rotate, thumbnails, search and print. Pick any revision from the list. A red **SUPERSEDED** banner shows on old revisions, with a link to the current one. DWG/DXF files open in AutoCAD / DWG TrueView (double-click or **Open**). |
| **Edit** | F2 edits the drawing number, title, revision, status (IFR / IFA / IFC / As-built / FI), type, plant, unit, sheet kind and remarks. The change can apply to all revisions of the drawing. The file is moved to its new Plant \ Unit \ Type folder. |
| **Export** | **Drawing register PDF**: A4 landscape, grouped Plant → Unit → Type, with a unit × type summary table. **CSV for Excel**: every file, current and superseded. |

### How plant, unit, type and revision are recognised

| Level | Read from | Examples |
|---|---|---|
| Plant | `Plant-…` / `PLT…` folder, `PLANT2` code, a plain parent folder, else the name entered on import | `Plant-2\…`, `Refinery\CDU\…` |
| Unit / Area | `Unit-…`, `Area-…`, `Block-…` folder, `U200` code, area code before the discipline, or a plain folder | `Unit 300\…`, `RF-100-ST-…`, `U200-EL-…`, `CDU\…` |
| Type | discipline folder, discipline code in the number, or keywords | `02_Structural\…`, `-ST-`, `-EL-`, `P&ID`, `SLD`, `ISO`, `Foundation` |
| Revision / status | `R2`, `Rev B`, `REV-0`, `(IFC)`, `IFA`, `As-Built` | `RF-100-ST-GA-0001_R2.pdf` |

Anything not recognised goes under **OT – Other**. The import summary lists these files so they can be fixed with **Edit**.

### Library folder layout

```
Drawing Register Library\
  Plant 1\
    Unit 100\
      02 PR - Process\RF-100-PR-PID-0001_R2 Crude Charge P&ID.pdf
      04 ST - Structural\RF-100-ST-GA-0001_R0 Pipe Rack GA.pdf
      04 ST - Structural\RF-100-ST-GA-0001_R1 Pipe Rack GA.pdf
      07 PP - Piping\ISO-100-1023_R0.pdf
    Unit 200\
      08 EL - Electrical\U200-EL-SLD-001-R0 Main SLD (IFC).pdf
  Plant 2\
    CDU\
      03 CV - Civil\RF-CDU-CV-DRN-001_R0 Storm Drainage.dwg
```

## Install and run

1. Download **`DrawingRegister-windows-x64.zip`** from the latest run of the **Drawing Register (Windows, C# + SQL Server)** workflow (Actions tab → run → Artifacts) and extract it.
2. Install a SQL Server if the PC has none. Either one works:
   * **SQL Server Express LocalDB**, for a single PC. It's an option in the SQL Server Express installer, and it's included with Visual Studio.
   * **SQL Server Express** or a full SQL Server, for a team sharing one register.
3. Run **`DrawingRegister.exe`**. On first start it creates the `DrawingRegister` database and its tables by itself. If it can't connect, the Settings window opens so you can fix the connection.
4. **Settings…** has the project name (printed on the register), the SQL Server connection string and the library folder.

| Setup | Connection string | Library folder |
|---|---|---|
| One PC | `Server=(localdb)\MSSQLLocalDB;Database=DrawingRegister;Integrated Security=true;TrustServerCertificate=true` (default) | `Documents\Drawing Register Library` (default) |
| Team | `Server=SERVERNAME\SQLEXPRESS;Database=DrawingRegister;Integrated Security=true;TrustServerCertificate=true` | a shared folder, e.g. `\\SERVERNAME\Drawings` |

The PDF viewer uses the Microsoft Edge WebView2 Runtime, which comes with Windows 10/11. Without it, images still show and PDFs open in the default PDF program.

Settings and an `error.log` are kept in `%APPDATA%\DrawingRegister`.

## Database

`database/DrawingRegister.sql` (it's also built into the app) creates:

| Object | Purpose |
|---|---|
| `dbo.Drawing` | One row per drawing file (revision): plant, unit, type, number, title, revision, revision rank, status, file and library path, remarks, who and when |
| `dbo.DrawingType` | The 13 drawing types with their register order and colour |
| `dbo.ImportBatch` | One row per import: source ZIPs, default plant, who, and the added / duplicate / error counts |
| `dbo.vw_DrawingRegister` | Every revision, with `IsCurrent = 1` on the latest revision of each drawing. Use it for reports in Excel, Power BI or SSRS. |
| `dbo.vw_DrawingSummary` | Count of current drawings per plant / unit / type |

The script is safe to run again, and the app runs it on every start-up so the schema stays up to date.

## Build from source

```
windows-drawing-register\
  DrawingRegister.sln
  database\DrawingRegister.sql
  src\DrawingRegister.Core\   parser, import (unzip), library folder, SQL Server store, PDF/CSV export  (net8.0)
  src\DrawingRegister.App\    WinForms UI and WebView2 viewer                                          (net8.0-windows)
  tests\DrawingRegister.Tests\ xUnit tests (set DR_TEST_SQL to a connection string to include the SQL Server test)
```

```bash
dotnet test windows-drawing-register/tests/DrawingRegister.Tests
dotnet publish windows-drawing-register/src/DrawingRegister.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The parser follows the same rules as the Drawings tab in the Site DPR phone app, so a ZIP is filed the same way in both.
