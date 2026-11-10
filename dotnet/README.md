# SiteNotes DPR

**Construction site diary, Daily Progress Report (DPR), planning and cost control**, for **Windows PCs (WPF)** and **Android phones**. Everything is written in **C# and SQL**, and both apps work with the same data, synchronised in real time.

| | Windows PC | Android phone / tablet |
|---|---|---|
| App | **WPF desktop** (Ribbon, document tabs, editable grids, rich-text notes) | **.NET MAUI** (touch-first, camera, GPS, share) |
| Best for | Office: planning, review, approval, reports, registers | Site: daily entry, photos, video, document scans |
| Database on the device | SQLite (works fully offline) | SQLite (works fully offline) |
| Sync (optional) | SiteNotes Server: ASP.NET Core + SignalR + **SQL Server** (or SQLite) | same |
| Reports | PDF and Excel, created on the device | same |

---

## 1. How the app is organised

```
Project
├─ Planning ........ WBS (Plant › Unit › Area › System › Work package)
│                    Activities (plan qty = 100 %, dates, method, agency, department, weightage)
│                    Resources & rates (manpower, machinery, material, non-material)
│                    Work calendar (hours/day, working days, holidays, overtime, currency)
├─ Daily reports ... one per day (auto number + auto date): progress, manpower, machinery,
│                    materials, hindrances, requirements, safety, photos/videos, remarks
├─ Registers ....... Hindrance register, Requirement register
├─ Notes & scans ... folders, rich notes, multi-page scanned PDFs
└─ Reports ......... Report manager (PDF / Excel), progress & cost, registers
```

## 2. Daily workflow (2–5 minutes a day)

1. **New DPR** (Ctrl+N on PC, ＋ on phone). The **number** (e.g. `DPR-012`) and **date** (the next working day after the last report, never later than today) are filled in automatically. The first report number is asked **once per project**.
2. Yesterday's crew, machines and every **running activity** below 100 % are already listed.
3. Type **only today's quantity** for each activity. *To date*, *balance* and *%* calculate themselves.
4. Adjust manpower (subcontractor crews, department staff), machines and materials. Book each row to an activity so cost per activity is exact.
5. Add hindrances, requirements, toolbox talk, photos and remarks.
6. **Save** → **Submit** → **Approve** (approved reports lock). Press **PDF** (Ctrl+P) to print or share.

## 3. Modules

### 3.1 Daily Progress Report
- **Header:** automatic number, date, shift, weather (morning / evening), temperature, rain, working hours, prepared / checked / approved by, visitors.
- **Live summary:** total manpower (subcontractor and department), man-hours, machines, activities worked, **cost of the day**, hours lost.
- **1 Work progress:** activity, location, agency, unit, planned qty, **today**, *to date (auto)*, *balance*, *progress bar*, and a check that warns if the plan is exceeded. Add from the plan (tick many at once) or add unplanned work.
- **2 Manpower:** subcontractor or department, trade, activity worked on, nos, hours, *man-hours*, rate, basis, *cost*.
- **3 Machinery:** own or hired, activity, nos, working / idle hours, fuel, rate, *cost* (idle hours are charged only for hired machines).
- **4 Materials & non-material:** received and consumed quantities, activity, rate, *cost*. "Other" covers hire, testing and transport.
- **5–6 Hindrances and requirements:** these also appear in the project registers with automatic serial numbers.
- **7 Safety & remarks:** toolbox talk topic and attendees, safety observations, plan for tomorrow, remarks.
- **8 Photos & videos:** camera or gallery on the phone, files or drag-and-drop on the PC. Phone photos carry GPS and time.
- Duplicate date/shift check. Unsaved-changes protection. Approved reports are read-only until unlocked.

### 3.2 Planning, progress and cost
- **WBS tree:** Project › Plant › Unit › Area › System › Work package. Progress reports group by this path.
- **Activities:** code, name, WBS, **method**, agency, department, unit, planned quantity, planned start / finish, planned output per day, weightage, location.
- **Calculated per activity:** done, %, balance, planned % to date, actual start, days worked, average per day, **forecast finish**, status (*Not started / In progress / Delayed / Completed*), **cost, cost per unit, cost per day, labour cost per man-hour, budget, earned value**.
- **Assigned resources:** planned crew, machines and materials per activity, which give a **planned budget**.
- **Subcontractor-wise:** man-days, man-hours, cost and quantity of work done per activity. **Department-wise:** the same for departments.

### 3.3 Registers and masters
- **Hindrance register:** dates, category, description, affected activity, responsibility, hours lost, status, action. Filters and bulk "mark resolved".
- **Requirement register:** item, quantity, unit, required-by date, priority, status, overdue flag. Bulk status changes.
- **Masters:** projects, subcontractors, departments, resources & rates.

### 3.4 Notes and scanner
- **Rich notes:** headings H1–H3, normal text, quote, **bullets, numbering, arrows, checklists** (ticked items are struck through), indent, **bold / italic / underline / strike**, 6 font styles, 14 sizes, colours, highlight, alignment, divider lines, **pictures, videos, scanned documents**.
- Notes live in folders and sub-folders, with search, pin, move, duplicate, PDF export and auto-save.
- **Scan to PDF:** add pages one after another (camera on the phone, files or drag-and-drop on the PC). Clean-up options are Enhanced, Grayscale, or **Black & white** (adaptive, removes shadows). Rotate, reorder and add remarks per page. The result is saved as **one PDF plus each page as an image**, filed automatically in **Scans › yyyy-MM-dd**.

### 3.5 Report manager (PDF and Excel)
| Choice | Options |
|---|---|
| Report | Daily report · all reports in one PDF · register summary · DPR workbook (Excel) · progress & cost (PDF / Excel) · manpower summary · hindrance register · requirement register |
| Days | All, Today, Yesterday, This week, Last 7 days, This month, Last month, Custom from–to |
| Filter | Status (multi-select), agency, search text |
| Sort | Newest, oldest, report no., most manpower, status. Activity rows: as entered, name, most / least progress, most done today, agency, location. Manpower rows: agency, trade, most people, activity |
| Contents | Tick sections and set their **order**. Numbering follows. Show / hide costs. Leave out empty sections. Photos per row (1–4) and per report |
| Header | **Company logo** (left), **client logo** (right), company name, **header lines** (project ref, contract no., …), alignment left / centre / right |
| Footer | Footer text, date generated, **page numbers** `Page 3` · `Page 3 of 9` · `3 / 9` · none, positioned left / centre / right |
| Remarks | Custom report title and a note printed on the report |

**Save as default** stores these settings per project. They sync, and every PDF the app makes uses them. Excel workbooks have frozen headers, auto-filters and `SUBTOTAL` totals that follow the filter.

### 3.6 Lists everywhere
- **Search box**, filter list, **click a column header to sort**, **Ctrl / Shift + click to select many**.
- Bulk actions on the selection: PDF, Excel, submit / approve / draft, resolve, change status, activate / deactivate, delete.
- PC grids work like a spreadsheet (type, Tab, Enter). Every edited row saves itself.

## 4. How the calculations work

All money uses `decimal` arithmetic, rounded to 2 places per row, so totals add up exactly.

| Figure | Formula |
|---|---|
| To date (cumulative) | Σ *today's quantity* of the activity in all reports up to this one (by date, then number) |
| % complete | To date ÷ planned qty × 100, capped at 100 % |
| Balance | Planned qty − to date (never below 0) |
| Planned % to date | Working days elapsed ÷ planned working days (work calendar) |
| Average per day | To date ÷ days the activity was worked |
| Forecast finish | Today + balance ÷ average per day, counted in **working days** of the calendar |
| Delayed | Forecast finish after planned finish, or more than 10 points behind planned % |
| Overall progress | Σ (weight × %) ÷ Σ weight. Weight = weightage, else planned budget, else equal |
| Man-hours | Nos × hours. **Man-days** = man-hours ÷ hours per day |
| Paid hours | Normal hours + overtime hours × overtime factor |
| Manpower cost | Per hour: nos × paid hours × rate. Per day: nos × rate × paid hours ÷ hours per day |
| Machine cost | Nos × chargeable hours × rate (hourly) or ÷ hours per day (daily). Chargeable = working hours, plus idle hours if hired |
| Material / other cost | Consumed × rate, or lump sum |
| Cost per unit / per day | Activity cost ÷ quantity done / ÷ days worked |
| Earned value | Budget × % complete |
| Unallocated cost | Rows not booked to an activity show as **General / overheads** |

## 5. Architecture

```
dotnet/
├─ src/SiteNotes.Core      shared C#: models, SQLite database, progress & cost engine (parallel),
│                          PDF engine (SkiaSharp), Excel (ClosedXML), scanner, sync client
├─ src/SiteNotes.Desktop   Windows app – WPF, all UI in C# (no XAML)
├─ src/SiteNotes.App       Android app – .NET MAUI, all UI in C#
├─ src/SiteNotes.Server    sync server – ASP.NET Core + SignalR, plain ADO.NET + SQL
│   └─ Sql/sqlserver.sql   schema + reporting views (vw_DailyReports, vw_Manpower, vw_Activities, …)
└─ tests/
   ├─ SiteNotes.Core.Tests        calculations, reports, scanner, two-device sync through the real server
   ├─ SiteNotes.Desktop.Tests     WPF: note editor round-trip, every screen opens (runs on Windows)
   └─ SiteNotes.App.CompileCheck  checks the Android UI code on any OS
```

- **Offline first:** every device keeps its own SQLite database. Ids are GUIDs, so records can be created anywhere.
- **Sync:** edits are pushed about 1.5 s after a change. The server stores rows in SQL and broadcasts a SignalR "changed" message, and every other device pulls immediately. Photos, videos, scans and logos sync as files. When two devices edit the same row, the **newest edit wins**. Deletes sync too, because they are soft deletes.
- **Performance:** multi-report exports load with one query per table. Progress and cost run in parallel across CPU cores, as does scan page processing. Grids are virtualised. Photo thumbnails decode at small size. PDFs embed JPEGs. "Page n of N" uses a fast layout pass that skips images.

## 6. Install and set up

1. **Windows:** download `SiteNotes-windows.zip` from the repository's Releases page (tags `sitenotes-N`), extract it and run `SiteNotes.exe`.
2. **Android:** install `SiteNotes-android.apk`.
3. Create the **project**. Then:
   - set the **work calendar**,
   - add **subcontractors**,
   - add **resources & rates**,
   - add the **WBS**,
   - add **activities**.

   Or use **Load sample project** to explore first.
4. *(Optional, for a team)* Run the **sync server**:
   - Extract `SiteNotesServer-windows.zip` (or the Linux zip).
   - Edit `appsettings.json`:
     - `SiteNotes:ApiKey`: your site key.
     - `SiteNotes:Provider`: `SqlServer` or `Sqlite`.
     - `ConnectionStrings:SqlServer`: e.g. `Server=.\SQLEXPRESS;Database=SiteNotes;Trusted_Connection=True;TrustServerCertificate=True`. Create the empty database first. Tables and views are created on start.
   - Run `SiteNotes.Server` (port 5080) and allow the port in the firewall.
   - In each app, open **Settings & sync** and enter `http://<server>:5080` and the key.
   - Put the server behind HTTPS if it is on the internet. The site key is a shared password, not a per-user login.

The SQL Server views (`vw_DailyReports`, `vw_Manpower`, `vw_Activities`, `vw_Machinery`, `vw_Hindrances`, `vw_Requirements`) can be opened directly in Excel or Power BI.

## 7. Build from source

```bash
dotnet test  dotnet/tests/SiteNotes.Core.Tests           # any OS
dotnet build dotnet/src/SiteNotes.Desktop                 # WPF (compiles on any OS, runs on Windows)
dotnet test  dotnet/tests/SiteNotes.Desktop.Tests         # Windows
dotnet workload install maui-android
dotnet publish dotnet/src/SiteNotes.App -f net10.0-android -c Release
```

GitHub Actions (`.github/workflows/dotnet.yml`) runs all tests and publishes the Windows app, the Android APK and the server on every push.

## 8. Keyboard shortcuts (Windows)

| Keys | Action |
|---|---|
| Ctrl+N | New daily report (auto number + date) |
| Ctrl+S | Save the current tab |
| Ctrl+P | PDF of the current report / note |
| Ctrl+W | Close the tab |
| F5 | Refresh / recalculate |
| Ctrl+Enter | Tick or untick a checklist line in notes |
| Ctrl / Shift + click | Select several rows |
