"""
Example: forward approved DPR quantities to another system (ERP, SAP, data
warehouse). Runs forever, survives restarts by storing the event cursor.

  export CDPR_URL=... CDPR_ANON_KEY=... CDPR_EMAIL=erp-bot@company.com CDPR_PASSWORD=...
  python examples/erp_sync.py
"""
import json
import os
from pathlib import Path

from construct_dpr import ConstructDPR

CURSOR = Path("erp_sync.cursor")

api = ConstructDPR(os.environ["CDPR_URL"], os.environ["CDPR_ANON_KEY"])
api.sign_in(os.environ["CDPR_EMAIL"], os.environ["CDPR_PASSWORD"])
after = int(CURSOR.read_text()) if CURSOR.exists() else 0

for event in api.follow_events(after):
    if event["event_type"] == "dpr.approved":
        doc = api.dpr_document(event["entity_id"])
        for line in doc["activities"]:
            record = {
                "project": doc["project"]["code"], "dpr_no": doc["header"]["dpr_no"],
                "revision": doc["header"]["revision"], "date": doc["header"]["report_date"],
                "activity": line["activity_code"], "boq_item": line.get("boq_item_no"),
                "accepted_qty": line["qty_accepted"], "unit": line["unit"],
            }
            print("→ ERP", json.dumps(record))   # replace with your ERP call (keep it idempotent: dpr_no+revision+activity)
    CURSOR.write_text(str(event["id"]))         # commit the cursor only after the event is handled
