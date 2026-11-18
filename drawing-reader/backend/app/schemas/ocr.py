from __future__ import annotations

from datetime import datetime
from typing import Any

from pydantic import Field

from app.schemas.common import BaseSchema


class OCRProcessRequest(BaseSchema):
    file_ids: list[int] = Field(default=[], max_length=5000, examples=[[1, 2, 3]])
    drawing_ids: list[int] = Field(default=[], max_length=5000)
    extract_bom: bool = True
    apply_to_register: bool = Field(default=True, description="Write extracted metadata onto the drawing record")


class OCRJobOut(BaseSchema):
    id: int
    drawing_file_id: int
    revision_id: int | None
    status: str
    engine: str | None
    progress: int
    error: str | None
    started_at: datetime | None
    finished_at: datetime | None
    duration_ms: int | None
    created_at: datetime
    result_id: int | None = None


class TitleBlockFields(BaseSchema):
    drawing_number: str | None = Field(default=None, examples=["NRP-ST-A01-GA-0001"])
    title: str | None = Field(default=None, examples=["PIPE RACK PR-01 GENERAL ARRANGEMENT"])
    revision: str | None = Field(default=None, examples=["C"])
    date: str | None = Field(default=None, examples=["2026-03-14"])
    status: str | None = Field(default=None, examples=["IFC"])
    scale: str | None = Field(default=None, examples=["1:50"])
    sheet_number: str | None = Field(default=None, examples=["1 OF 2"])
    drawn_by: str | None = None
    checker: str | None = None
    approver: str | None = None
    vendor: str | None = None
    project: str | None = None


class OCRResultOut(BaseSchema):
    id: int
    job_id: int
    drawing_file_id: int
    extracted: dict[str, Any]
    overrides: dict[str, Any]
    effective: dict[str, Any]
    field_confidences: dict[str, Any]
    overall_confidence: float
    revision_block: list[Any]
    notes: list[Any]
    regions: dict[str, Any]
    rotation: int
    is_scanned: bool
    engine: str | None
    is_overridden: bool
    needs_review: bool
    reviewed_at: datetime | None
    bom_item_count: int = 0


class OCROverrideRequest(TitleBlockFields):
    apply_to_register: bool = True
