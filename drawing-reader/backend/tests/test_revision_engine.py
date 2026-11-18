"""Revision intelligence: matching, change classification, weight and cost deltas."""

from __future__ import annotations

from datetime import date
from decimal import Decimal

import pytest
from sqlalchemy import select
from sqlalchemy.orm import Session

from app.core.exceptions import NotFoundError, ValidationFailedError
from app.models import Drawing, DrawingRevision
from app.services.bom_service import RateBook
from app.services.revision_engine import ItemView, compare_items, compare_revisions, resolve_revision


class FakeRates(RateBook):
    def __init__(self) -> None:  # no DB
        self._rates = {}

    def lookup(self, material, grade):
        if (grade or "") == "S355JR":
            return Decimal("3.00"), "kg", "USD"
        if material == "BOLT":
            return Decimal("1.50"), "unit", "USD"
        return Decimal("2.00"), "kg", "USD"


def item(
    part: str | None,
    qty: float,
    uw: float,
    *,
    grade: str = "S275JR",
    spec: str = "EN 10025",
    desc: str = "BEAM",
    material: str = "CS",
    number: str = "1",
) -> ItemView:
    q, u = Decimal(str(qty)), Decimal(str(uw))
    return ItemView(number, part, desc, material, grade, spec, q, "EA", u, q * u)


def test_added_removed_modified_and_unchanged() -> None:
    old = [item("A", 2, 100), item("B", 1, 50), item("C", 4, 10), item("D", 1, 5)]
    new = [
        item("A", 3, 100),  # quantity change
        item("B", 1, 50, grade="S355JR"),  # material (grade) change
        item("C", 4, 10, spec="ASTM A6"),  # specification change
        item("E", 2, 25),  # added
    ]  # D removed
    result = compare_items(old, new, FakeRates())
    assert result.summary["added"] == 1 and result.summary["removed"] == 1 and result.summary["modified"] == 3
    assert result.summary["quantity_changes"] == 1
    assert result.summary["material_changes"] == 1
    assert result.summary["specification_changes"] == 1
    assert result.from_weight == Decimal("295.000")
    assert result.to_weight == Decimal("440.000")
    assert result.weight_delta == Decimal("145.000")
    mod_a = next(m for m in result.modified if m["part_number"] == "A")
    assert mod_a["quantity_delta"] == 1 and mod_a["weight_delta_kg"] == 100
    assert {c["field"] for c in mod_a["changes"]} == {"quantity", "total_weight_kg"}
    # cost: +100 kg @2, B moves 50 kg from @2 to @3 (+50), +50 kg added @2, -5 kg removed @2
    assert result.cost_delta == Decimal("200") + Decimal("50") + Decimal("100") - Decimal("10")
    by_grade = {(c["material"], c["grade"]): c for c in result.cost_impacts}
    assert by_grade[("CS", "S355JR")]["weight_delta_kg"] == 50
    assert by_grade[("CS", "S275JR")]["weight_delta_kg"] == 95


def test_fuzzy_match_without_part_numbers() -> None:
    old = [item(None, 2, 10, desc="PLATE 10 THK 200 x 300 GALV")]
    new = [item(None, 2, 10, desc="PLATE 10THK 200 x 300 GALV")]
    result = compare_items(old, new, FakeRates())
    assert result.summary["modified"] == 1 and result.summary["added"] == 0
    assert result.modified[0]["categories"] == ["description"]


def test_unit_rate_basis_and_identical() -> None:
    old = [item("X", 10, 0.2, material="BOLT")]
    new = [item("X", 12, 0.2, material="BOLT")]
    result = compare_items(old, new, FakeRates())
    assert result.cost_delta == Decimal("3.00")  # 2 units @ 1.50
    same = compare_items(old, old, FakeRates())
    assert same.unchanged_count == 1 and same.cost_impacts == [] and same.weight_delta == 0


def test_item_view_from_dict() -> None:
    v = ItemView.from_dict({"part_number": "A", "quantity": 2, "total_weight_kg": "3.5", "unit_weight_kg": None})
    assert v.quantity == 2 and v.total_weight_kg == Decimal("3.5") and v.unit_weight_kg is None


def _drawing_with_revisions(db: Session) -> Drawing:
    drawings = db.scalars(select(Drawing)).all()
    return next(d for d in drawings if len(d.revisions) >= 2)


def test_compare_revisions_persists_and_matches_bom(db: Session) -> None:
    drawing = _drawing_with_revisions(db)
    revs = sorted(drawing.revisions, key=lambda r: r.revision_seq)
    cmp = compare_revisions(db, revs[0], revs[-1])
    assert cmp["from_weight_kg"] == pytest.approx(float(revs[0].total_weight_kg))
    assert cmp["to_weight_kg"] == pytest.approx(float(revs[-1].total_weight_kg))
    assert cmp["weight_delta_kg"] == pytest.approx(float(revs[-1].total_weight_kg - revs[0].total_weight_kg), abs=1e-3)
    assert cmp["difference_id"] is not None
    item_delta = sum(d["weight_delta_kg"] for d in cmp["added"] + cmp["removed"] + cmp["modified"])
    assert item_delta == pytest.approx(cmp["weight_delta_kg"], abs=0.01)
    assert sum(c["cost_delta"] for c in cmp["cost_impacts"]) == pytest.approx(cmp["cost_delta"], abs=0.01)


def test_compare_validation(db: Session) -> None:
    drawing = _drawing_with_revisions(db)
    rev = drawing.revisions[0]
    with pytest.raises(ValidationFailedError):
        compare_revisions(db, rev, rev)
    other = db.scalars(select(DrawingRevision).where(DrawingRevision.drawing_id != drawing.id)).first()
    with pytest.raises(ValidationFailedError):
        compare_revisions(db, rev, other)


def test_resolve_revision_variants(db: Session) -> None:
    drawing = _drawing_with_revisions(db)
    rev = drawing.revisions[0]
    assert resolve_revision(db, None, rev.id).id == rev.id
    assert resolve_revision(db, drawing.id, f"Rev {rev.revision}").id == rev.id
    numeric = next((r for r in drawing.revisions if r.revision.isdigit()), None)
    if numeric is not None:
        assert resolve_revision(db, drawing.id, f"R{numeric.revision}").id == numeric.id
    with pytest.raises(NotFoundError):
        resolve_revision(db, drawing.id, "ZZ")
    with pytest.raises(NotFoundError):
        resolve_revision(db, None, 999_999)


def test_revision_dates_are_ordered(db: Session) -> None:
    drawing = _drawing_with_revisions(db)
    revs = sorted(drawing.revisions, key=lambda r: r.revision_seq)
    assert all(isinstance(r.revision_date, date) for r in revs)
    assert sum(r.is_current for r in revs) == 1 and revs[-1].is_current
