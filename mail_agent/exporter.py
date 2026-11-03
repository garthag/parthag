"""Export the result list / index as Excel, CSV, JSON or a text bundle; tables; email drafts."""

from __future__ import annotations

import csv
import io
import json
from collections import defaultdict
from datetime import datetime
from email.message import EmailMessage
from email.utils import formatdate

from .models import Attachment
from .smartfields import DOC_LABELS

COLUMNS = [
    ("email_date", "Email date"), ("from_name", "From"), ("from_email", "From email"), ("to", "To"),
    ("subject", "Subject"), ("filename", "File"), ("ext", "Type"), ("size_kb", "Size (KB)"),
    ("doc_type", "Document"), ("doc_no", "Number"), ("doc_date", "Document date"), ("amount", "Amount"),
    ("currency", "Currency"), ("gstin", "GSTIN"), ("text_source", "Text"), ("folder", "Folder"),
]


def rows_for(atts: list[Attachment]) -> list[dict]:
    out = []
    for a in atts:
        f = a.fields or {}
        out.append({
            "email_date": a.date.strftime("%Y-%m-%d %H:%M") if a.date else "",
            "from_name": a.from_name, "from_email": a.from_email, "to": a.to, "subject": a.subject,
            "filename": a.filename, "ext": a.ext, "size_kb": round(a.size / 1024, 1),
            "doc_type": DOC_LABELS.get(f.get("doc_type") or "other", f.get("doc_type") or ""),
            "doc_no": f.get("doc_no") or "", "doc_date": f.get("doc_date") or "",
            "amount": f.get("amount"), "currency": f.get("currency") or "", "gstin": f.get("gstin") or "",
            "text_source": a.text_source or "", "folder": a.folder,
        })
    return out


def to_csv(atts: list[Attachment]) -> bytes:
    buf = io.StringIO()
    w = csv.writer(buf)
    w.writerow([h for _, h in COLUMNS])
    for r in rows_for(atts):
        w.writerow(["" if r[k] is None else r[k] for k, _ in COLUMNS])
    return ("﻿" + buf.getvalue()).encode("utf-8")     # BOM -> Excel opens UTF-8 correctly


def to_json(atts: list[Attachment]) -> bytes:
    return json.dumps(rows_for(atts), indent=1, ensure_ascii=False).encode("utf-8")


def _month(a: Attachment) -> str:
    d = (a.fields or {}).get("doc_date") or (a.date.date().isoformat() if a.date else "")
    return d[:7] if d else "unknown"


def summary(atts: list[Attachment]) -> dict:
    by_month: dict = defaultdict(lambda: [0, 0.0])
    by_sender: dict = defaultdict(lambda: [0, 0.0])
    for a in atts:
        amt = (a.fields or {}).get("amount") or 0
        for key, bucket in ((_month(a), by_month), (a.from_name or a.from_email, by_sender)):
            bucket[key][0] += 1
            bucket[key][1] += amt
    return {"by_month": sorted(by_month.items()), "by_sender": sorted(by_sender.items(), key=lambda x: -x[1][1])}


def to_xlsx(atts: list[Attachment], title: str = "Attachments") -> bytes:
    from openpyxl import Workbook
    from openpyxl.styles import Alignment, Font, PatternFill
    from openpyxl.utils import get_column_letter

    wb = Workbook()
    ws = wb.active
    ws.title = "Attachments"
    head_font, head_fill = Font(bold=True, color="FFFFFF"), PatternFill("solid", fgColor="2563EB")
    ws.append([h for _, h in COLUMNS])
    for c in ws[1]:
        c.font, c.fill = head_font, head_fill
    rows = rows_for(atts)
    for r in rows:
        ws.append([r[k] for k, _ in COLUMNS])
    amount_col = [k for k, _ in COLUMNS].index("amount") + 1
    n = len(rows)
    for row in ws.iter_rows(min_row=2, max_row=n + 1, min_col=amount_col, max_col=amount_col):
        for c in row:
            c.number_format = "#,##0.00"
    total_row = n + 2
    ws.cell(total_row, 1, "TOTAL").font = Font(bold=True)
    ws.cell(total_row, 6, f"{n} files").font = Font(bold=True)
    if n:
        col = get_column_letter(amount_col)
        tc = ws.cell(total_row, amount_col, f"=SUBTOTAL(9,{col}2:{col}{n + 1})")
        tc.font, tc.number_format = Font(bold=True), "#,##0.00"
    widths = {"subject": 42, "filename": 32, "from_name": 24, "from_email": 26, "to": 26}
    for i, (k, _) in enumerate(COLUMNS, 1):
        ws.column_dimensions[get_column_letter(i)].width = widths.get(k, 14)
    ws.freeze_panes = "A2"
    ws.auto_filter.ref = f"A1:{get_column_letter(len(COLUMNS))}{n + 1}"

    s = summary(atts)
    for name, data, label in (("By month", s["by_month"], "Month"), ("By sender", s["by_sender"], "Sender")):
        sh = wb.create_sheet(name)
        sh.append([label, "Files", "Total amount"])
        for c in sh[1]:
            c.font, c.fill = head_font, head_fill
        for key, (cnt, amt) in data:
            sh.append([key, cnt, round(amt, 2)])
        for row in sh.iter_rows(min_row=2, min_col=3, max_col=3):
            for c in row:
                c.number_format = "#,##0.00"
        sh.column_dimensions["A"].width = 32
        sh.column_dimensions["B"].width = 10
        sh.column_dimensions["C"].width = 16
        sh.freeze_panes = "A2"

    info = wb.create_sheet("About")
    info["A1"], info["A1"].font = title, Font(bold=True, size=14)
    info["A2"] = f"Exported {datetime.now():%Y-%m-%d %H:%M} by Mail Attachment Agent"
    info["A3"] = "Amounts, numbers and dates are detected automatically - check important values."
    info["A3"].alignment = Alignment(wrap_text=True)
    info.column_dimensions["A"].width = 80
    buf = io.BytesIO()
    wb.save(buf)
    return buf.getvalue()


def text_bundle(atts: list[Attachment]) -> bytes:
    parts = []
    for a in atts:
        f = a.fields or {}
        parts.append("=" * 78)
        parts.append(f"FILE    : {a.filename}")
        parts.append(f"FROM    : {a.from_name} <{a.from_email}>")
        parts.append(f"DATE    : {a.date:%Y-%m-%d %H:%M}" if a.date else "DATE    : -")
        parts.append(f"SUBJECT : {a.subject}")
        if f.get("doc_no") or f.get("amount"):
            parts.append(f"DETECTED: {DOC_LABELS.get(f.get('doc_type') or 'other', '')} "
                         f"{f.get('doc_no') or ''}  {f.get('doc_date') or ''}  "
                         f"{f.get('currency') or ''} {f.get('amount') if f.get('amount') is not None else ''}")
        parts.append(f"TEXT    : ({a.text_source or 'not read yet'})")
        parts.append("-" * 78)
        parts.append((a.text or "").strip() or "(no text)")
        parts.append("")
    return "\n".join(parts).encode("utf-8")


def tables_csv(tables: list[list[list[str]]]) -> bytes:
    buf = io.StringIO()
    w = csv.writer(buf)
    for i, t in enumerate(tables):
        if i:
            w.writerow([])
        w.writerows(t)
    return ("﻿" + buf.getvalue()).encode("utf-8")


def tables_xlsx(tables: list[list[list[str]]]) -> bytes:
    from openpyxl import Workbook
    from openpyxl.styles import Font
    wb = Workbook()
    wb.remove(wb.active)
    for i, t in enumerate(tables, 1):
        ws = wb.create_sheet(f"Table {i}")
        for r in t:
            ws.append([_num(c) for c in r])
        for c in ws[1]:
            c.font = Font(bold=True)
    if not tables:
        wb.create_sheet("Table 1")
    buf = io.BytesIO()
    wb.save(buf)
    return buf.getvalue()


def _num(cell: str):
    s = cell.replace(",", "").replace("₹", "").replace("$", "").strip()
    try:
        return float(s) if s and any(ch.isdigit() for ch in s) and s.replace(".", "", 1).lstrip("-").isdigit() else cell
    except ValueError:
        return cell


def share_text(atts: list[Attachment]) -> str:
    lines = []
    total = 0.0
    for a in atts:
        f = a.fields or {}
        amt = f.get("amount")
        total += amt or 0
        bits = [a.date.strftime("%d-%m-%Y") if a.date else "", a.filename, a.from_name]
        if f.get("doc_no"):
            bits.append(f"#{f['doc_no']}")
        if amt is not None:
            bits.append(f"{f.get('currency') or ''} {amt:,.2f}".strip())
        lines.append(" | ".join(b for b in bits if b))
    if total:
        lines.append(f"Total: {total:,.2f} ({len(atts)} files)")
    return "\n".join(lines)


def eml_draft(files: list[tuple[str, bytes, str]], to: str = "", subject: str = "", body: str = "",
              sender: str = "") -> bytes:
    """An unsent email (.eml with X-Unsent) - Outlook / Windows Mail open it as a new draft."""
    m = EmailMessage()
    m["X-Unsent"] = "1"
    if sender:
        m["From"] = sender
    if to:
        m["To"] = to
    m["Subject"] = subject or "Attachments"
    m["Date"] = formatdate(localtime=True)
    m.set_content(body or "Please find the attached files.")
    for name, data, mime in files:
        maintype, _, subtype = (mime or "application/octet-stream").partition("/")
        m.add_attachment(data, maintype=maintype or "application", subtype=subtype or "octet-stream", filename=name)
    return m.as_bytes()
