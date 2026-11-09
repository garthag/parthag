# User manual

## For everyone

* **Install**: open the app link in Chrome → ⋮ → *Install app*. It works offline for drafts.
* **Status bar** (top): green = online and up to date; blue = synchronising; amber = offline (your work is on the phone); red = something needs attention → tap *Review*.
* **Project**: *Projects* → choose. The app remembers it.
* **Theme**: *Settings → Theme* (Light is best in sunlight).

## Site engineer / supervisor — daily report in 5 minutes

1. Home → **＋ Today's DPR**.
2. **Report**: date, shift, area, weather. (Prepared-by is automatic.)
3. **Activities**: type part of the code or name, or pick a ★ favourite / recent, or **Copy activities from last report**. Enter **Today** quantity — previous, cumulative, balance, % and variance calculate instantly.
   * Behind plan? add a *delay reason* (open *More details*).
   * Over the approved quantity? the app stops you unless you tick *Authorised overrun* and give a reference.
   * **Voice**: *🎙 Voice note* → *Allow microphone* → speak, e.g. "At the skip bridge, erected twelve metric tonnes of structural steel during day shift using one mobile crane and twenty-four workers." → correct the text → *Convert to DPR entry* → check the highlighted fields → *Add to report*. No microphone? use *Type or paste a site statement*.
4. **Evidence**: *📷 Capture photo* (switch camera, take several), *🎥 Record video* (max 60 s), *🖼 Gallery / files*, *📍 Add location* (once, or type it). Add a caption and the activity, then **Add to report**. Files upload automatically when the report is saved and you are online.
5. **Manpower / Equipment / Materials**: add lines; totals, man-hours, utilisation and closing stock are calculated.
6. **Safety & QA**: toolbox talks, permits (with checklists), observations, inspections, NCRs, constraints, actions.
7. **Narrative**: work done, problems, tomorrow's plan.
8. **Review**: fix red errors, acknowledge amber warnings → **Submit for review**. Offline? *Save draft* — it will send itself later; submit when online.

If the reviewer **rejects** the report you will see it under *Approvals → returned for correction* with the reason. Edit and resubmit.

## Reviewer / project manager

* *Approvals* lists reports waiting; it updates live.
* Open a report → *Start review* (optional) → **Approve…**: confirm the accepted quantity per activity (defaults to inspected or reported) → Approve. Or **Reject…** with a reason.
* After approval: **Download PDF** (portrait/landscape) and **Publish PDF to Drive**. The approved report can never change; to correct it use **Start revision…** with a reason — a new revision goes through approval again and the old one is kept.

## Planning engineer

* *Projects → Setup*: WBS, areas, calendar (working days, holidays), members.
* *Activities*: add/edit or **Import CSV**; set approved quantity, weight, duration, planned dates.
* *Schedule*: add links (FS/SS/FF/SF, lag/lead) → review dates, floats and critical path (red) → **Apply dates to plan** → **Freeze baseline** (mark as approved). Use *Look-ahead* for 7/14/21-day plans; **Export CSV**.

## Storekeeper

Record receipts (challan, heat/batch, inspection status), issues, consumption and returns in DPR step 5. *Materials* shows stock, shortage risk and each material's ledger.

## Commercial engineer

*Setup → BOQ*: measured, certified and billed quantities per item; contract/executed/certified/billed amounts. Quantity or rate changes: **Revise** (requires *boq approve* permission and a reason) — history is kept; approved DPRs are not affected.

## QA/QC and safety

*Safety & QA/QC* registers: update status, responsible and target dates; overdue items show in red; commissioning checklists and punch list (A/B/C).

## Administrators

* *Setup → Members & roles*: add registered users by e-mail, choose role, deactivate leavers.
* *Setup → Details*: timezone, currency, decimals, report header/footer, self-approval policy.
* *Documents*: create the Drive folder tree; share the project folder with specific Google accounts only when needed.
* *Settings → Export project (JSON)* for a complete portable copy.
