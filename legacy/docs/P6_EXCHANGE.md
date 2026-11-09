# Primavera P6 exchange (CSV)

Native P6 XER/XML import is **not** implemented and the app does not claim P6 compatibility. The tested route is CSV:

## P6 → CONSTRUCT DPR
1. In P6 select the activities layout with columns: Activity ID, Activity Name, WBS Code, Original Duration, Start (or BL Project Start), Finish, Activity % Complete is *not* imported.
2. *File → Export → Spreadsheet (XLSX)*, open in Excel/Sheets, rename headers to:
   `code, name, wbs_code, original_duration, planned_start, planned_finish` (+ optional `unit, total_qty, weight, area_code, boq_item_no, location, is_milestone, responsible`).
3. Dates must be `YYYY-MM-DD`. Save as CSV (UTF-8).
4. Create the WBS codes first (Setup → WBS), then *Activities → Import CSV*. Existing codes are updated, new ones inserted.
5. Relationships: add them in *Schedule → Relationships* (CSV import of links is on the roadmap).

## CONSTRUCT DPR → P6
*Schedule → Export CSV* gives code, name, duration, early/late dates, floats, critical flag, baseline dates and finish variance. Map to P6 columns with *Import → Spreadsheet* in P6, or use it for progress updates (actual start/finish from *Activities → Export CSV*).

Differences to expect: P6 uses its own calendars, hours-based durations and retained-logic/progress-override settings; this engine uses working days, retained logic, and treats actual dates as fixed.
