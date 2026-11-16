# BSL Project Control (C# + SQL Server Windows app)

A Windows desktop app for the BSL BF#3 revamp. It keeps the **drawing register**, **drawing master**, **standardised BOM**, **contractor master and work orders**, **internal and external rates**, the **BOQ** and **RA bills** in one SQL Server database. Every screen has a table view and a form view, user-defined fields with formulas, Excel import and export, and PDF reports.

It is built from an analysis of `BSL-MASTER_BOM-5.xlsx` (21 BOM sheets) and `Contractor_Master_BSL_2026_1.xlsx` (contractor master, BOQ with RA columns, drawing scope).

---

## 1. What the analysis found

### BOM workbook: 21 sheets in about 8 different layouts

| Problem in the source | What the app does |
|---|---|
| The header row is on row 2 in some sheets and row 3 in others. The weight has a "UNIT / TOTAL" sub-header in some sheets and not in others. | The header row is detected from known column names, and the sub-header is resolved. |
| The same column has different names: `DRG NUMBER` / `DRG NO` / `Drawing No.` / `Drg No`, `MARK NO` / `ASSEMBLY MARK NO` / `MARKNO.`, `NO OF PCS.` / `Qty` / `TOTAL QTY`, `Thickness(MM)` / `Thikness` / `Thk` … | A table of header aliases maps every source column to one standard field. The `COLUMN_MAPPING` sheet shows how each column was mapped. |
| Weights are in **MT** in STOVE NOZZLES and in kg everywhere else. The "unit weight" column holds kg/pc, kg/m or a density (7.85 / 78.5), depending on the sheet. | All weights are stored in kg. *Unit Wt (kg/pc) = Total / Pcs* in every sheet. The original unit column is kept as "Unit (as in sheet)". |
| The sheet and revision are written into the drawing number: `ERC/MCK/BSL/STH/F-001_R3 (SH. 1OF 1)`, `…-4028-R1, Sht-2/3-B`, `16282205R0`, `ERC MCK BSL  STH F-004 (SHT.1 OF 1)R1` | These are split into **base drawing no + sheet + revision**. Revisions become `R0`, `R1` …, and spaces are changed to `/` for the ERC series. |
| Excel turned 154 sheet numbers ("2/3") into **dates** (02-Mar-2026). | They are changed back to `2/3` and flagged. |
| 120 quantities look like `9/12`, which means piece 9 of 12. | Each is read as 1 piece; the original text is kept in "Qty (text)". |
| There are **three assembly patterns**: (a) an assembly-mark row followed by part rows listed for **one** assembly (SCRUB&DEMIST, CRUDE GAS STR, where B1 qty 2 = 2 × its parts); (b) item `37` followed by `37a`, `37b` … (HOT BLAST); (c) OCR errors such as `34l` → `341` and `41o` → `410`. | The assembly row is counted. Its detail rows are kept for cutting lists but excluded from totals. If an assembly row has no weight, its parts are counted instead. A parts × qty against assembly check is flagged. |
| Set multipliers are hidden in total formulas: `SUBTOTAL(…)/1000*3` (3 stoves), `=O44*4` (4 sets). | They are stored as **Package.NoOfSets** and shown as weight per set and weight for all sets. |
| Total rows are typed into the data, using SUM, SUBTOTAL, constants and selective sums. | Total rows are removed. Totals are recalculated, and each sheet is **reconciled** against its own total. |
| Material grade typos: `15:2002` (should be IS 2002), `152002`, `IS2062`, `Gr .- 2` | These are standardised (`IS 2002 Gr.2`). The original is kept in its own column and the change is flagged. |
| The same section is written in many ways: `PL THK 25`, `PLATE 16 THK. x …`, `PL.398x10`, `PLT16*230`, `ISMC 150`, `Pipe Ø60.3x5.54` | A **Section (Std)** column is added: `PL-25`, `ISMC150`, `PIPE OD60.3X5.54`, `FLANGE DN600`, `BOLT M20` … |
| The STOVE HOUSE list of drawings has no items under it. Its last rows have item text in the Drawing and Sheet columns. | The drawing-index rows go to the drawing register only. Descriptions found in the drawing column are moved to Item Description and flagged. |

**Reconciliation with each sheet's own total.** 15 of the 21 sheets match exactly; CLEAN GAS SUPP STR has no total to compare. The other five:

| Sheet | Standardised | Sheet says | Why |
|---|---|---|---|
| STOVE HOUSE | 427.361 MT | 425.929 MT | `SUM(P4:P5971)` leaves out the last 3 rows (5972–5974, **1.43 MT**). |
| SCRUB&DEMIST | 104.612 MT | 108.844 MT | The total is a typed constant, not a formula. Check it. |
| Back Draft Line | 28.611 MT | 26.012 MT | 3 rows have a unit weight but a blank total. They are calculated and flagged. |
| PRESSURE AND RELIEF DUCT | 15.710 MT | 15.545 MT | The total formula adds only some of the rows. |
| SDD of HBM Outside SH | 31.999 MT | 31.458 MT | Rows outside the summed range. |
| BACK DRAFT STACK / Back Draft Line | – | – | The **same drawing (…-4026, sheet 1/5)** appears in both sheets, so it may be counted twice. |

### Contractor workbook

* `Contractor_Master` has one row **per work order**, so the same firm is repeated. The app splits it into **Contractor** (one row per firm, code GRE/MDE/SEW/CHC …) and **Work Order** (BM01…BM14).
* Phone numbers are stored as decimals (`9334240112.0`) and dates as text (`02.11.2023`). The BM12 value is a formula (`3931235*30%+3931235`). All of these are converted.
* Work order BM01 uses the contractor code **MCK**, which is also the main contractor's code in the drawing numbers. This is flagged; give the firm its own code.
* The BOQ sheet ("Sheet2") holds both rates. **Supply Unit Rate is the external rate** (client to MCK) and **Agency Rate is the internal rate** (MCK to agency). Its 42 RA columns are turned into **RA Bill + RA Bill Line** rows. The RA labels in row 1 are 5 columns out of line with row 2; row 2 is used.
* 63 BOQ items have RA quantities that add up to more than the R1 scope. The sheet's own `BALANCE` formula adds the RA columns together. If your RA columns are cumulative quantities to date and not quantities per bill, tell me and the import can take the last RA value instead.
* `Unique_No` sometimes holds `BSL`, `SN`, `NA` or `RAJU SHEETING`, which are not work orders. These are kept as an "Agency Ref". Units `nr`, `NR`, `nr `, `set ` are standardised. The units `ott` and `nov` are flagged.
* None of the 83 rows in the DRAWING scope sheet has a drawing number. They are imported as `SCOPE-<row>` placeholders with the status "Scope (no drg no.)", so you can fill in the numbers later.

---

## 2. Framework

```
┌──────────────────── BSL.Desktop (WinForms, .NET 8) ─────────────────────┐
│ MainForm: menu, navigation, tabs, dashboard                             │
│ EntityBrowser (one generic screen for every master):                    │
│   TABLE view (grid) ⇄ FORM view (grouped fields + related records)      │
│   search · advanced filter · column chooser · New/Save/Delete           │
│   Excel export/import/template · PDF list (grouped) · PDF record card   │
│   User defined fields · Formula builder · "calculate column by formula" │
│ ImportWizard (BOM / contractor workbook) · Reports · Settings           │
└───────────────────────────────┬─────────────────────────────────────────┘
┌──────────────────── BSL.Core (.NET 8 class library) ────────────────────┐
│ Metadata/EntityCatalog  – fields, captions, lookups, groups, children   │
│ Data/EntityRepository   – generic load (views) / save (tables) + UDFs   │
│ Formula/FormulaEngine   – live formulas (DataColumn expressions)        │
│ Excel/BomWorkbookReader, Standardizer, StandardBomWriter,               │
│       ContractorWorkbookReader, TableExcel (ClosedXML)                  │
│ Import/ImportService (bulk copy), TableImporter                         │
│ Reports/PdfReport (MigraDoc), ReportCatalog                             │
└───────────────────────────────┬─────────────────────────────────────────┘
┌──────────────────── SQL Server (Express / LocalDB / full) ──────────────┐
│ Contractor ─< WorkOrder ─< ContractItem (BOQ: ext + int rate) ─< RaBillLine >─ RaBill
│ WorkOrder ─< RateMaster (internal / external rate per item)             │
│ Package ─< DrawingMaster ─< DrawingRegister (sheet / rev / transmittal) │
│ Package ─< BomItem >─ DrawingMaster                                     │
│ UdfDefinition ─< UdfValue  ·  LookupValue  ·  ImportBatch  ·  AuditLog  │
│ vw_* views add joined contractor / WO / progress / weight columns       │
└─────────────────────────────────────────────────────────────────────────┘
```

Every entity is described once in `EntityCatalog.cs` (field, caption, type, lookup, drop-down list, group, related records). The grid, the form, Excel, PDF and import are all generated from that description. To add a column, add it to the table, the view and one line in the catalog.

### Screens

The screens are grouped like an ERP; the menu bar and the navigation use the same numbers:

| Module | Screens |
|---|---|
| 1. Masters | Contractor Master, Activity Master, Contractor Activities, Work Orders |
| 2. Contract & billing | WO Activity Rates (internal / external), Contract BOQ Items, RA Bills, RA Bill Lines |
| 3. Engineering – drawings & BOM | Packages / BOM Sheets, Drawing Master, Drawing Register, BOM Items |
| 4. Documents | Notes Register, Uploaded Files & Drawings |
| 5. Reports | 21 standard reports, PDF / Excel |
| 6. Import / Export | BOM workbook, contractor workbook |
| 7. Settings | Company, logo & database; user defined fields; drop-down lists; import history |

**House style.**
* All screens, menus, grids, dialogs, PDF and Excel output use **Arial 10** in **capitals**.
* Text is typed in capitals and stored in capitals. E-mail, password, server names and formulas keep their case.
* Dialogs shrink to fit smaller screens.
* Toolbars wrap onto a second line instead of hiding buttons, and buttons grow to fit their text.

**Notes and attachments on every record.**
* Every form view has a **NOTES** tab and an **ATTACHMENTS** tab.
* **Notes** have a date, a category (general, instruction, hindrance, query, decision, follow-up), an important flag and the user's name.
* **Attachments**: upload, drag and drop, open, save a copy and delete. Files are stored in the database (up to 100 MB each), so every user sees them and they are included in database backups.
* **Drawings.** Drawing Master and Drawing Register have:
  * **UPLOAD DRAWING**, which stores the file as type DRAWING with the drawing's revision.
  * **BULK UPLOAD DRAWINGS**: choose many files at once. Each is matched to its drawing by the number in the file name, e.g. `ERC_SMS_TSSS_F-006 R1.pdf` → drawing ERC_SMS_TSSS_F-006, revision R1. Files that don't match are listed for you to rename.
* File and note counts appear on the drawing screens.
* Reports: *Drawings without uploaded file*, *Uploaded files register*, *Notes register*.

**Company logo.**
* Set it under *7. Settings → Company, logo & database*: company name, an address / project line and the logo. Any PNG / JPG works; it is saved as a standard PNG.
* PDF reports: every page header has a logo box on the left, then company name and address, with the report title on the right. Without a logo, the space is kept blank.
* Excel exports carry the logo at the top right; the dashboard shows it top right.

**Form view related records:**

| Record | Related records shown |
|---|---|
| Contractor | Work orders, drawings originated, sheets issued |
| Work order | BOQ items, RA bills, rates, packages |
| Drawing | Register sheets and revisions, BOM lines |
| RA bill | RA bill lines |

`Open / edit in new tab` opens the related records filtered to the current record. New rows added there are linked to it automatically.

### Activities: linking contractors, work orders and rates

* **Activity Master** lists the work done: `DIS-EQP` dismantling, `ERE-EQP` erection of equipment, `ERE-HYD` hydraulics, `ERE-UTL` utility piping, `MOD-FAB` modification / fabrication, `SUP-SRV` supply & service, `FAB-STR`, `ERE-STR`, `ERE-DUCT`. Each has a unit and default internal / external rates. You can add your own.
* **Contractor Activities** records which contractor is approved for which activity, with an optional quoted rate and capacity.
* **WO Activity Rates** holds the internal (paid to agency) and external (billed to client) rate for each work order and activity.
* Work orders, BOQ items, packages and drawings each have an **Activity** field.
* **Auto-link activities** (dashboard button, Tools menu; also runs after every import) fills blanks only:

  | What | How it is filled |
  |---|---|
  | Work order → activity | From its work category |
  | BOQ item → activity | From its work order |
  | Contractor ↔ activity | From the work orders the contractor holds |
  | Work-order activity rate | Weighted average (by R1 weight) of its BOQ item rates |
  | Activity default rates | Weighted average over the whole BOQ |
  | Package → activity | From its name (ducts / mains, structures, stove shell) |
  | Drawing → activity | From its BOQ item or package |

  On the imported data this gave 14 work orders, 428 BOQ items, 11 contractor links, 11 work-order rates, 21 packages and 548 drawings. Running it again changes nothing.
* **Apply activity rates to BOQ** fills blank item rates from the work-order activity rate, or else the activity default. On the imported data it would fill 297 internal and 69 external rates; it only changes data when you press it.
* **While you type:**

  | Screen | What is filled in |
  |---|---|
  | New work order | Next number (BM15 …) |
  | Work order | Choosing an activity fills service type and work category |
  | BOQ item | Choosing a work order fills its activity and rates |
  | WO activity rate | Choosing an activity fills description, unit and default rates |

  Only blank fields are filled.
* **Margins are blank when a rate is missing.** They used to count a missing rate as zero, which showed ₹16.24 Cr; the real margin on items with both rates is ₹6.09 Cr.
* **Quick create:** the dashboard and File menu have *New contractor*, *New work order*, *New activity*, *Link contractor to activity* and *New WO activity rate*. Each opens the form on a new record.
* **Reports (Activities):**
  * Activity summary
  * Contractor – activity matrix
  * Work order activity rates
  * BOQ items to check: no activity, a missing rate, external rate below internal, or an agency not approved for the activity

### User-defined fields and formulas

* **Fields & formulas ▸ User defined fields** adds a field to any screen. The types are Text, Number, Date, Yes/No, List (drop-down) and **Formula**.
* A formula can use **any column of the record**: contractor details from the joined view (`ContractorCode`, `ContractorName`, `GSTIN`, `PAN` …), `InternalRate`, `ExternalRate`, weights, progress, and other user fields. Formulas recalculate live while you type.
  ```
  ([ExternalRate] - [InternalRate]) * [R1WeightTon]                 margin
  IIF([ExternalRate] > 0, ([ExternalRate]-[InternalRate])*100/[ExternalRate], 0)
  [ContractorCode] + ' / ' + ISNULL([WorkOrderNo], 'No WO')         text
  IIF([ThicknessMm] > 0, [LengthMm]*[WidthMm]*[ThicknessMm]*ISNULL([Qty],1)*7.85/1000000, 0)
  ```
* **Calculate a column with a formula** writes the result of a formula into a normal stored column (all rows, filtered rows or selected rows, with an optional condition). For example, it can fill the total weight of plates from L × W × T. Then press Save.
* Example user fields are seeded: margin %, balance amount, agency tag, calculated plate weight, weight difference, fabrication status and WO balance.

### Excel

* **Import / Export ▸ Import BOM workbook**: analyse, review the reconciliation and issues, then **Save standardised Excel** and/or **Import into database**. The standardised workbook has the same 33 columns in every sheet, plus the sheets README, SUMMARY, ALL_BOM, DRAWING_REGISTER, DATA_ISSUES and COLUMN_MAPPING.
* **Import / Export ▸ Import contractor workbook**: contractors, work orders, BOQ, RA bills and drawing scope.
* On every screen, the **Excel** menu has: export all or filtered rows (formatted, with totals and filters), **import from Excel** (columns are matched by caption, and lookups accept codes such as `GRE`, `BM05` or a drawing number), and **download template**.

### PDF reports

Each screen has **PDF ▸ List report**, grouped by any column with subtotals and a grand total, and **PDF ▸ Record card**, which prints the form view with signature boxes.

The **Reports** screen has 14 standard reports. You can filter each one by package, agency or text, and export it to PDF or Excel:

* Package-wise weight (sets applied)
* Section-wise weight
* Material grade-wise weight
* Drawing-wise BOM weight
* Drawing register
* Drawings pending
* BOM data-quality flags
* Contractor list
* Work order summary
* BOQ progress
* Internal vs external rate margin
* Rate master
* RA bill abstract
* RA bill summary

---

## 3. Install and run

1. **SQL Server.** SQL Server **2017 or later**. Any of these will do: **LocalDB** (installed with Visual Studio), **SQL Server Express** (free) or a full server.
2. **Install the app.** Either:
   * **`BSLProjectControl-Setup-1.1.0.exe`** (recommended): installs to *Program Files* with Start-menu and desktop shortcuts and an entry in *Add/Remove Programs*. The uninstaller keeps your database and settings.
   * **`BSLProjectControl-1.1.0-portable.zip`**: unzip anywhere and run `BSLProjectControl.exe`. No installation needed.

   Both are self-contained: .NET does **not** need to be installed. The GitHub Actions job *Desktop (C# / SQL Server)* builds both on every push (artifact `bsl-project-control-windows`).
   To build them yourself, open `desktop/BSL.ProjectControl.sln` in Visual Studio 2022 (.NET 8), publish `BSL.Desktop` (win-x64, self-contained, single file) to `desktop/publish/release`, then run `makensis BSLProjectControl.nsi` in `desktop/installer` (NSIS 3).
3. **First start.** The default connection is `(localdb)\MSSQLLocalDB`, database `BSL_ProjectControl`. If that fails, the Settings window opens; enter the server (for example `.\SQLEXPRESS`) and press **Test / create DB**. The app creates the database, tables, views and seed data by itself. The scripts in `desktop/database/*.sql` are the same and can also be run by hand.
4. **Load your data.**
   1. Import the contractor workbook.
   2. Import the BOM workbook.
   3. Check *Reports ▸ BOM data-quality flags*.

**Keyboard:**

| Key | Action |
|---|---|
| Ctrl+S | Save |
| Ctrl+N | New |
| F2 | Switch between table and form view |
| F5 | Refresh |
| Ctrl+F | Search |
| Ctrl+W | Close tab |
| Delete | Delete the selected rows |
| Ctrl+U | User-defined fields |
| Ctrl+K | Calculate a column with a formula |
| Ctrl+P | PDF list report |
| Ctrl+E | Export filtered rows to Excel |

**Settings file and support:** `%AppData%\BSLProjectControl\settings.json` holds the connection, report header and `HighDpiMode` (`SystemAware` by default; `DpiUnaware` can help on remote desktop / Citrix). Any error is written with full details to `%AppData%\BSLProjectControl\error.log`. PDF reports use Arial (or Segoe UI / Calibri); if a PC has none of these, put the `.ttf` files in a `fonts` folder next to the program.

### Command line (`BSL.Cli`)

```
bsl bom <BSL-MASTER_BOM.xlsx> <out.xlsx>         # standardise all BOM sheets (no database needed)
bsl contractor <Contractor_Master.xlsx>          # read + list issues
bsl import "<connection string>" <bom.xlsx> <contractor.xlsx>
bsl export "<connection string>" <out.xlsx>      # normalised masters to one workbook
bsl dbtest "<connection string>" <folder>        # load every screen, render every report, CRUD + UDF test
bsl selftest                                     # formula engine + standardisation rules
```

### Tested

Tested against **SQL Server 2022**:

* schema, views, procedures and seed data (idempotent, and upgrades an existing database)
* importing both workbooks (15,959 BOM lines, 631 drawings, 577 register entries, 14 WOs, 479 BOQ items, 195 RA lines)
* loading all 12 screens
* all 14 reports rendered to PDF
* insert, update and delete with UDF values
* formulas that use contractor fields and rates
* friendly errors for a duplicate code and for deleting a record that is in use

**UI preview.** The real Windows build (published with Microsoft's official .NET 8 SDK) was run under Wine against the same database, and every module was opened: dashboard, contractors, work orders (table and form view with related records), BOQ, RA bills, packages, drawing master and register, BOM items (15,959 rows), reports with PDF export, the BOM import wizard (analyse and import), user-defined fields with the formula builder, and add / save / delete. Bugs found this way were fixed. Wine cannot open drop-down menus (it lacks a Windows 10 DPI function), so those menus were not clicked; the actions behind them were reached through the keyboard shortcuts above. A final check on a real Windows PC is still recommended.

**Libraries (all free for commercial use):**

| Library | Licence | Used for |
|---|---|---|
| ClosedXML | MIT | Excel |
| PDFsharp-MigraDoc | MIT | PDF |
| Microsoft.Data.SqlClient | MIT | SQL Server access |
