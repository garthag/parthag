# SiteNotes DPR – C# app for Android and Windows

A construction site notebook and **Daily Progress Report (DPR)** app with planning, progress and cost tracking. It is written only in **C# and SQL**:

- **App:** .NET MAUI (one C# code base for Android phones/tablets and Windows 10/11 PCs). All screens are built in C#, with no XAML pages.
- **On the device:** SQLite. The app works fully offline.
- **Sync server (optional):** ASP.NET Core + SignalR with **SQL Server** (or SQLite for small sites). Changes reach every device within seconds.
- **Reports:** PDF (SkiaSharp) and Excel (ClosedXML), generated on the device.

## What it does

### Planning
| Feature | Details |
|---|---|
| **WBS** | Project › Plant › Unit › Area › System › Work package. Activities link to any level, and reports group by WBS path. |
| **Activities** | Code/BOQ item, WBS, location, **method**, agency (subcontractor), department, unit, planned quantity (= 100 %), planned start/finish, planned output per day, weightage. |
| **Resources & rates** | Manpower, Material, Machinery and **Other (non-material)** costs such as hire, testing and transport. Rates are per hour, per day, per unit or lump sum, and each resource can be linked to a subcontractor or department. |
| **Assigned resources** | Planned crew, machines and materials per activity, giving a planned budget per activity. |
| **Work calendar** | Hours per day, shift start, working weekdays, holidays, overtime factor and currency. |

### Daily progress report
- **Activities:** pick from the plan and enter only **today's quantity**. Cumulative quantity, % complete and balance are **calculated automatically** from all earlier reports, shown with a progress bar, and flagged if they exceed the plan. A new report automatically lists every activity that is running and below 100 %.
- **Manpower:** subcontractor crews and department staff, with trade, number, hours, **activity worked on** and rate. Totals (subcontractor, department, man-hours) update live.
- **Machinery:** own or hired, with working, idle and fuel figures, activity and rate. Idle hours are charged only for hired machines.
- **Material & non-material resources:** received and consumed quantities, activity and rate.
- **Live summary bar:** total manpower, man-hours, machines and **cost of the day**.
- Hindrances, requirements, safety and toolbox talk, photos and videos (with GPS and time), plan for tomorrow, visitors and remarks.
- **Carry forward** from the previous day, a **Draft → Submitted → Approved** workflow (approved reports are locked), and a duplicate-date check.

### Cost (exact, decimal arithmetic)
- Manpower: per hour = nos × paid hours × rate. Per day = nos × rate × paid hours ÷ hours per day. Paid hours = normal hours + overtime × factor.
- Machinery: nos × chargeable hours × rate (hourly) or ÷ hours per day (daily rate).
- Material / other: consumed × rate, or lump sum.
- Results include **cost per activity, per unit, per day, per hour, labour cost per man-hour, planned budget and earned value**. Cost not booked to an activity appears separately as "General / overheads".

### Progress & cost screen and reports
- Overall % (weighted), planned % to date, and each activity's state: not started, in progress, **delayed** or completed.
- Forecast finish date from the actual average output per day and the calendar.
- **Subcontractor-wise:** man-days, man-hours, cost and the quantity of work done per activity.
- **Department-wise:** man-days, man-hours, cost and activities.
- Calculations run in parallel across CPU cores.

### Notes & scanner
- Folders and sub-folders, search, sort, pin, move, duplicate.
- The block editor supports headings and sub-headings, bullets, numbering, **arrows**, **checklists (checked items are struck through)**, quotes, dividers, indents, bold/italic/underline/strike, 6 font styles, 14 sizes, text colour, highlight and alignment. It also inserts **camera photos, gallery photos, videos and scans**.
- **PDF scanner:** capture pages one after another (each photo becomes a separate page), with Enhanced, Grayscale or adaptive Black & White clean-up (shadow removal). You can rotate, reorder and add remarks per page. Saving stores every page as an image plus one combined PDF, filed **automatically in Scans › yyyy-MM-dd**.

### Report numbers and dates
- **Automatic:** the app asks for the first report number **once per project**. Every new report then takes the next number (shown with a prefix such as `DPR-012`) and the **next working day** after the last report, never later than today.
- One tap on **＋ New DPR** opens it ready to fill. If a report already exists for that date, you can open it or start another shift.
- Prefix, first number and "copy yesterday's crew & activities" are set in the project form.

### Report manager (all choices by tapping)
- **Report:** daily report, all reports in one PDF, register summary, Excel workbook, progress & cost, manpower summary, hindrance and requirement registers.
- **Days, filter & sort:** period chips (Today, This week, This month, Last month, Custom…), status (multi-select), agency, search, sort.
- **What to include & order:** tick sections and move them up or down. Numbering follows automatically. You can also set activity row sort (name, most progress, most done today, agency, location), manpower row sort, show/hide costs, leave out empty sections, photos per row (1–4) and photos per report.
- **Header, footer & page numbers:** company logo (left), client logo (right), company name, header lines (project ref, contract no.…), header alignment (left, centre, right), footer text, generated stamp, page numbers (`Page 3`, `Page 3 of 9`, `3 / 9` or none) and their position, portrait or landscape.
- **Remarks:** custom report title and a note printed on the report.
- **Save as default:** saved per project, synced, and used by every PDF the app makes.

### Faster work with lists
- **☑ Select** on every list lets you select items (or All) and then apply an action: PDF, Excel, mark submitted / approved / draft, mark resolved, change requirement status, activate / deactivate agencies, or delete.
- Filters and sorting are chips: one tap and the list updates.

### Registers, filters and reports
- Hindrance register and requirement register (with an overdue flag). Both can be searched, sorted and filtered, and exported to PDF or Excel.
- **Report centre:** choose a period preset or custom dates, status, subcontractor, search text and sort order, then generate:
  - DPR (one day, PDF) and DPR register (PDF, optionally with every full report)
  - DPR workbook (Excel: summary, activities, manpower, manpower grid, machinery, materials, hindrances, requirements, all with cost)
  - Activity progress & cost (PDF / Excel)
  - Manpower summary
  - Hindrance and requirement registers
- Excel files have frozen headers, auto-filters and SUBTOTAL totals that follow the filter.

### Interface
- **Windows:** a custom title bar showing the app name, current project and live sync status, a side menu that is always open, and toolbars.
- **Android:** a slide-out menu, title bar toolbars, and the camera, video, GPS and share sheet (WhatsApp, email, Drive).
- Dashboard with quick actions, key figures, a 14-day manpower chart and a "needs attention" list (delayed activities, overdue requirements, open hindrances).
- Light and dark theme. Sample project loader.

## Project layout

```
dotnet/
  src/SiteNotes.Core      models, SQLite database, progress & cost engine, PDF/Excel reports, scanner, sync client
  src/SiteNotes.App       .NET MAUI app (Android + Windows), UI in C#
  src/SiteNotes.Server    sync server: ASP.NET Core + SignalR, SQL Server or SQLite
    Sql/sqlserver.sql     schema + reporting views (vw_DailyReports, vw_Manpower, vw_Activities, …)
  tests/SiteNotes.Core.Tests        unit tests + two-device sync test through the real server
  tests/SiteNotes.App.CompileCheck  compiles the app UI code on any OS
```

## Build

```bash
# tests (any OS)
dotnet test dotnet/tests/SiteNotes.Core.Tests

# app (Windows with Visual Studio 2022+ or the .NET 10 SDK)
dotnet workload install maui
dotnet build dotnet/src/SiteNotes.App -f net10.0-android
dotnet build dotnet/src/SiteNotes.App -f net10.0-windows10.0.19041.0
```

GitHub Actions (`.github/workflows/dotnet.yml`) runs the tests and publishes `SiteNotes-android.apk`, `SiteNotes-windows.zip` and the server zips on the Releases page (tags `sitenotes-N`).

## Real-time sync server

1. Download `SiteNotesServer-windows.zip` (or the Linux one) from Releases and extract it.
2. Edit `appsettings.json`:
   - `SiteNotes:ApiKey`: your site key (every device enters the same key).
   - `SiteNotes:Provider`: `SqlServer` or `Sqlite`.
   - `ConnectionStrings:SqlServer`: for example `Server=.\SQLEXPRESS;Database=SiteNotes;Trusted_Connection=True;TrustServerCertificate=True`. Create the empty database first. Tables and views are created on start.
3. Run `SiteNotes.Server` (it listens on port 5080) and allow that port in the firewall.
4. In the app, open **Settings & sync** and enter `http://<server-ip>:5080` and the key.

Each device pushes its changes and the server broadcasts a SignalR "changed" message, so the other devices pull immediately. Photos, videos and scans sync as files. When two devices edit the same row, the newest edit wins. Offline edits queue up and are sent when the connection returns.

The SQL Server views (`vw_DailyReports`, `vw_Manpower`, `vw_Activities`, `vw_Machinery`, `vw_Hindrances`, `vw_Requirements`) let you build Power BI or Excel reports straight from the server.

> Use HTTPS (a reverse proxy) when the server is on the internet. The site key is a shared password, not per-user login.
