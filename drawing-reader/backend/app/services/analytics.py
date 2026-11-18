"""Dashboard and engineering analytics computed with aggregate SQL (no Python-side scans)."""

from __future__ import annotations

from collections import defaultdict
from decimal import Decimal

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.models import (
    BOMHeader,
    BOMItem,
    BOQSummary,
    CostImpact,
    Discipline,
    Drawing,
    DrawingFile,
    DrawingRevision,
    DrawingStatus,
    OCRJob,
    OCRJobStatus,
    OCRResult,
    Project,
    RevisionDifference,
    WeightHistory,
)
from app.services.bom_service import RateBook


def _month(db: Session, column):
    if db.bind is not None and db.bind.dialect.name == "postgresql":
        return func.to_char(column, "YYYY-MM")
    return func.strftime("%Y-%m", column)


def _live_drawings(project_id: int | None):
    stmt = select(Drawing.id).where(Drawing.is_deleted.is_(False))
    if project_id:
        stmt = stmt.where(Drawing.project_id == project_id)
    return stmt


def dashboard_summary(db: Session, project_id: int | None = None) -> dict:
    live = _live_drawings(project_id).subquery()
    total_drawings = db.scalar(select(func.count()).select_from(live)) or 0
    total_revisions = (
        db.scalar(
            select(func.count(DrawingRevision.id)).where(
                DrawingRevision.drawing_id.in_(select(live.c.id)), DrawingRevision.is_deleted.is_(False)
            )
        )
        or 0
    )
    total_weight = (
        db.scalar(
            select(func.coalesce(func.sum(Drawing.current_weight_kg), 0)).where(Drawing.id.in_(select(live.c.id)))
        )
        or 0
    )
    projects = (
        db.scalar(
            select(func.count(Project.id)).where(
                Project.is_deleted.is_(False), *([Project.id == project_id] if project_id else [])
            )
        )
        or 0
    )
    job_scope = select(DrawingFile.id).where(DrawingFile.drawing_id.in_(select(live.c.id)))
    pending = (
        db.scalar(
            select(func.count(OCRJob.id)).where(
                OCRJob.status.in_([OCRJobStatus.QUEUED, OCRJobStatus.RUNNING]), OCRJob.drawing_file_id.in_(job_scope)
            )
        )
        or 0
    )
    failed = (
        db.scalar(
            select(func.count(OCRJob.id)).where(
                OCRJob.status == OCRJobStatus.FAILED, OCRJob.drawing_file_id.in_(job_scope)
            )
        )
        or 0
    )
    review = (
        db.scalar(
            select(func.count(OCRResult.id)).where(
                OCRResult.needs_review.is_(True), OCRResult.drawing_file_id.in_(job_scope)
            )
        )
        or 0
    )
    ifc = (
        db.scalar(
            select(func.count())
            .select_from(Drawing)
            .where(Drawing.id.in_(select(live.c.id)), Drawing.status.in_([DrawingStatus.IFC, DrawingStatus.AFC]))
        )
        or 0
    )
    bom_items = (
        db.scalar(
            select(func.count(BOMItem.id))
            .join(BOMHeader, BOMHeader.id == BOMItem.bom_header_id)
            .where(BOMHeader.drawing_id.in_(select(live.c.id)), BOMItem.is_deleted.is_(False))
        )
        or 0
    )
    weight = float(total_weight)
    return {
        "total_drawings": total_drawings,
        "total_revisions": total_revisions,
        "total_weight_kg": round(weight, 3),
        "total_weight_t": round(weight / 1000, 3),
        "projects": projects,
        "pending_ocr": pending,
        "failed_ocr": failed,
        "ocr_needs_review": review,
        "ifc_drawings": ifc,
        "bom_items": bom_items,
    }


def dashboard_charts(db: Session, project_id: int | None = None) -> dict:
    base = select(Drawing).where(Drawing.is_deleted.is_(False))
    if project_id:
        base = base.where(Drawing.project_id == project_id)
    sub = base.subquery()
    by_disc = db.execute(
        select(
            func.coalesce(Discipline.code, "N/A"),
            func.count(sub.c.id),
            func.coalesce(func.sum(sub.c.current_weight_kg), 0),
            Discipline.color,
        )
        .select_from(sub)
        .outerjoin(Discipline, Discipline.id == sub.c.discipline_id)
        .group_by(Discipline.code, Discipline.color)
        .order_by(func.count(sub.c.id).desc())
    ).all()
    month_rev = _month(db, DrawingRevision.revision_date)
    rev_trend = db.execute(
        select(month_rev, func.count(DrawingRevision.id))
        .where(DrawingRevision.drawing_id.in_(select(sub.c.id)), DrawingRevision.revision_date.is_not(None))
        .group_by(month_rev)
        .order_by(month_rev)
    ).all()
    month_up = _month(db, DrawingFile.created_at)
    uploads = db.execute(
        select(month_up, func.count(DrawingFile.id))
        .where(DrawingFile.drawing_id.in_(select(sub.c.id)), DrawingFile.is_deleted.is_(False))
        .group_by(month_up)
        .order_by(month_up)
    ).all()
    statuses = db.execute(
        select(sub.c.status, func.count(sub.c.id)).group_by(sub.c.status).order_by(func.count(sub.c.id).desc())
    ).all()
    return {
        "drawings_by_discipline": [{"label": c, "value": n, "extra": {"color": color}} for c, n, _, color in by_disc],
        "weight_by_discipline": [
            {"label": c, "value": round(float(w) / 1000, 3), "extra": {"unit": "t", "color": color}}
            for c, _, w, color in sorted(by_disc, key=lambda r: -float(r[2]))
        ],
        "revision_trend": [{"label": m, "value": n} for m, n in rev_trend if m],
        "monthly_upload_trend": [{"label": m, "value": n} for m, n in uploads if m],
        "status_breakdown": [{"label": s, "value": n} for s, n in statuses],
    }


def material_summary(
    db: Session, project_id: int | None = None, discipline_id: int | None = None, current_only: bool = True
) -> list[dict]:
    stmt = (
        select(
            func.coalesce(BOMItem.material, "UNSPECIFIED"),
            BOMItem.grade,
            BOMItem.unit,
            func.sum(BOMItem.quantity),
            func.sum(BOMItem.total_weight_kg),
            func.count(BOMItem.id),
            func.count(func.distinct(BOMHeader.drawing_id)),
        )
        .join(BOMHeader, BOMHeader.id == BOMItem.bom_header_id)
        .join(DrawingRevision, DrawingRevision.id == BOMHeader.revision_id)
        .join(Drawing, Drawing.id == BOMHeader.drawing_id)
        .where(BOMItem.is_deleted.is_(False), Drawing.is_deleted.is_(False))
        .group_by(BOMItem.material, BOMItem.grade, BOMItem.unit)
        .order_by(func.sum(BOMItem.total_weight_kg).desc())
    )
    if current_only:
        stmt = stmt.where(DrawingRevision.is_current.is_(True))
    if project_id:
        stmt = stmt.where(Drawing.project_id == project_id)
    if discipline_id:
        stmt = stmt.where(Drawing.discipline_id == discipline_id)
    rates = RateBook(db)
    out = []
    for material, grade, unit, qty, weight, n, drawings in db.execute(stmt).all():
        qty_d, w_d = Decimal(qty or 0), Decimal(weight or 0)
        out.append(
            {
                "material": material,
                "grade": grade,
                "unit": unit,
                "total_quantity": qty_d,
                "total_weight_kg": w_d,
                "item_count": n,
                "drawing_count": drawings,
                "estimated_cost": rates.cost(material, grade, w_d, qty_d),
            }
        )
    return out


GROUPS = {"discipline", "project", "area", "drawing", "status"}


def weight_summary(db: Session, group_by: str = "discipline", project_id: int | None = None) -> dict:
    from app.models import Area

    stmt = select(Drawing).where(Drawing.is_deleted.is_(False))
    if project_id:
        stmt = stmt.where(Drawing.project_id == project_id)
    sub = stmt.subquery()
    if group_by == "project":
        key, label, join = sub.c.project_id, Project.code, (Project, Project.id == sub.c.project_id)
    elif group_by == "area":
        key, label, join = sub.c.area_id, Area.code, (Area, Area.id == sub.c.area_id)
    elif group_by == "drawing":
        key, label, join = sub.c.id, sub.c.drawing_number, None
    elif group_by == "status":
        key, label, join = sub.c.status, sub.c.status, None
    else:
        key, label, join = sub.c.discipline_id, Discipline.code, (Discipline, Discipline.id == sub.c.discipline_id)
    q = select(key, label, func.count(sub.c.id), func.coalesce(func.sum(sub.c.current_weight_kg), 0)).select_from(sub)
    if join is not None:
        q = q.outerjoin(*join)
    q = q.group_by(key, label).order_by(func.sum(sub.c.current_weight_kg).desc())
    if group_by == "drawing":
        q = q.limit(500)
    rows = []
    total = Decimal(0)
    for k, lab, n, w in db.execute(q).all():
        w = Decimal(w or 0)
        total += w
        rows.append(
            {
                "group_key": str(k),
                "group_label": lab or "Unassigned",
                "drawing_count": n,
                "total_weight_kg": w,
                "total_weight_t": round(float(w) / 1000, 3),
            }
        )
    return {"group_by": group_by, "total_weight_kg": total, "rows": rows}


def material_analysis(db: Session, project_id: int | None = None) -> list[dict]:
    rows = material_summary(db, project_id)
    merged: dict[tuple, dict] = {}
    for r in rows:
        key = (r["material"], r["grade"])
        m = merged.setdefault(
            key,
            {
                "material": r["material"],
                "grade": r["grade"],
                "total_weight_kg": 0.0,
                "total_quantity": 0.0,
                "estimated_cost": 0.0,
                "drawing_count": 0,
            },
        )
        m["total_weight_kg"] += float(r["total_weight_kg"])
        m["total_quantity"] += float(r["total_quantity"])
        m["estimated_cost"] += float(r["estimated_cost"])
        m["drawing_count"] = max(m["drawing_count"], r["drawing_count"])
    total = sum(m["total_weight_kg"] for m in merged.values()) or 1.0
    out = sorted(merged.values(), key=lambda m: -m["total_weight_kg"])
    for m in out:
        m["share_pct"] = round(m["total_weight_kg"] / total * 100, 2)
        m["total_weight_kg"] = round(m["total_weight_kg"], 3)
        m["estimated_cost"] = round(m["estimated_cost"], 2)
    return out


def weight_growth(db: Session, project_id: int | None = None, group_by: str = "project") -> list[dict]:
    """Monthly weight deltas from WeightHistory with cumulative totals per series."""
    period = _month(db, WeightHistory.recorded_on)
    if group_by == "discipline":
        series_col = func.coalesce(Discipline.code, "N/A")
    else:
        series_col = Project.code
    stmt = (
        select(series_col, period, func.sum(WeightHistory.delta_kg), func.count(WeightHistory.id))
        .select_from(WeightHistory)
        .join(Project, Project.id == WeightHistory.project_id)
        .outerjoin(Discipline, Discipline.id == WeightHistory.discipline_id)
        .group_by(series_col, period)
        .order_by(series_col, period)
    )
    if project_id:
        stmt = stmt.where(WeightHistory.project_id == project_id)
    series: dict[str, list[dict]] = defaultdict(list)
    for key, per, delta, n in db.execute(stmt).all():
        pts = series[key]
        cumulative = (pts[-1]["cumulative_kg"] if pts else 0.0) + float(delta or 0)
        pts.append(
            {
                "period": per,
                "weight_kg": round(cumulative, 3),
                "delta_kg": round(float(delta or 0), 3),
                "cumulative_kg": round(cumulative, 3),
                "revisions": n,
            }
        )
    return [{"key": k, "points": v} for k, v in series.items()]


def cost_impact_summary(db: Session, project_id: int | None = None, limit: int = 100) -> list[dict]:
    from sqlalchemy.orm import aliased

    fr, to = aliased(DrawingRevision), aliased(DrawingRevision)
    stmt = (
        select(RevisionDifference, Drawing.drawing_number, fr.revision, to.revision)
        .join(Drawing, Drawing.id == RevisionDifference.drawing_id)
        .join(fr, fr.id == RevisionDifference.from_revision_id)
        .join(to, to.id == RevisionDifference.to_revision_id)
        .where(Drawing.is_deleted.is_(False))
        .order_by(func.abs(RevisionDifference.cost_delta).desc())
        .limit(limit)
    )
    if project_id:
        stmt = stmt.where(Drawing.project_id == project_id)
    return [
        {
            "drawing_id": d.drawing_id,
            "drawing_number": num,
            "from_revision": a,
            "to_revision": b,
            "weight_delta_kg": float(d.weight_delta_kg),
            "cost_delta": float(d.cost_delta),
            "currency": d.currency,
        }
        for d, num, a, b in db.execute(stmt).all()
    ]


def boq_rows(db: Session, project_id: int | None = None) -> list[dict]:
    stmt = (
        select(BOQSummary, Discipline.code)
        .outerjoin(Discipline, Discipline.id == BOQSummary.discipline_id)
        .order_by(BOQSummary.total_weight_kg.desc())
    )
    if project_id:
        stmt = stmt.where(BOQSummary.project_id == project_id)
    return [
        {
            "project_id": b.project_id,
            "discipline": code,
            "material": b.material,
            "grade": b.grade,
            "unit": b.unit,
            "total_quantity": b.total_quantity,
            "total_weight_kg": b.total_weight_kg,
            "total_cost": b.total_cost,
            "currency": b.currency,
            "drawing_count": b.drawing_count,
        }
        for b, code in db.execute(stmt).all()
    ]


def procurement_impact(db: Session, project_id: int | None = None) -> list[dict]:
    stmt = (
        select(
            CostImpact.material,
            CostImpact.grade,
            func.sum(CostImpact.weight_delta_kg),
            func.sum(CostImpact.cost_delta),
            CostImpact.currency,
            func.count(func.distinct(CostImpact.drawing_id)),
        )
        .group_by(CostImpact.material, CostImpact.grade, CostImpact.currency)
        .order_by(func.sum(CostImpact.cost_delta).desc())
    )
    if project_id:
        stmt = stmt.where(CostImpact.project_id == project_id)
    return [
        {
            "material": m,
            "grade": g,
            "weight_delta_kg": round(float(w or 0), 3),
            "cost_delta": round(float(c or 0), 2),
            "currency": cur,
            "drawings": n,
        }
        for m, g, w, c, cur, n in db.execute(stmt).all()
    ]


__all__ = [
    "boq_rows",
    "cost_impact_summary",
    "dashboard_charts",
    "dashboard_summary",
    "material_analysis",
    "material_summary",
    "procurement_impact",
    "weight_growth",
    "weight_summary",
]
