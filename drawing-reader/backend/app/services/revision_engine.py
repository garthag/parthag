"""Revision intelligence: BOM-level comparison of two revisions with weight and cost impact."""

from __future__ import annotations

import difflib
import itertools
import re
from collections import defaultdict
from dataclasses import dataclass, field
from decimal import Decimal
from typing import Any

from sqlalchemy import select
from sqlalchemy.orm import Session, selectinload

from app.core.exceptions import NotFoundError, ValidationFailedError
from app.models import BOMHeader, BOMItem, CostImpact, Drawing, DrawingRevision, RevisionDifference
from app.services.bom.weights import q3
from app.services.bom_service import RateBook


@dataclass
class ItemView:
    item_number: str | None
    part_number: str | None
    description: str
    material: str | None
    grade: str | None
    specification: str | None
    quantity: Decimal
    unit: str
    unit_weight_kg: Decimal | None
    total_weight_kg: Decimal

    @classmethod
    def from_model(cls, item: BOMItem) -> ItemView:
        return cls(
            item.item_number,
            item.part_number,
            item.description or "",
            item.material,
            item.grade,
            item.specification,
            Decimal(item.quantity or 0),
            item.unit or "EA",
            Decimal(item.unit_weight_kg) if item.unit_weight_kg is not None else None,
            Decimal(item.total_weight_kg or 0),
        )

    @classmethod
    def from_dict(cls, d: dict[str, Any]) -> ItemView:
        def dec(v: Any) -> Decimal | None:
            return Decimal(str(v)) if v is not None else None

        return cls(
            d.get("item_number"),
            d.get("part_number"),
            d.get("description") or "",
            d.get("material"),
            d.get("grade"),
            d.get("specification"),
            dec(d.get("quantity")) or Decimal(0),
            d.get("unit") or "EA",
            dec(d.get("unit_weight_kg")),
            dec(d.get("total_weight_kg")) or Decimal(0),
        )


def _norm(text: str | None) -> str:
    return re.sub(r"\s+", " ", (text or "").upper()).strip()


def match_key(item: ItemView) -> str:
    if item.part_number:
        return f"P:{_norm(item.part_number)}"
    return f"D:{_norm(item.description)}|{_norm(item.material)}|{_norm(item.grade)}"


COMPARED_FIELDS = (
    ("quantity", "quantity"),
    ("unit", "quantity"),
    ("material", "material"),
    ("grade", "material"),
    ("specification", "specification"),
    ("description", "description"),
    ("unit_weight_kg", "weight"),
    ("total_weight_kg", "weight"),
)


def _as_json(v: Any) -> Any:
    return float(v) if isinstance(v, Decimal) else v


@dataclass
class ComparisonResult:
    added: list[dict[str, Any]] = field(default_factory=list)
    removed: list[dict[str, Any]] = field(default_factory=list)
    modified: list[dict[str, Any]] = field(default_factory=list)
    unchanged_count: int = 0
    from_weight: Decimal = Decimal(0)
    to_weight: Decimal = Decimal(0)
    cost_delta: Decimal = Decimal(0)
    cost_impacts: list[dict[str, Any]] = field(default_factory=list)

    @property
    def weight_delta(self) -> Decimal:
        return q3(self.to_weight - self.from_weight)

    @property
    def summary(self) -> dict[str, int]:
        counts: dict[str, int] = defaultdict(int)
        for m in self.modified:
            for c in m["categories"]:
                counts[c] += 1
        return {
            "added": len(self.added),
            "removed": len(self.removed),
            "modified": len(self.modified),
            "unchanged": self.unchanged_count,
            "quantity_changes": counts["quantity"],
            "material_changes": counts["material"],
            "specification_changes": counts["specification"],
            "weight_changes": counts["weight"],
            "description_changes": counts["description"],
        }


def _diff_entry(
    change: str,
    old: ItemView | None,
    new: ItemView | None,
    rates: RateBook,
    changes: list[dict] | None = None,
    categories: list[str] | None = None,
) -> dict[str, Any]:
    ref = new or old
    assert ref is not None
    old_w = old.total_weight_kg if old else Decimal(0)
    new_w = new.total_weight_kg if new else Decimal(0)
    old_q = old.quantity if old else Decimal(0)
    new_q = new.quantity if new else Decimal(0)
    # Cost of the change: new-state cost minus old-state cost (handles material swaps correctly).
    new_cost = rates.cost(new.material, new.grade, new_w, new_q) if new else Decimal(0)
    old_cost = rates.cost(old.material, old.grade, old_w, old_q) if old else Decimal(0)
    return {
        "key": match_key(ref),
        "change_type": change,
        "item_number": ref.item_number,
        "part_number": ref.part_number,
        "description": ref.description,
        "material": ref.material,
        "grade": ref.grade,
        "old_quantity": float(old_q) if old else None,
        "new_quantity": float(new_q) if new else None,
        "quantity_delta": float(new_q - old_q),
        "old_weight_kg": float(old_w) if old else None,
        "new_weight_kg": float(new_w) if new else None,
        "weight_delta_kg": float(q3(new_w - old_w)),
        "cost_delta": float(new_cost - old_cost),
        "categories": categories or [],
        "changes": changes or [],
    }


def compare_items(old_items: list[ItemView], new_items: list[ItemView], rates: RateBook) -> ComparisonResult:
    """Match BOM lines across revisions and classify differences.

    Matching: exact part number, else description+material+grade; leftover lines are paired by
    description similarity (>= 0.85) so renumbered or re-described items count as modified rather
    than removed + added.
    """
    result = ComparisonResult(
        from_weight=q3(sum((i.total_weight_kg for i in old_items), Decimal(0))),
        to_weight=q3(sum((i.total_weight_kg for i in new_items), Decimal(0))),
    )
    old_by_key: dict[str, list[ItemView]] = defaultdict(list)
    for it in old_items:
        old_by_key[match_key(it)].append(it)
    pairs: list[tuple[ItemView, ItemView]] = []
    unmatched_new: list[ItemView] = []
    for it in new_items:
        bucket = old_by_key.get(match_key(it))
        if bucket:
            pairs.append((bucket.pop(0), it))
        else:
            unmatched_new.append(it)
    unmatched_old = [it for bucket in old_by_key.values() for it in bucket]
    for new in list(unmatched_new):
        best, best_ratio = None, 0.0
        for old in unmatched_old:
            ratio = difflib.SequenceMatcher(None, _norm(old.description), _norm(new.description)).ratio()
            if old.part_number and new.part_number and _norm(old.part_number) != _norm(new.part_number):
                ratio -= 0.2
            if ratio > best_ratio:
                best, best_ratio = old, ratio
        if best is not None and best_ratio >= 0.85:
            pairs.append((best, new))
            unmatched_old.remove(best)
            unmatched_new.remove(new)

    for old, new in pairs:
        changes: list[dict] = []
        categories: list[str] = []
        for attr, category in COMPARED_FIELDS:
            a, b = getattr(old, attr), getattr(new, attr)
            if isinstance(a, str) or isinstance(b, str):
                differs = _norm(a) != _norm(b)
            else:
                differs = (a or Decimal(0)) != (b or Decimal(0))
            if differs:
                changes.append({"field": attr, "old": _as_json(a), "new": _as_json(b)})
                if category not in categories:
                    categories.append(category)
        if changes:
            result.modified.append(_diff_entry("modified", old, new, rates, changes, categories))
        else:
            result.unchanged_count += 1
    result.added = [_diff_entry("added", None, it, rates) for it in unmatched_new]
    result.removed = [_diff_entry("removed", it, None, rates) for it in unmatched_old]

    impacts: dict[tuple[str, str], dict[str, Decimal]] = defaultdict(
        lambda: {"q": Decimal(0), "w": Decimal(0), "c": Decimal(0)}
    )

    def book(item: ItemView, sign: int) -> None:
        key = (item.material or "UNSPECIFIED", item.grade or "")
        impacts[key]["q"] += sign * item.quantity
        impacts[key]["w"] += sign * item.total_weight_kg
        impacts[key]["c"] += sign * rates.cost(item.material, item.grade, item.total_weight_kg, item.quantity)

    for old, new in pairs:
        book(old, -1)
        book(new, +1)
    for it in unmatched_new:
        book(it, +1)
    for it in unmatched_old:
        book(it, -1)
    for (material, grade), v in sorted(impacts.items()):
        if v["w"] == 0 and v["q"] == 0 and v["c"] == 0:
            continue
        rate, _, currency = rates.lookup(material, grade)
        result.cost_impacts.append(
            {
                "material": material,
                "grade": grade,
                "quantity_delta": float(v["q"]),
                "weight_delta_kg": float(q3(v["w"])),
                "rate": float(rate),
                "cost_delta": float(v["c"].quantize(Decimal("0.01"))),
                "currency": currency,
            }
        )
    result.cost_delta = sum((Decimal(str(c["cost_delta"])) for c in result.cost_impacts), Decimal(0))
    return result


def _load_items(db: Session, revision: DrawingRevision) -> list[ItemView]:
    header = db.scalar(
        select(BOMHeader).options(selectinload(BOMHeader.items)).where(BOMHeader.revision_id == revision.id)
    )
    if header is None:
        return []
    return [ItemView.from_model(i) for i in header.items if not i.is_deleted]


def resolve_revision(db: Session, drawing_id: int | None, ref: str | int) -> DrawingRevision:
    """Accept a revision id or a revision code (with ``drawing_id``)."""
    rev: DrawingRevision | None = None
    if isinstance(ref, int) or (isinstance(ref, str) and ref.isdigit() and drawing_id is None):
        rev = db.get(DrawingRevision, int(ref))
    elif drawing_id is not None:
        code = re.sub(r"^REV\.?\s*", "", str(ref).upper()).strip()
        rev = db.scalar(
            select(DrawingRevision).where(DrawingRevision.drawing_id == drawing_id, DrawingRevision.revision == code)
        )
        if rev is None and code.startswith("R") and code[1:].isdigit():
            rev = db.scalar(
                select(DrawingRevision).where(
                    DrawingRevision.drawing_id == drawing_id, DrawingRevision.revision == code[1:]
                )
            )
    if rev is None or rev.is_deleted:
        raise NotFoundError(f"Revision {ref} not found")
    return rev


def compare_revisions(db: Session, from_rev: DrawingRevision, to_rev: DrawingRevision, *, persist: bool = True) -> dict:
    if from_rev.drawing_id != to_rev.drawing_id:
        raise ValidationFailedError("Revisions must belong to the same drawing")
    if from_rev.id == to_rev.id:
        raise ValidationFailedError("Select two different revisions")
    drawing = db.get(Drawing, from_rev.drawing_id)
    rates = RateBook(db)
    result = compare_items(_load_items(db, from_rev), _load_items(db, to_rev), rates)
    metadata_changes = [
        {"field": f, "old": _as_json(getattr(from_rev, f)), "new": _as_json(getattr(to_rev, f))}
        for f in ("status", "revision_date", "description", "checker", "approver")
        if getattr(from_rev, f) != getattr(to_rev, f)
    ]
    for mc in metadata_changes:
        if hasattr(mc["old"], "isoformat"):
            mc["old"] = mc["old"].isoformat()
        if hasattr(mc["new"], "isoformat"):
            mc["new"] = mc["new"].isoformat()
    currency = drawing.project.currency if drawing and drawing.project else "USD"
    difference_id = None
    if persist and drawing is not None:
        difference_id = _persist(db, drawing, from_rev, to_rev, result, currency)
    from_w, delta = result.from_weight, result.weight_delta
    return {
        "difference_id": difference_id,
        "drawing_id": from_rev.drawing_id,
        "drawing_number": drawing.drawing_number if drawing else "",
        "from_revision": from_rev.revision,
        "to_revision": to_rev.revision,
        "from_revision_id": from_rev.id,
        "to_revision_id": to_rev.id,
        "from_weight_kg": float(from_w),
        "to_weight_kg": float(result.to_weight),
        "weight_delta_kg": float(delta),
        "weight_delta_pct": round(float(delta / from_w * 100), 2) if from_w else None,
        "cost_delta": float(result.cost_delta),
        "currency": currency,
        "summary": result.summary,
        "added": result.added,
        "removed": result.removed,
        "modified": result.modified,
        "unchanged_count": result.unchanged_count,
        "cost_impacts": result.cost_impacts,
        "metadata_changes": metadata_changes,
    }


def _persist(
    db: Session,
    drawing: Drawing,
    from_rev: DrawingRevision,
    to_rev: DrawingRevision,
    result: ComparisonResult,
    currency: str,
) -> int:
    diff = db.scalar(
        select(RevisionDifference).where(
            RevisionDifference.from_revision_id == from_rev.id, RevisionDifference.to_revision_id == to_rev.id
        )
    )
    if diff is None:
        diff = RevisionDifference(drawing_id=drawing.id, from_revision_id=from_rev.id, to_revision_id=to_rev.id)
        db.add(diff)
    diff.added_count = len(result.added)
    diff.removed_count = len(result.removed)
    diff.modified_count = len(result.modified)
    diff.from_weight_kg = result.from_weight
    diff.to_weight_kg = result.to_weight
    diff.weight_delta_kg = result.weight_delta
    diff.cost_delta = result.cost_delta
    diff.currency = currency
    diff.details = {
        "summary": result.summary,
        "added": result.added,
        "removed": result.removed,
        "modified": result.modified,
    }
    diff.cost_impacts.clear()
    db.flush()
    for c in result.cost_impacts:
        diff.cost_impacts.append(
            CostImpact(
                project_id=drawing.project_id,
                drawing_id=drawing.id,
                material=c["material"],
                grade=c["grade"],
                quantity_delta=Decimal(str(c["quantity_delta"])),
                weight_delta_kg=Decimal(str(c["weight_delta_kg"])),
                rate=Decimal(str(c["rate"])),
                cost_delta=Decimal(str(c["cost_delta"])),
                currency=c["currency"],
            )
        )
    db.flush()
    return diff.id


def compare_consecutive(db: Session, drawing: Drawing) -> list[int]:
    """Compute and store differences for each consecutive revision pair of a drawing."""
    revs = sorted((r for r in drawing.revisions if not r.is_deleted), key=lambda r: r.revision_seq)
    return [compare_revisions(db, a, b)["difference_id"] for a, b in itertools.pairwise(revs)]
