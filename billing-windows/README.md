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
| **BOM** / **BOQ** | The BOM drawings with their part lines; the BOQ items with R1 scope, billed tonnes, balance and rates (see *BSL workbooks* below). |
| **BOM progress** | Tonnes billed per BOM activity at system, drawing or item level, with a %. Rows over scope are shown in red. Double-click a row to see the bill lines behind it. |
| **Work orders / Contractors** | Add, edit and delete; value, gross billed, balance and % used. Validates GSTIN and PAN length. |
| **Reports** | Contractor-wise, work order-wise and activity-wise abstracts, bill register, payment register, outstanding bills, all bill lines, and lines above BOM scope. Each can be filtered by bill date, exported to Excel, or printed / saved as PDF. |
| **Masters** | Activities (and whether each is billed against the BOM), rates (per work order, or a default for all), added fields, and defaults for new bills. When you choose an activity on a bill line, the rate is filled in. |
| **Data** | Data file location and counts. Back up, restore (a safety copy of your data is taken first), open, new and demo data files. Import from Excel (with column matching and duplicate check), quick imports, Excel templates. Export everything. |

**Print PDF** opens a print preview. Choose **Microsoft Print to PDF** to save a PDF.

## BSL workbooks (master BOM and contractor master)

**Data › Import BSL workbooks…** reads the two project workbooks and loads them as linked records:

```
Contractor ─< Work order ─< BOQ item (R1 scope t, client rate, agency rate)
                  │              │
                  └─< Bill ─< Bill line >─┤  BOQ line → BOQ item
                                          └  BOM line → BOM drawing ─< BOM part lines
```

| From | Becomes |
|---|---|
| **Master BOM**, every sheet (STOVE, STOVE HOUSE, HOT BLAST, SCRUB&DEMIST, SDD …) | One **BOM row per drawing** (system = sheet, drawing no., sheet, rev, title, tonnes, no. of sets), with **all its part lines** under it (assembly / part mark, section, standard section, sizes, qty, unit and total weight, grade, notes). |
| **Contractor_Master** | **Contractors** (one per firm; code, PAN, GSTIN, phone, contact person, e-mail) and their **work orders** (WO no., date, value, service, ref BM01…BM14, work category). |
| **Sheet2 (BOQ)** | **BOQ items** linked to their work order by Unique_No: group, spec item, description, unit, qty, R1 weight as the **scope in tonnes**, Supply Unit Rate as the **client rate**, Agency Rate as the **agency rate**, activity. |
| **Sheet2 RA-1 … RA-42 columns** | **RA bills**: one bill per work order and RA column, one **BOQ line** per item with a quantity, priced at the agency rate. |
| **DRAWING** and **REMARKS** sheets | BOQ items without a work order (scope list and extra items not in the DOW). |

The workbooks are checked before anything is saved:

* **BOM sheets:**
  * The header row is found (row 2 or 3), including the *UNIT / TOTAL* sub-header. Weights in MT are converted to kg.
  * Sheet numbers that Excel turned into dates are turned back. Sheet and revision written inside the drawing number (`ERC_SMS_DSWG_F-0016_1 OF 1 - Rev 0`, `16282205R0`) are split out.
  * Assembly rows and their part rows are recognised, so weights are not counted twice. Grade typos are corrected.
  * Sets in the total formula (×3 stoves) are kept as the no. of sets.
  * Each sheet's own total is compared with the lines, and drawings that appear in two sheets are reported.
* **Contractor master:**
  * Firms that repeat on every work order row become one contractor.
  * Codes that clash with MCK, BSL, ERC or SMS are reported.
  * PAN and GSTIN are checked.
  * BOQ items whose RA quantities add up to more than the R1 scope are reported.
  * RA lines without an agency rate get rate 0, with a note.
* **The RA columns are read as each bill's own quantity, not a running total.** In most items billed more than once, the quantity goes down from one RA to the next, which a running total cannot do.

The check can be saved to Excel. Importing again later (a new BOM revision, more RA columns) updates what is there. RA bills that are already saved are kept as they are.

**What the workbooks do not say, and the import assumes** (change in the app as needed):

* **RA bill dates** are not in the workbook. Each bill gets its work order's date.
* **RA bill status** is set to *Certified*, **paid** is 0, and **GST, retention and TDS** are the defaults in Masters (GST 18 %).
* **BOQ items are not linked to BOM drawings** in the workbooks, so RA quantities count against each BOQ item's R1 scope (BOQ tab), not against the BOM (BOM progress).

The **BOM** tab shows the drawings with their part lines. The **BOQ** tab shows every item's R1 scope, tonnes billed, balance, % billed, client and agency rates; items over scope are shown in red. In the bill editor, **+ BOQ line** adds items of the bill's work order, with the same live scope check as BOM lines.

## Excel import

### Import from Excel (check and match columns)

Use **Data › Import from Excel (check and match columns)…**, or **Import from Excel…** on the first-start screen. It works with untidy workbooks: several sheets, title rows above the table, merged cells, and columns named differently from the app's fields.

1. **Sheets.** Every sheet is read. The heading row is found automatically (you can change it) and the app suggests what the sheet holds: contractors, work orders, BOM or bill lines. Sheets are listed in a sensible order: contractors, then work orders, then BOM, then bills.
2. **Columns.** Each Excel column is fitted to a field by its heading, allowing for different spellings: `Agency Name` → Name, `GST No.` → GSTIN, `W.O. No.` → Work order no., `Drg No` → Drawing no., `Wt (kg)` → Weight (kg). You can change any choice.
   A column that fits no field is marked **⚠**. When you check the data, the app **asks** whether to add these columns as **new fields**. Yes adds a field for each and keeps the data there; No leaves them out; Cancel lets you choose a field for each yourself.
3. **Check.** Nothing is saved yet. Every row is cleaned (amounts like `₹ 12,50,000/-`, dates like `02.11.2025`, extra spaces) and coloured:

   | Colour | Meaning | Ticked to import? |
   |---|---|---|
   | Green – **New** | Not saved yet | Yes |
   | Orange – **Already saved** | Same contractor name / work order no. / drawing and item / bill as a saved record | No, or Yes if you choose *update with the file's values* |
   | Yellow – **Repeated in file** | The same record appears more than once in the upload. Each group of repeats gets a group number (D1, D2 …) and the note says which row it repeats: *exact copy of row 6* or *same contractor name as row 6, different details* | Only the first row of each group |
   | Red – **Problem** | A needed value is missing, or a number or date cannot be read | No |
   | Grey – **Left out** | Total and empty rows | No |

   Names are compared ignoring case, spaces and punctuation, so `G.R. Enterprises` matches the saved `G.R ENTERPRISES`. In a BOM, rows with the same drawing and item but different weights are parts and their weights are **added together**; only exact copies are marked as repeats.
   Tick or untick any row (select several rows and press Space). **Save check to Excel…** writes the same coloured list.
4. **Import ticked rows.** New fields are created, then the rows are added. Work orders whose contractor is not in the list add the contractor (you can switch this off). A bill is only added whole: if one of its lines has a problem, the whole bill is held back.

**Added fields** appear as extra columns in Contractors and Work orders (and in their edit forms), in BOM progress at Items level, and in the bill editor's lines. Manage them in **Masters › Added fields** (add, rename, delete).

### Quick imports

**Quick BOM import** and **Quick bill import** skip the check. They import every sheet that fits, leave unmatched columns out, update BOM weights, and skip bills that are already saved.

* **BOM:** a drawing column and a weight column (tonnes, or kg divided by 1000), plus System, Sheet, Rev and Item/Mark when they are there. Without a System column, the sheet name is the system. Sheet numbers that Excel turned into dates (2/3 → 02-Mar) are turned back.
* **Bills:** one row per bill line with *Work order, Bill no, Type, Bill date, Status, GST %, Retention %, TDS %, Other deduction, Paid, Paid on, System, Drawing no, Sheet, Rev, Item, Description, Activity, Unit, Qty, Rate, Remarks*. A row with a drawing becomes a BOM line; a row without one becomes a free line. The **Bill lines** sheet of **Export everything** has the same layout, and **Data › Save Excel templates** writes both layouts with an example row.

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
  src/Bf3Billing.Core/   data file (SQLite + Dapper), bill maths, scope checks, dashboard, reports, Excel import/export (ClosedXML),
                         SmartImport (sheet reading, column matching, duplicate check, added fields)
  src/Bf3Billing.App/    Windows Forms screens, printing, and `Bf3Billing.exe --selftest`
  tests/Bf3Billing.Tests xUnit tests; the demo data must reproduce the design's figures
```

`Bf3Billing.exe --selftest [log file]` opens every screen and every bill on a temporary demo data file and renders the printouts. CI runs it on Windows.
