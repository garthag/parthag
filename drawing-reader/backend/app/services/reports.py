"""Report builders (register, revision difference, BOQ, MTO, cost impact, weight growth) and exporters.

Every report is a ``Report`` (title + columns + row iterator) built from the same service functions
the dashboard uses, so exported numbers match on-screen values. Exporters stream:
CSV row-by-row, Excel via openpyxl write-only mode, PDF via ReportLab platypus.
"""

from __future__ import annotations

import csv
import io
from collections.abc import Iterable, Iterator
from dataclasses import dataclass
from datetime import UTC, datetime
from decimal import Decimal
from typing import Any

from sqlalchemy import select
from sqlalchemy.orm import Session

from app.core.exceptions import ValidationFailedError
from app.models import Area, Discipline, Drawing, Project
from app.services import analytics
from app.services.revision_engine import compare_revisions, resolve_revision

REPORT_TYPES = ("drawing-register", "revision-difference", "boq", "mto", "cost-impact", "weight-growth")
FORMATS = {
    "csv": "text/csv",
    "xlsx": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    "pdf": "application/pdf",
}


@dataclass
class Report:
    title: str
    columns: list[str]
    rows: Iterable[list[Any]]
    subtitle: str = ""
    totals: list[Any] | None = None


def _cell(v: Any) -> Any:
    if isinstance(v, Decimal):
        return float(v)
    if hasattr(v, "isoformat"):
        return v.isoformat()
    return v


def drawing_register(db: Session, project_id: int | None = None) -> Report:
    stmt = (
        select(Drawing, Project.code, Discipline.code, Area.code)
        .join(Project, Project.id == Drawing.project_id)
        .outerjoin(Discipline, Discipline.id == Drawing.discipline_id)
        .outerjoin(Area, Area.id == Drawing.area_id)
        .where(Drawing.is_deleted.is_(False))
        .order_by(Project.code, Drawing.drawing_number)
        .execution_options(yield_per=1000)
    )
    if project_id:
        stmt = stmt.where(Drawing.project_id == project_id)

    def rows() -> Iterator[list[Any]]:
        for d, p, disc, area in db.execute(stmt):
            yield [
                p,
                d.drawing_number,
                d.title,
                disc,
                area,
                d.drawing_type,
                d.current_revision,
                d.status,
                d.scale,
                d.sheet_number,
                d.vendor,
                float(d.current_weight_kg or 0),
                d.updated_at.date().isoformat(),
            ]

    return Report(
        "Drawing Register",
        [
            "Project",
            "Drawing No",
            "Title",
            "Discipline",
            "Area",
            "Type",
            "Rev",
            "Status",
            "Scale",
            "Sheet",
            "Vendor",
            "Weight (kg)",
            "Updated",
        ],
        rows(),
    )


def revision_difference(db: Session, drawing_id: int, from_rev: str, to_rev: str) -> Report:
    a = resolve_revision(db, drawing_id, from_rev)
    b = resolve_revision(db, drawing_id, to_rev)
    cmp = compare_revisions(db, a, b)
    rows = [
        [
            d["change_type"].upper(),
            d["item_number"],
            d["part_number"],
            d["description"],
            d["material"],
            d["grade"],
            d["old_quantity"],
            d["new_quantity"],
            d["old_weight_kg"],
            d["new_weight_kg"],
            d["weight_delta_kg"],
            d["cost_delta"],
            "; ".join(f"{c['field']}: {c['old']} → {c['new']}" for c in d["changes"]),
        ]
        for d in cmp["added"] + cmp["removed"] + cmp["modified"]
    ]
    return Report(
        "Revision Difference Report",
        [
            "Change",
            "Item",
            "Part No",
            "Description",
            "Material",
            "Grade",
            "Old Qty",
            "New Qty",
            "Old Wt (kg)",
            "New Wt (kg)",
            "Δ Wt (kg)",
            f"Δ Cost ({cmp['currency']})",
            "Details",
        ],
        rows,
        subtitle=(
            f"{cmp['drawing_number']}  Rev {cmp['from_revision']} → {cmp['to_revision']}  |  "
            f"Weight {cmp['from_weight_kg']:,.1f} → {cmp['to_weight_kg']:,.1f} kg "
            f"(Δ {cmp['weight_delta_kg']:+,.1f})  |  Cost Δ {cmp['cost_delta']:+,.2f} {cmp['currency']}"
        ),
        totals=[
            "TOTAL",
            None,
            None,
            None,
            None,
            None,
            None,
            None,
            cmp["from_weight_kg"],
            cmp["to_weight_kg"],
            cmp["weight_delta_kg"],
            cmp["cost_delta"],
            None,
        ],
    )


def boq(db: Session, project_id: int | None = None) -> Report:
    data = analytics.boq_rows(db, project_id)
    rows = [
        [
            r["discipline"],
            r["material"],
            r["grade"],
            r["unit"],
            r["total_quantity"],
            r["total_weight_kg"],
            r["total_cost"],
            r["currency"],
            r["drawing_count"],
        ]
        for r in data
    ]
    totals = [
        "TOTAL",
        None,
        None,
        None,
        None,
        sum(float(r["total_weight_kg"]) for r in data),
        sum(float(r["total_cost"]) for r in data),
        None,
        None,
    ]
    return Report(
        "Bill of Quantities",
        ["Discipline", "Material", "Grade", "Unit", "Quantity", "Weight (kg)", "Cost", "Currency", "Drawings"],
        rows,
        totals=totals,
    )


def mto(db: Session, project_id: int | None = None) -> Report:
    data = analytics.material_summary(db, project_id)
    rows = [
        [
            r["material"],
            r["grade"],
            r["unit"],
            r["total_quantity"],
            r["total_weight_kg"],
            r["item_count"],
            r["drawing_count"],
            r["estimated_cost"],
        ]
        for r in data
    ]
    totals = [
        "TOTAL",
        None,
        None,
        None,
        sum(float(r["total_weight_kg"]) for r in data),
        None,
        None,
        sum(float(r["estimated_cost"]) for r in data),
    ]
    return Report(
        "Material Take-Off",
        ["Material", "Grade", "Unit", "Quantity", "Weight (kg)", "Lines", "Drawings", "Est. Cost"],
        rows,
        totals=totals,
    )


def cost_impact(db: Session, project_id: int | None = None) -> Report:
    data = analytics.cost_impact_summary(db, project_id, limit=100_000)
    rows = [
        [
            r["drawing_number"],
            r["from_revision"],
            r["to_revision"],
            r["weight_delta_kg"],
            r["cost_delta"],
            r["currency"],
        ]
        for r in data
    ]
    totals = ["TOTAL", None, None, sum(r["weight_delta_kg"] for r in data), sum(r["cost_delta"] for r in data), None]
    return Report(
        "Cost Impact Report",
        ["Drawing No", "From Rev", "To Rev", "Δ Weight (kg)", "Δ Cost", "Currency"],
        rows,
        totals=totals,
    )


def weight_growth(db: Session, project_id: int | None = None) -> Report:
    rows = [
        [s["key"], p["period"], p["delta_kg"], p["cumulative_kg"], p["revisions"]]
        for s in analytics.weight_growth(db, project_id)
        for p in s["points"]
    ]
    return Report("Weight Growth Report", ["Series", "Period", "Δ Weight (kg)", "Cumulative (kg)", "Revisions"], rows)


def build(db: Session, report_type: str, params: dict[str, Any]) -> Report:
    pid = params.get("project_id")
    if report_type == "drawing-register":
        return drawing_register(db, pid)
    if report_type == "revision-difference":
        if not (params.get("drawing_id") and params.get("from_revision") and params.get("to_revision")):
            raise ValidationFailedError("drawing_id, from_revision and to_revision are required")
        return revision_difference(db, params["drawing_id"], params["from_revision"], params["to_revision"])
    if report_type == "boq":
        return boq(db, pid)
    if report_type == "mto":
        return mto(db, pid)
    if report_type == "cost-impact":
        return cost_impact(db, pid)
    if report_type == "weight-growth":
        return weight_growth(db, pid)
    raise ValidationFailedError(f"Unknown report type '{report_type}'")


# ------------------------------------------------------------------ exporters


def to_csv(report: Report) -> Iterator[bytes]:
    buf = io.StringIO()
    writer = csv.writer(buf)
    yield "﻿".encode()  # BOM so Excel opens UTF-8 correctly
    writer.writerow(report.columns)
    for i, row in enumerate(report.rows):
        writer.writerow([_cell(v) for v in row])
        if i % 500 == 0:
            yield buf.getvalue().encode()
            buf.seek(0)
            buf.truncate()
    if report.totals:
        writer.writerow([_cell(v) for v in report.totals])
    yield buf.getvalue().encode()


def to_xlsx(report: Report) -> bytes:
    from openpyxl import Workbook
    from openpyxl.cell import WriteOnlyCell
    from openpyxl.styles import Font, PatternFill

    wb = Workbook(write_only=True)
    ws = wb.create_sheet(report.title[:31])
    bold = Font(bold=True, color="FFFFFF")
    fill = PatternFill("solid", fgColor="1F3A5F")
    title = WriteOnlyCell(ws, value=report.title)
    title.font = Font(bold=True, size=14)
    ws.append([title])
    if report.subtitle:
        ws.append([report.subtitle])
    ws.append([f"Generated {datetime.now(UTC):%Y-%m-%d %H:%M} UTC"])
    header = []
    for col in report.columns:
        c = WriteOnlyCell(ws, value=col)
        c.font, c.fill = bold, fill
        header.append(c)
    ws.append(header)
    for row in report.rows:
        ws.append([_cell(v) for v in row])
    if report.totals:
        ws.append([WriteOnlyCell(ws, value=_cell(v)) for v in report.totals])
    out = io.BytesIO()
    wb.save(out)
    return out.getvalue()


def to_pdf(report: Report) -> bytes:
    from reportlab.lib import colors
    from reportlab.lib.pagesizes import A3, landscape
    from reportlab.lib.styles import getSampleStyleSheet
    from reportlab.lib.units import mm
    from reportlab.platypus import LongTable, Paragraph, SimpleDocTemplate, Spacer, TableStyle

    out = io.BytesIO()
    doc = SimpleDocTemplate(
        out,
        pagesize=landscape(A3),
        leftMargin=12 * mm,
        rightMargin=12 * mm,
        topMargin=12 * mm,
        bottomMargin=12 * mm,
        title=report.title,
    )
    styles = getSampleStyleSheet()
    cell_style = styles["BodyText"].clone("cell", fontSize=7, leading=8.5)
    story = [Paragraph(report.title, styles["Title"])]
    if report.subtitle:
        story.append(Paragraph(report.subtitle, styles["Normal"]))
    story.append(Paragraph(f"Generated {datetime.now(UTC):%Y-%m-%d %H:%M} UTC", styles["Normal"]))
    story.append(Spacer(1, 4 * mm))

    def fmt(v: Any) -> Any:
        v = _cell(v)
        if isinstance(v, float):
            return f"{v:,.2f}"
        return Paragraph(str(v), cell_style) if isinstance(v, str) and len(v) > 25 else ("" if v is None else v)

    data = [report.columns] + [[fmt(v) for v in row] for row in report.rows]
    if report.totals:
        data.append([fmt(v) for v in report.totals])
    table = LongTable(data, repeatRows=1)
    style = [
        ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#1F3A5F")),
        ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
        ("FONTSIZE", (0, 0), (-1, -1), 7),
        ("GRID", (0, 0), (-1, -1), 0.25, colors.HexColor("#B0B7C3")),
        ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, colors.HexColor("#F2F5F9")]),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
    ]
    if report.totals:
        style.append(("FONTNAME", (0, -1), (-1, -1), "Helvetica-Bold"))
    table.setStyle(TableStyle(style))
    story.append(table)
    doc.build(story)
    return out.getvalue()
