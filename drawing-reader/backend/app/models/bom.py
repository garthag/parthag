"""BOM, weights, BOQ, rates, revision differences and cost impacts."""

from __future__ import annotations

from datetime import date, datetime
from decimal import Decimal
from typing import TYPE_CHECKING, Any

from sqlalchemy import Boolean, Date, DateTime, Float, ForeignKey, Index, Integer, Numeric, String, UniqueConstraint
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db.base import Base, BigIntPK, JSONType, StandardMixin, TimestampMixin, utcnow
from app.models.drawing import WEIGHT

if TYPE_CHECKING:
    from app.models.drawing import Drawing, DrawingRevision

QTY = Numeric(18, 3)
MONEY = Numeric(18, 2)


class BOMHeader(StandardMixin, Base):
    __tablename__ = "bom_headers"
    __table_args__ = (UniqueConstraint("revision_id"),)

    drawing_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("drawings.id", ondelete="CASCADE"), index=True)
    revision_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("drawing_revisions.id", ondelete="CASCADE"))
    source: Mapped[str] = mapped_column(String(20), default="ocr")  # ocr | native | manual | import
    page_from: Mapped[int | None] = mapped_column(Integer)
    page_to: Mapped[int | None] = mapped_column(Integer)
    item_count: Mapped[int] = mapped_column(Integer, default=0)
    total_weight_kg: Mapped[Decimal] = mapped_column(WEIGHT, default=Decimal("0"))
    source_total_weight_kg: Mapped[Decimal | None] = mapped_column(WEIGHT)
    extraction_confidence: Mapped[float | None] = mapped_column(Float)
    weight_check_passed: Mapped[bool | None] = mapped_column(Boolean)

    revision: Mapped[DrawingRevision] = relationship(back_populates="bom_header")
    drawing: Mapped[Drawing] = relationship()
    items: Mapped[list[BOMItem]] = relationship(
        back_populates="header", cascade="all, delete-orphan", order_by="BOMItem.line_no"
    )


class BOMItem(StandardMixin, Base):
    __tablename__ = "bom_items"
    __table_args__ = (
        Index("ix_bom_items_material", "material", "grade"),
        Index("ix_bom_items_part", "part_number"),
    )

    bom_header_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("bom_headers.id", ondelete="CASCADE"), index=True)
    line_no: Mapped[int] = mapped_column(Integer, default=0)
    item_number: Mapped[str | None] = mapped_column(String(20))
    part_number: Mapped[str | None] = mapped_column(String(120))
    description: Mapped[str] = mapped_column(String(500), default="")
    material: Mapped[str | None] = mapped_column(String(120))
    grade: Mapped[str | None] = mapped_column(String(60))
    specification: Mapped[str | None] = mapped_column(String(200))
    quantity: Mapped[Decimal] = mapped_column(QTY, default=Decimal("0"))
    unit: Mapped[str] = mapped_column(String(20), default="EA")
    unit_weight_kg: Mapped[Decimal | None] = mapped_column(WEIGHT)
    total_weight_kg: Mapped[Decimal] = mapped_column(WEIGHT, default=Decimal("0"))
    sheet_no: Mapped[int | None] = mapped_column(Integer)
    confidence: Mapped[float | None] = mapped_column(Float)
    weight_calculated: Mapped[bool] = mapped_column(Boolean, default=False)

    header: Mapped[BOMHeader] = relationship(back_populates="items")


class RevisionDifference(TimestampMixin, Base):
    __tablename__ = "revision_differences"
    __table_args__ = (UniqueConstraint("from_revision_id", "to_revision_id"),)

    drawing_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("drawings.id", ondelete="CASCADE"), index=True)
    from_revision_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("drawing_revisions.id", ondelete="CASCADE"))
    to_revision_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("drawing_revisions.id", ondelete="CASCADE"))
    added_count: Mapped[int] = mapped_column(Integer, default=0)
    removed_count: Mapped[int] = mapped_column(Integer, default=0)
    modified_count: Mapped[int] = mapped_column(Integer, default=0)
    from_weight_kg: Mapped[Decimal] = mapped_column(WEIGHT, default=Decimal("0"))
    to_weight_kg: Mapped[Decimal] = mapped_column(WEIGHT, default=Decimal("0"))
    weight_delta_kg: Mapped[Decimal] = mapped_column(WEIGHT, default=Decimal("0"), index=True)
    cost_delta: Mapped[Decimal] = mapped_column(MONEY, default=Decimal("0"))
    currency: Mapped[str] = mapped_column(String(3), default="USD")
    details: Mapped[dict[str, Any]] = mapped_column(JSONType, default=dict)
    generated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)

    cost_impacts: Mapped[list[CostImpact]] = relationship(back_populates="difference", cascade="all, delete-orphan")


class WeightHistory(TimestampMixin, Base):
    __tablename__ = "weight_history"
    __table_args__ = (
        Index("ix_weight_history_project_date", "project_id", "recorded_on"),
        UniqueConstraint("revision_id"),
    )

    project_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("projects.id", ondelete="CASCADE"))
    drawing_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("drawings.id", ondelete="CASCADE"), index=True)
    revision_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("drawing_revisions.id", ondelete="CASCADE"))
    discipline_id: Mapped[int | None] = mapped_column(BigIntPK, ForeignKey("disciplines.id", ondelete="SET NULL"))
    total_weight_kg: Mapped[Decimal] = mapped_column(WEIGHT, default=Decimal("0"))
    delta_kg: Mapped[Decimal] = mapped_column(WEIGHT, default=Decimal("0"))
    recorded_on: Mapped[date] = mapped_column(Date)


class BOQSummary(TimestampMixin, Base):
    __tablename__ = "boq_summary"
    __table_args__ = (UniqueConstraint("project_id", "discipline_id", "material", "grade", "unit"),)

    project_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("projects.id", ondelete="CASCADE"), index=True)
    discipline_id: Mapped[int | None] = mapped_column(BigIntPK, ForeignKey("disciplines.id", ondelete="SET NULL"))
    material: Mapped[str] = mapped_column(String(120))
    grade: Mapped[str] = mapped_column(String(60), default="")
    unit: Mapped[str] = mapped_column(String(20), default="EA")
    total_quantity: Mapped[Decimal] = mapped_column(QTY, default=Decimal("0"))
    total_weight_kg: Mapped[Decimal] = mapped_column(WEIGHT, default=Decimal("0"))
    total_cost: Mapped[Decimal] = mapped_column(MONEY, default=Decimal("0"))
    currency: Mapped[str] = mapped_column(String(3), default="USD")
    drawing_count: Mapped[int] = mapped_column(Integer, default=0)
    as_of: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)


class MaterialRate(StandardMixin, Base):
    __tablename__ = "material_rates"
    __table_args__ = (UniqueConstraint("material", "grade", "effective_from"),)

    material: Mapped[str] = mapped_column(String(120), index=True)
    grade: Mapped[str] = mapped_column(String(60), default="")
    rate: Mapped[Decimal] = mapped_column(Numeric(18, 4))
    rate_basis: Mapped[str] = mapped_column(String(10), default="kg")  # kg | unit
    currency: Mapped[str] = mapped_column(String(3), default="USD")
    effective_from: Mapped[date] = mapped_column(Date)
    effective_to: Mapped[date | None] = mapped_column(Date)


class CostImpact(TimestampMixin, Base):
    __tablename__ = "cost_impacts"
    __table_args__ = (Index("ix_cost_impacts_project", "project_id"),)

    revision_difference_id: Mapped[int] = mapped_column(
        BigIntPK, ForeignKey("revision_differences.id", ondelete="CASCADE"), index=True
    )
    project_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("projects.id", ondelete="CASCADE"))
    drawing_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("drawings.id", ondelete="CASCADE"))
    material: Mapped[str] = mapped_column(String(120))
    grade: Mapped[str] = mapped_column(String(60), default="")
    quantity_delta: Mapped[Decimal] = mapped_column(QTY, default=Decimal("0"))
    weight_delta_kg: Mapped[Decimal] = mapped_column(WEIGHT, default=Decimal("0"))
    rate: Mapped[Decimal] = mapped_column(Numeric(18, 4), default=Decimal("0"))
    cost_delta: Mapped[Decimal] = mapped_column(MONEY, default=Decimal("0"))
    currency: Mapped[str] = mapped_column(String(3), default="USD")

    difference: Mapped[RevisionDifference] = relationship(back_populates="cost_impacts")
