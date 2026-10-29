"""Command line tool: ``python -m construct_dpr <command>``.

Credentials come from environment variables (never put passwords in scripts):
  CDPR_URL, CDPR_ANON_KEY, CDPR_EMAIL, CDPR_PASSWORD
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import sys
from pathlib import Path

from .client import ConstructDPR, ConstructDPRError


def connect() -> ConstructDPR:
    missing = [k for k in ("CDPR_URL", "CDPR_ANON_KEY", "CDPR_EMAIL", "CDPR_PASSWORD") if not os.environ.get(k)]
    if missing:
        sys.exit(f"Set environment variables: {', '.join(missing)}")
    api = ConstructDPR(os.environ["CDPR_URL"], os.environ["CDPR_ANON_KEY"])
    api.sign_in(os.environ["CDPR_EMAIL"], os.environ["CDPR_PASSWORD"])
    return api


def write_csv(rows: list[dict], path: str | None) -> None:
    out = open(path, "w", newline="", encoding="utf-8-sig") if path else sys.stdout
    if rows:
        w = csv.DictWriter(out, fieldnames=list(rows[0].keys()), extrasaction="ignore")
        w.writeheader()
        for r in rows:
            w.writerow({k: json.dumps(v) if isinstance(v, (dict, list)) else v for k, v in r.items()})
    if path:
        out.close()


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="construct_dpr", description="CONSTRUCT DPR command line tool")
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("projects", help="list projects you can access")
    e = sub.add_parser("export", help="export a whole project as JSON")
    e.add_argument("project_code")
    e.add_argument("-o", "--out", required=True)
    r = sub.add_parser("progress", help="activity progress as CSV")
    r.add_argument("project_code")
    r.add_argument("-o", "--out")
    b = sub.add_parser("boq", help="BOQ status (contract/executed/certified/billed) as CSV")
    b.add_argument("project_code")
    b.add_argument("-o", "--out")
    ev = sub.add_parser("events", help="print integration events as JSON lines")
    ev.add_argument("--after", type=int, default=0)
    ev.add_argument("--follow", action="store_true", help="keep waiting for new events")
    ia = sub.add_parser("import-activities", help="create/update activities from a CSV (code,name,unit,total_qty,…)")
    ia.add_argument("project_code")
    ia.add_argument("csv_file")
    a = p.parse_args(argv)

    try:
        api = connect()
        if a.cmd == "projects":
            for pr in api.projects():
                print(f"{pr['code']:<16} {pr['name']}")
        elif a.cmd == "export":
            data = api.export_project(api.project_by_code(a.project_code)["id"])
            Path(a.out).write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
            print(f"Wrote {a.out}: {len(data.get('dpr_headers', []))} DPRs, {len(data.get('activities', []))} activities")
        elif a.cmd == "progress":
            write_csv(api.activity_progress(api.project_by_code(a.project_code)["id"]), a.out)
        elif a.cmd == "boq":
            write_csv(api.boq_status(api.project_by_code(a.project_code)["id"]), a.out)
        elif a.cmd == "events":
            for evt in api.follow_events(a.after, stop_when_idle=not a.follow):
                print(json.dumps(evt, ensure_ascii=False), flush=True)
        elif a.cmd == "import-activities":
            pid = api.project_by_code(a.project_code)["id"]
            with open(a.csv_file, newline="", encoding="utf-8-sig") as f:
                rows = []
                for r in csv.DictReader(f):
                    row = {k: (v if v != "" else None) for k, v in r.items() if k}
                    for num in ("total_qty", "weight", "original_duration"):
                        if row.get(num) is not None:
                            row[num] = float(row[num])
                    rows.append(row)
            print(f"Imported {len(api.upsert_activities(pid, rows))} activities")
    except ConstructDPRError as err:
        print(f"Error: {err}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
