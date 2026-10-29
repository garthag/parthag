"""
Example: weekly progress workbook for management (needs: pip install openpyxl).

  python examples/weekly_excel.py DEMO-BF2 progress.xlsx
"""
import os
import sys

from openpyxl import Workbook

from construct_dpr import ConstructDPR

code, out = sys.argv[1], sys.argv[2]
api = ConstructDPR(os.environ["CDPR_URL"], os.environ["CDPR_ANON_KEY"])
api.sign_in(os.environ["CDPR_EMAIL"], os.environ["CDPR_PASSWORD"])
project = api.project_by_code(code)

wb = Workbook()
ws = wb.active
ws.title = "Activity progress"
ws.append(["Code", "Activity", "Unit", "Approved qty", "Accepted cumulative", "Progress %", "Last reported"])
for a in api.activity_progress(project["id"]):
    ws.append([a["code"], a["name"], a["unit"], a["total_qty"], a["accepted_cum_qty"], a["progress_pct"], a["last_reported_on"]])

bs = wb.create_sheet("BOQ")
bs.append(["Item", "Description", "Unit", "Approved qty", "Executed qty", "Rate", "Contract ₹", "Executed ₹", "Certified ₹", "Billed ₹"])
for b in api.boq_status(project["id"]):
    bs.append([b["item_no"], b["description"], b["unit"], b["approved_qty"], b["executed_qty"], b["rate"],
               b["contract_amount"], b["executed_amount"], b["certified_amount"], b["billed_amount"]])
wb.save(out)
print("Saved", out)
