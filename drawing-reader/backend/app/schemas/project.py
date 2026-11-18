from __future__ import annotations

from datetime import date

from pydantic import Field, model_validator

from app.schemas.common import BaseSchema, TimestampedSchema


class ProjectBase(BaseSchema):
    code: str = Field(min_length=2, max_length=40, pattern=r"^[A-Za-z0-9_-]+$", examples=["NRP-2026"])
    name: str = Field(min_length=2, max_length=255, examples=["North Refinery Expansion"])
    client: str | None = Field(default=None, max_length=255, examples=["Acme Petroleum"])
    location: str | None = Field(default=None, max_length=255)
    description: str | None = None
    status: str = Field(default="active", pattern=r"^(active|on_hold|completed|cancelled)$")
    currency: str = Field(default="USD", min_length=3, max_length=3)
    start_date: date | None = None
    end_date: date | None = None

    @model_validator(mode="after")
    def _dates(self) -> ProjectBase:
        if self.start_date and self.end_date and self.end_date < self.start_date:
            raise ValueError("end_date must be on or after start_date")
        return self


class ProjectCreate(ProjectBase):
    pass


class ProjectUpdate(BaseSchema):
    name: str | None = Field(default=None, min_length=2, max_length=255)
    client: str | None = None
    location: str | None = None
    description: str | None = None
    status: str | None = Field(default=None, pattern=r"^(active|on_hold|completed|cancelled)$")
    currency: str | None = Field(default=None, min_length=3, max_length=3)
    start_date: date | None = None
    end_date: date | None = None


class ProjectList(TimestampedSchema):
    code: str
    name: str
    client: str | None = None
    status: str
    drawing_count: int = 0


class ProjectDetail(TimestampedSchema, ProjectBase):
    drawing_count: int = 0


class DisciplineOut(BaseSchema):
    id: int
    code: str
    name: str
    color: str | None = None


class AreaOut(BaseSchema):
    id: int
    project_id: int
    code: str
    name: str


class SystemOut(BaseSchema):
    id: int
    project_id: int
    area_id: int | None = None
    code: str
    name: str
