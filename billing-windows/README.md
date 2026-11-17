# BF#3 Billing Database (Windows, C# + SQL)

A Windows desktop app for contractor billing on the BF#3 project. It is built from the "BF#3 Billing – app preview" design: Dashboard, Bills, Bill editor, BOM progress and the first-start screen.

* **C# / .NET 8, Windows Forms.**
* **SQL database:** one SQLite file, `%LOCALAPPDATA%\Bf3Billing\billing.db`. Nothing to install and no server. Back it up by copying the file, or use **Data › Back up now**.
* Indian number formats (1,23,45,678.00, ₹ 29.43 L) and dd-MM-yyyy dates.

## Get it

GitHub Actions builds the app on every push that changes `billing-windows/` (workflow **BF#3 Billing (Windows)**). Download the artifact **Bf3Billing-win-x64**, unzip it and run `Bf3Billing.exe`. It is self-contained, so .NET does not need to be installed.

To build it yourself on Windows with the .NET 8 SDK:

```bat
cd billing-windows
dotnet test tests\Bf3Billing.Tests
dotnet run --project src\Bf3Billing.App
```

## What is in a new data file

| Loaded | Not loaded |
|---|---|
| Activities: Fabrication, Erection, Painting and Supply / transport (billed in tonnes against the BOM), plus Dismantling, Utility and Other | **The BOM.** Import it from Excel: **Data › Import BOM…** |
| Contractors: G.R ENTERPRISES, M.D. ESTHAPAN, SURAJ ENGINEERING WORKS, CHOUDHARY CONSTRUCTION, RAZZAVUDDIN | Bills (none until you enter or import them) |
| The 14 work orders and their values | Rates (enter them in **Masters › Rates**) |
| Defaults: GST 18 %, retention 0 %, TDS 0 % | |

To try the app with made-up bills, use **Data › Open demo data file**. It opens a separate file (`billing-demo.db`) with 10 BF#3 drawings and the six sample bills from the design. Your own data file is not changed.

## Screens

| Tab | What it does |
|---|---|
| **Dashboard** | Totals for bills entered, gross, net payable, paid and outstanding. A **Needs attention** list (lines above BOM scope, draft bills, bills submitted but not certified, certified bills awaiting payment). BOM progress by activity, a summary by contractor, and work order utilisation. Before the first bill it shows the **No bills yet** card instead. |
| **Bills** | Search; filter by status, contractor, work order, date range and "over BOM scope". Export to Excel, print or save as PDF, and **+ New bill** (Ctrl+N anywhere). Double-click a bill to open it. |
| **Bill editor** | Work order (fills in the contractor and the next RA number), bill number, type, dates, period, status, GST/retention/TDS %, other deduction, paid amount and date, and remarks. **+ BOM line** picks one or more drawings or items from the BOM. **+ Free line** is for anything else. Each BOM line shows *scope · earlier · this · % billed* and is flagged **OVER SCOPE** when the total billed goes past the BOM tonnes. You can still save it. Totals: gross, + GST, − retention, − TDS, − other, net payable, paid, balance due. Also Delete, Duplicate (as the next RA number, Draft), and Print PDF. Ctrl+S saves. |
| **BOM progress** | Tonnes billed per BOM activity at system, drawing or item level, with a %. Rows over scope are shown in red. Double-click a row to see the bill lines behind it. |
| **Work orders / Contractors** | Add, edit and delete; value, gross billed, balance and % used. Validates GSTIN and PAN length. |
| **Reports** | Contractor-wise, work order-wise and activity-wise abstracts, bill register, payment register, outstanding bills, all bill lines, and lines above BOM scope. Each can be filtered by bill date, exported to Excel, or printed / saved as PDF. |
| **Masters** | Activities (and whether each is billed against the BOM), rates (per work order, or a default for all), and defaults for new bills. When you choose an activity on a bill line, the rate is filled in. |
| **Data** | Data file location and counts. Back up, restore (a safety copy of your data is taken first), open, new and demo data files. Import from Excel and save the Excel templates. Export everything. |

**Print PDF** opens a print preview. Choose **Microsoft Print to PDF** to save a PDF.

## Excel import

**BOM.** The importer reads every sheet that has a drawing column (`Drawing no`, `DRG NO`, `Drg No` …) and a weight column, either in tonnes (`Tonnes`, `Wt (MT)`, `Weight (t)` …) or in kg (`Weight (kg)`, `Total weight` …, divided by 1000). It also uses `System`, `Sheet`, `Rev` and `Item`/`Mark` when they are there. Without a System column, the sheet name is the system. The header row is found anywhere in the first 15 rows. Sheet numbers that Excel turned into dates (2/3 → 02-Mar) are turned back. Total rows are skipped. Rows that already exist get their tonnes updated. To replace a revised BOM, use **Delete unused BOM rows** first.

**Bills.** One row per bill line with the columns *Work order, Bill no, Type, Bill date, Status, GST %, Retention %, TDS %, Other deduction, Paid, Paid on, System, Drawing no, Sheet, Rev, Item, Description, Activity, Unit, Qty, Rate, Remarks*. A row with a drawing becomes a BOM line; a row without one becomes a free line. A bill that already exists is skipped, so importing the same file twice is safe. The **Bill lines** sheet of **Export everything** has the same layout.

**Data › Save Excel templates** writes both layouts with an example row.

## How the numbers are worked out

* Line amount = qty × rate, rounded to paise.
* Gross = sum of line amounts. GST is added on gross. Retention and TDS are deducted as a % of gross. The other deduction is a fixed amount.
* Net payable = gross + GST − retention − TDS − other. Due = net − paid.
* The BOM scope is checked per BOM row **and** per activity: erection of a drawing and painting of the same drawing each have the full BOM tonnes as their scope. A line is over scope when the cumulative quantity, taking the bills in date order, goes above the BOM tonnes.
* Cancelled bills stay in the list but are left out of every total, progress figure and scope check.

## Project layout

```
billing-windows/
  Bf3Billing.sln
  src/Bf3Billing.Core/   data file (SQLite + Dapper), bill maths, scope checks, dashboard, reports, Excel import/export (ClosedXML)
  src/Bf3Billing.App/    Windows Forms screens, printing, and `Bf3Billing.exe --selftest`
  tests/Bf3Billing.Tests xUnit tests; the demo data must reproduce the design's figures
```

`Bf3Billing.exe --selftest [log file]` opens every screen and every bill on a temporary demo data file and renders the printouts. CI runs it on Windows.
