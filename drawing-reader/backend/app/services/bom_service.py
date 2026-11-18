"""Persist BOMs, roll up weights (drawing/revision/discipline/project) and rebuild BOQ summaries."""

from __future__ import annotations

from collections import defaultdict
from datetime import date
from decimal import Decimal
from typing import Any

from sqlalchemy import delete, func, select
from sqlalchemy.orm import Session

from app.core.config import settings
from app.models import (
    BOMHeader,
    BOMItem,
    BOQSummary,
    Discipline,
    Drawing,
    DrawingRevision,
    MaterialRate,
    WeightHistory,
)
from app.services.bom.extractor import BOMExtraction, ExtractedItem
from app.services.bom.weights import q3, reconcile_weights

STEEL_TOKENS = ("CS", "STEEL", "S355", "S275", "A36", "A572", "MS", "SS", "STAINLESS", "IS2062", "E250")


class RateBook:
    """Material rate lookup: exact (material, grade) -> material -> steel default -> 0."""

    def __init__(self, db: Session, on: date | None = None) -> None:
        on = on or date.today()
        rows = db.scalars(
            select(MaterialRate)
            .where(MaterialRate.is_deleted.is_(False), MaterialRate.effective_from <= on)
            .where((MaterialRate.effective_to.is_(None)) | (MaterialRate.effective_to >= on))
            .order_by(MaterialRate.effective_from)
        ).all()
        self._rates: dict[tuple[str, str], MaterialRate] = {}
        for r in rows:
            self._rates[(r.material.upper(), (r.grade or "").upper())] = r

    def lookup(self, material: str | None, grade: str | None) -> tuple[Decimal, str, str]:
        """Return (rate, basis, currency)."""
        mat = (material or "").upper()
        gr = (grade or "").upper()
        for key in ((mat, gr), (mat, "")):
            if key in self._rates:
                r = self._rates[key]
                return Decimal(r.rate), r.rate_basis, r.currency
        text = f"{mat} {gr}"
        if any(tok in text for tok in STEEL_TOKENS):
            return Decimal(str(settings.DEFAULT_STEEL_RATE_PER_KG)), "kg", settings.DEFAULT_CURRENCY
        return Decimal("0"), "kg", settings.DEFAULT_CURRENCY

    def cost(self, material: str | None, grade: str | None, weight_kg: Decimal, quantity: Decimal) -> Decimal:
        rate, basis, _ = self.lookup(material, grade)
        base = quantity if basis == "unit" else weight_kg
        return (rate * base).quantize(Decimal("0.01"))


def _item_from(data: ExtractedItem | dict[str, Any], line_no: int) -> BOMItem:
    d = data.as_dict() if isinstance(data, ExtractedItem) else dict(data)
    qty = Decimal(str(d.get("quantity") or 0))
    uw = d.get("unit_weight_kg")
    tw = d.get("total_weight_kg")
    uw = Decimal(str(uw)) if uw is not None else None
    tw = Decimal(str(tw)) if tw not in (None, 0, Decimal("0")) else None
    calculated = bool(d.get("weight_calculated"))
    if tw is None or uw is None:
        uw, tw_calc, calc = reconcile_weights(
            qty, uw, tw, d.get("description") or "", d.get("material"), d.get("grade"), d.get("unit")
        )
        tw = tw_calc
        calculated = calculated or calc
    return BOMItem(
        line_no=line_no,
        item_number=d.get("item_number"),
        part_number=d.get("part_number"),
        description=(d.get("description") or "")[:500],
        material=d.get("material"),
        grade=d.get("grade"),
        specification=d.get("specification"),
        quantity=qty,
        unit=(d.get("unit") or "EA")[:20],
        unit_weight_kg=uw,
        total_weight_kg=q3(tw or 0),
        sheet_no=d.get("sheet_no"),
        confidence=d.get("confidence"),
        weight_calculated=calculated,
    )


def save_bom(
    db: Session,
    revision: DrawingRevision,
    items: list[ExtractedItem] | list[dict[str, Any]],
    *,
    source: str,
    extraction: BOMExtraction | None = None,
    user_id: int | None = None,
) -> BOMHeader:
    """Replace the BOM of ``revision`` and roll weights up to revision, drawing and history."""
    header = db.scalar(select(BOMHeader).where(BOMHeader.revision_id == revision.id))
    if header is None:
        header = BOMHeader(drawing_id=revision.drawing_id, revision_id=revision.id, created_by_id=user_id)
        db.add(header)
        db.flush()
    else:
        header.items.clear()
        db.flush()
    header.items.extend(_item_from(it, n) for n, it in enumerate(items, 1))
    total = q3(sum((i.total_weight_kg for i in header.items), Decimal("0")))
    header.source = source
    header.item_count = len(header.items)
    header.total_weight_kg = total
    header.updated_by_id = user_id
    if extraction is not None:
        header.page_from, header.page_to = extraction.page_from, extraction.page_to
        header.source_total_weight_kg = extraction.source_total_weight_kg
        header.extraction_confidence = extraction.confidence
        header.weight_check_passed = extraction.weight_check_passed
    revision.total_weight_kg = total
    drawing = db.get(Drawing, revision.drawing_id)
    if drawing is not None and revision.is_current:
        drawing.current_weight_kg = total
    db.flush()
    if drawing is not None:
        record_weight_history(db, drawing, revision)
    return header


def record_weight_history(db: Session, drawing: Drawing, revision: DrawingRevision) -> WeightHistory:
    previous = db.scalar(
        select(DrawingRevision)
        .where(
            DrawingRevision.drawing_id == drawing.id,
            DrawingRevision.revision_seq < revision.revision_seq,
            DrawingRevision.is_deleted.is_(False),
        )
        .order_by(DrawingRevision.revision_seq.desc())
        .limit(1)
    )
    prev_weight = Decimal(previous.total_weight_kg) if previous else Decimal("0")
    entry = db.scalar(select(WeightHistory).where(WeightHistory.revision_id == revision.id))
    if entry is None:
        entry = WeightHistory(revision_id=revision.id, project_id=drawing.project_id, drawing_id=drawing.id)
        db.add(entry)
    entry.discipline_id = drawing.discipline_id
    entry.total_weight_kg = revision.total_weight_kg
    entry.delta_kg = q3(Decimal(revision.total_weight_kg) - prev_weight)
    entry.recorded_on = revision.revision_date or date.today()
    db.flush()
    return entry


def rebuild_boq(db: Session, project_id: int) -> int:
    """Aggregate current-revision BOMs of a project into ``boq_summary``. Returns row count."""
    rates = RateBook(db)
    rows = db.execute(
        select(
            Drawing.discipline_id,
            func.coalesce(BOMItem.material, "UNSPECIFIED"),
            func.coalesce(BOMItem.grade, ""),
            BOMItem.unit,
            func.sum(BOMItem.quantity),
            func.sum(BOMItem.total_weight_kg),
            func.count(func.distinct(Drawing.id)),
        )
        .join(BOMHeader, BOMHeader.id == BOMItem.bom_header_id)
        .join(DrawingRevision, DrawingRevision.id == BOMHeader.revision_id)
        .join(Drawing, Drawing.id == DrawingRevision.drawing_id)
        .where(
            Drawing.project_id == project_id,
            Drawing.is_deleted.is_(False),
            DrawingRevision.is_current.is_(True),
            BOMItem.is_deleted.is_(False),
        )
        .group_by(Drawing.discipline_id, BOMItem.material, BOMItem.grade, BOMItem.unit)
    ).all()
    db.execute(delete(BOQSummary).where(BOQSummary.project_id == project_id))
    merged: dict[tuple, list[Decimal | int]] = defaultdict(lambda: [Decimal(0), Decimal(0), 0])
    for disc, material, grade, unit, qty, weight, n in rows:
        key = (disc, material, grade, unit)
        merged[key][0] += Decimal(qty or 0)
        merged[key][1] += Decimal(weight or 0)
        merged[key][2] += n
    for (disc, material, grade, unit), (qty, weight, n) in merged.items():
        _, _, currency = rates.lookup(material, grade)
        db.add(
            BOQSummary(
                project_id=project_id,
                discipline_id=disc,
                material=material,
                grade=grade,
                unit=unit,
                total_quantity=qty,
                total_weight_kg=q3(weight),
                total_cost=rates.cost(material, grade, Decimal(weight), Decimal(qty)),
                currency=currency,
                drawing_count=int(n),
            )
        )
    db.flush()
    return len(merged)


def discipline_lookup(db: Session) -> dict[int, Discipline]:
    return {d.id: d for d in db.scalars(select(Discipline))}
