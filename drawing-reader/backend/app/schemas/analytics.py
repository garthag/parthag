from __future__ import annotations

from typing import Any

from pydantic import Field

from app.schemas.common import BaseSchema


class DashboardSummary(BaseSchema):
    total_drawings: int
    total_revisions: int
    total_weight_kg: float
    total_weight_t: float
    projects: int
    pending_ocr: int
    failed_ocr: int
    ocr_needs_review: int
    ifc_drawings: int
    bom_items: int


class ChartPoint(BaseSchema):
    label: str
    value: float
    extra: dict[str, Any] = {}


class DashboardCharts(BaseSchema):
    drawings_by_discipline: list[ChartPoint]
    weight_by_discipline: list[ChartPoint]
    revision_trend: list[ChartPoint]
    monthly_upload_trend: list[ChartPoint]
    status_breakdown: list[ChartPoint]


class MaterialAnalysisRow(BaseSchema):
    material: str
    grade: str | None
    total_weight_kg: float
    total_quantity: float
    share_pct: float
    estimated_cost: float
    drawing_count: int


class WeightGrowthPoint(BaseSchema):
    period: str
    weight_kg: float
    delta_kg: float
    cumulative_kg: float
    revisions: int


class WeightGrowthSeries(BaseSchema):
    key: str
    points: list[WeightGrowthPoint]


class CostImpactSummaryRow(BaseSchema):
    drawing_id: int
    drawing_number: str
    from_revision: str
    to_revision: str
    weight_delta_kg: float
    cost_delta: float
    currency: str


class AIQueryRequest(BaseSchema):
    question: str = Field(
        min_length=3, max_length=1000, examples=["What changed between R2 and R3 on NRP-ST-A01-GA-0001?"]
    )
    project_id: int | None = None


class AIQueryResponse(BaseSchema):
    question: str
    intent: str
    answer: str
    columns: list[str]
    rows: list[dict[str, Any]]
    sql: str | None
    parameters: dict[str, Any] = {}
    elapsed_ms: int
    suggestions: list[str] = []
