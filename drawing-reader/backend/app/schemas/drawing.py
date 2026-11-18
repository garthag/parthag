from __future__ import annotations

from datetime import date, datetime
from typing import Any

from pydantic import Field

from app.models.drawing import DrawingStatus
from app.schemas.common import BaseSchema, DecimalFloat, TimestampedSchema

STATUS_PATTERN = "^(" + "|".join(s.replace("-", r"\-") for s in DrawingStatus.ALL) + ")$"


class DrawingBase(BaseSchema):
    drawing_number: str = Field(min_length=1, max_length=120, examples=["NRP-ST-A01-GA-0001"])
    title: str = Field(default="", max_length=500, examples=["PIPE RACK PR-01 GENERAL ARRANGEMENT"])
    drawing_type: str | None = Field(default=None, max_length=60, examples=["GA"])
    status: str = Field(default=DrawingStatus.PRELIMINARY, pattern=STATUS_PATTERN)
    scale: str | None = Field(default=None, max_length=40, examples=["1:50"])
    sheet_number: str | None = None
    total_sheets: int | None = Field(default=None, ge=1)
    vendor: str | None = None
    originator: str | None = None
    remarks: str | None = None


class DrawingCreate(DrawingBase):
    project_id: int
    discipline_id: int | None = None
    area_id: int | None = None
    system_id: int | None = None
    tags: list[str] = []


class DrawingUpdate(BaseSchema):
    title: str | None = Field(default=None, max_length=500)
    drawing_type: str | None = None
    status: str | None = Field(default=None, pattern=STATUS_PATTERN)
    scale: str | None = None
    sheet_number: str | None = None
    total_sheets: int | None = Field(default=None, ge=1)
    vendor: str | None = None
    originator: str | None = None
    remarks: str | None = None
    discipline_id: int | None = None
    area_id: int | None = None
    system_id: int | None = None
    tags: list[str] | None = None


class DrawingFileOut(BaseSchema):
    id: int
    revision_id: int | None
    original_filename: str
    relative_path: str | None
    file_size: int
    page_count: int | None
    is_scanned: bool | None
    upload_status: str
    error_message: str | None = None
    created_at: datetime


class RevisionOut(BaseSchema):
    id: int
    drawing_id: int
    revision: str
    revision_seq: int
    revision_date: date | None
    status: str
    description: str | None
    drawn_by: str | None = None
    checker: str | None = None
    approver: str | None = None
    is_current: bool
    total_weight_kg: DecimalFloat
    ocr_confidence: float | None = None
    created_at: datetime


class RevisionCreate(BaseSchema):
    revision: str = Field(min_length=1, max_length=20, examples=["B"])
    revision_date: date | None = None
    status: str = Field(default=DrawingStatus.IFR, pattern=STATUS_PATTERN)
    description: str | None = None
    checker: str | None = None
    approver: str | None = None


class DrawingList(TimestampedSchema):
    project_id: int
    project_code: str | None = None
    discipline_id: int | None
    discipline_code: str | None = None
    area_id: int | None
    area_code: str | None = None
    system_id: int | None
    drawing_number: str
    title: str
    drawing_type: str | None
    status: str
    current_revision: str | None
    scale: str | None
    sheet_number: str | None
    vendor: str | None
    folder_path: str | None
    current_weight_kg: DecimalFloat
    revision_count: int = 0
    ocr_confidence: float | None = None


class DrawingDetail(DrawingList):
    total_sheets: int | None
    originator: str | None
    remarks: str | None
    tags: list[str] = []
    revisions: list[RevisionOut] = []
    files: list[DrawingFileOut] = []
    latest_ocr_result_id: int | None = None


class UploadItemResult(BaseSchema):
    filename: str
    relative_path: str | None = None
    status: str = Field(examples=["created", "new_revision", "duplicate", "failed"])
    drawing_id: int | None = None
    revision_id: int | None = None
    file_id: int | None = None
    ocr_job_id: int | None = None
    drawing_number: str | None = None
    revision: str | None = None
    discipline: str | None = None
    error: str | None = None


class UploadSummary(BaseSchema):
    total: int
    succeeded: int
    failed: int
    duplicates: int
    success_rate: float
    items: list[UploadItemResult]


class TreeNode(BaseSchema):
    id: str
    label: str
    type: str = Field(examples=["project", "discipline", "area", "drawing"])
    entity_id: int | None = None
    count: int = 0
    meta: dict[str, Any] = {}
    children: list[TreeNode] = []
