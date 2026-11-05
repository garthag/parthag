# CONSTRUCT DPR Desktop — C# / WPF edition

A Windows program for construction daily progress reporting and scheduling. It runs fully offline on one PC, with all data in a single SQLite file.

| Layer | Technology |
|---|---|
| Frontend | C# + **WPF** (.NET 8), custom styles |
| Backend | C# class libraries (`ConstructDpr.Core`) |
| Database | **SQLite** through Entity Framework Core. Decimals are stored exactly, as text |
| Gantt chart | **Custom Gantt component** (`Controls/GanttChart.cs`), drawn with WPF `DrawingContext` from a layout shared with the PDF |
| Scheduling | **CPM algorithm in C#** (`Scheduling/CpmScheduler.cs`) |
| Reports | **QuestPDF** (PDF) and **ClosedXML** (Excel) |
| Excel integration | **ClosedXML**: export, template, validated import |

> **Why ClosedXML and not EPPlus?** EPPlus 5 and later needs a paid commercial licence for business use. ClosedXML is MIT-licensed and covers everything here (formatting, formulas, tables, import). If your company already owns an EPPlus licence, you can swap it in inside `ExcelService.cs` alone.

## Download and run (Windows 10/11, 64-bit)

1. Open the repository's **Releases** page → **CONSTRUCT DPR Desktop (C#)**.
2. Download **CONSTRUCT-DPR-Desktop.exe** and double-click it. Nothing needs installing, and .NET is included.
3. If Windows shows *"Windows protected your PC"*, click **More info → Run anyway**. This appears because the file is not code-signed yet.
4. The first start loads a demo project, *Blast Furnace #2 — Skip Bridge & Stockhouse*, with a week of approved reports and one report waiting for approval.

Your data is stored in `%LOCALAPPDATA%\ConstructDpr\construct-dpr.db`. To back it up, use **Settings → Back up database…**.

## Screens

The screenshots on the release page are taken automatically from each build:

| Screen | What you do there |
|---|---|
| **Dashboard** | Progress actual vs planned, forecast finish vs contract, executed value (₹), approvals pending, S-curve, critical/delayed activities, progress per activity |
| **Activities & BOQ** | Edit activities: code, WBS, area, unit, BOQ quantity, rate, weight, duration, milestone. Edit predecessors (FS/SS/FF/SF with lag or lead). **Run CPM**, **Freeze baseline** |
| **Schedule (Gantt)** | Custom Gantt: activity table, month/week timeline, bars, progress fill, baseline bars, milestones, dependency arrows, critical path in red, today line, zoom, hover details. Export PDF (A3) / Excel |
| **Daily reports** | Create a DPR (crews and equipment are copied from the previous report). Enter quantities, manpower, equipment, materials and narrative. **Submit → Approve / Reject (reason) → Revise**. Previous / cumulative / balance / % are computed live |
| **Reports & Excel** | DPR PDF/Excel, schedule PDF/Excel, activity import template, import from Excel, audit trail |
| **Settings** | Your name, project details, working days, self-approval policy, back-up, reset to demo |

## Rules the app enforces

- Cumulative quantity can never exceed the BOQ quantity (overrun is blocked).
- One report per date and shift. Report dates cannot be in the future.
- Only *Draft* and *Rejected* reports can be edited. A reject needs a reason.
- The reviewer can lower the **accepted** quantity. The **reported** quantity is kept, and only accepted quantities count towards progress.
- Nobody approves their own report, unless the project allows it (Settings).
- An approved report is locked and gets a **SHA-256 snapshot**. If the database file is changed outside the app, the report shows a tamper warning. Corrections are made as **revisions**, and the original stays.
- Every create, submit, approve, reject and revise is written to the audit trail.
- An Excel import is checked as a whole first. If any row is wrong, nothing is imported.

## Scheduling (CPM)

- Working-day calendar: working weekdays per project, plus holidays.
- FS / SS / FF / SF relationships with lag (positive) or lead (negative).
- Forward and backward pass give early and late dates, total float, free float and the critical path.
- Circular logic is detected and the activities involved are named.
- Actual start and finish dates are fixed. Remaining work cannot start before the data date, so delays push successors.
- Baselines are frozen as numbered revisions, and earlier ones are kept. The Gantt chart and the S-curve compare against the latest one.

## For developers

```bash
cd desktop
dotnet test tests/ConstructDpr.Tests            # 25 tests: CPM, workflow, PDF, Excel (any OS)
dotnet run --project tools/ConstructDpr.Preview -- out   # sample PDFs and workbooks (any OS)
dotnet run --project src/ConstructDpr.App       # the WPF app (Windows only)
ConstructDpr.exe --screenshots <folder>         # PNG of every screen on a fresh demo database
ConstructDpr.exe --data <folder>                # use another data folder
```

```
desktop/
  src/ConstructDpr.Core/      entities, SQLite DbContext, CPM, Gantt layout, DPR workflow, dashboard, demo data
  src/ConstructDpr.Reports/   QuestPDF reports (DPR A4, schedule A3 with Gantt) and ClosedXML export/import
  src/ConstructDpr.App/       WPF app: MainWindow, Views/*, Controls/GanttChart + SCurveChart
  tests/ConstructDpr.Tests/   xUnit tests
  tools/ConstructDpr.Preview/ sample report generator
```

The GitHub workflow `.github/workflows/desktop.yml` does the following on `windows-latest`:

1. Builds the solution and runs the tests.
2. Publishes a single self-contained `.exe`.
3. Runs the app in screenshot mode.
4. Generates the sample reports.
5. Puts all of it on the release page.

## Licences

| Component | Licence |
|---|---|
| This app | Apache-2.0 |
| .NET, WPF, EF Core, Microsoft.Data.Sqlite | MIT |
| ClosedXML | MIT |
| QuestPDF | Community licence. Free for individuals, non-profits, open-source projects and businesses with annual gross revenue under USD 1 million. Larger companies need a QuestPDF Professional or Enterprise licence. See questpdf.com/license |
| EPPlus | not used (commercial licence required) |

## How this edition relates to the others

The web, Android and Windows (Electron) apps share one cloud database (Supabase) and synchronise live. This C# edition is a standalone, single-PC program with its own SQLite file. Use it where a site office wants a classic Windows program that works without internet. It uses the same rules: CPM, approvals, accepted quantities and immutable approved reports.
