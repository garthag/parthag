from __future__ import annotations

from datetime import date
from typing import Any

from app.schemas.common import BaseSchema, DecimalFloat


class RevisionListItem(BaseSchema):
    id: int
    drawing_id: int
    drawing_number: str
    title: str
    project_id: int
    discipline_code: str | None
    revision: str
    revision_seq: int
    revision_date: date | None
    status: str
    is_current: bool
    total_weight_kg: DecimalFloat
    bom_item_count: int = 0


class FieldChange(BaseSchema):
    field: str
    old: Any
    new: Any


class ItemDiff(BaseSchema):
    key: str
    change_type: str  # added | removed | modified
    item_number: str | None = None
    part_number: str | None = None
    description: str | None = None
    material: str | None = None
    grade: str | None = None
    old_quantity: float | None = None
    new_quantity: float | None = None
    quantity_delta: float = 0
    old_weight_kg: float | None = None
    new_weight_kg: float | None = None
    weight_delta_kg: float = 0
    cost_delta: float = 0
    categories: list[str] = []
    changes: list[FieldChange] = []


class CostImpactRow(BaseSchema):
    material: str
    grade: str
    quantity_delta: float
    weight_delta_kg: float
    rate: float
    cost_delta: float
    currency: str


class RevisionComparison(BaseSchema):
    difference_id: int | None = None
    drawing_id: int
    drawing_number: str
    from_revision: str
    to_revision: str
    from_revision_id: int
    to_revision_id: int
    from_weight_kg: float
    to_weight_kg: float
    weight_delta_kg: float
    weight_delta_pct: float | None
    cost_delta: float
    currency: str
    summary: dict[str, int]
    added: list[ItemDiff]
    removed: list[ItemDiff]
    modified: list[ItemDiff]
    unchanged_count: int
    cost_impacts: list[CostImpactRow]
    metadata_changes: list[FieldChange] = []


class VisualDiffRegion(BaseSchema):
    x: float
    y: float
    width: float
    height: float
    kind: str  # change | cloud
    score: float = 1.0


class VisualDiff(BaseSchema):
    page: int
    from_revision_id: int
    to_revision_id: int
    regions: list[VisualDiffRegion]
    changed_ratio: float
