from __future__ import annotations

from decimal import Decimal

from pydantic import Field

from app.schemas.common import BaseSchema, DecimalFloat


class BOMItemOut(BaseSchema):
    id: int
    bom_header_id: int
    line_no: int
    item_number: str | None
    part_number: str | None
    description: str
    material: str | None
    grade: str | None
    specification: str | None
    quantity: DecimalFloat
    unit: str
    unit_weight_kg: DecimalFloat | None
    total_weight_kg: DecimalFloat
    sheet_no: int | None
    confidence: float | None
    weight_calculated: bool
    drawing_id: int | None = None
    drawing_number: str | None = None
    revision: str | None = None


class BOMItemIn(BaseSchema):
    item_number: str | None = Field(default=None, max_length=20)
    part_number: str | None = Field(default=None, max_length=120)
    description: str = Field(min_length=1, max_length=500)
    material: str | None = None
    grade: str | None = None
    specification: str | None = None
    quantity: Decimal = Field(ge=0, examples=[4])
    unit: str = Field(default="EA", max_length=20)
    unit_weight_kg: Decimal | None = Field(default=None, ge=0)
    total_weight_kg: Decimal | None = Field(default=None, ge=0)


class BOMReplaceRequest(BaseSchema):
    items: list[BOMItemIn] = Field(max_length=5000)


class MaterialSummaryRow(BaseSchema):
    material: str
    grade: str | None
    unit: str
    total_quantity: DecimalFloat
    total_weight_kg: DecimalFloat
    item_count: int
    drawing_count: int
    estimated_cost: DecimalFloat = Decimal("0")


class WeightSummaryRow(BaseSchema):
    group_key: str
    group_label: str
    drawing_count: int
    total_weight_kg: DecimalFloat
    total_weight_t: float


class WeightSummary(BaseSchema):
    group_by: str
    total_weight_kg: DecimalFloat
    rows: list[WeightSummaryRow]


class BOQRow(BaseSchema):
    project_id: int
    discipline: str | None
    material: str
    grade: str
    unit: str
    total_quantity: DecimalFloat
    total_weight_kg: DecimalFloat
    total_cost: DecimalFloat
    currency: str
    drawing_count: int


class MaterialRateIn(BaseSchema):
    material: str = Field(min_length=1, max_length=120)
    grade: str = ""
    rate: Decimal = Field(gt=0)
    rate_basis: str = Field(default="kg", pattern="^(kg|unit)$")
    currency: str = Field(default="USD", min_length=3, max_length=3)


class MaterialRateOut(MaterialRateIn):
    id: int
