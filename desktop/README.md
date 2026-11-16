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

| Module | Screens |
|---|---|
| Masters | Contractor Master, Work Orders |
| Contract | Rate Master (internal / external), Contract BOQ Items, RA Bills, RA Bill Lines |
| Drawings & BOM | Packages / BOM Sheets, Drawing Master, Drawing Register, BOM Items |
| Settings | Drop-down Lists, Import History |

**Form view related records:**

| Record | Related records shown |
|---|---|
| Contractor | Work orders, drawings originated, sheets issued |
| Work order | BOQ items, RA bills, rates, packages |
| Drawing | Register sheets and revisions, BOM lines |
| RA bill | RA bill lines |

`Open / edit in new tab` opens the related records filtered to the current record. New rows added there are linked to it automatically.

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

1. **SQL Server.** Any of these will do: **LocalDB** (installed with Visual Studio), **SQL Server Express** (free) or a full server.
2. **Build.** Open `desktop/BSL.ProjectControl.sln` in Visual Studio 2022 (.NET 8 SDK) and run **BSL.Desktop**. Or download the ready-made zip from the GitHub Actions run *Desktop (C# / SQL Server)* (artifact `bsl-project-control-windows`). It is self-contained, so .NET does not need to be installed.
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

* schema, views and seed data (idempotent)
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
