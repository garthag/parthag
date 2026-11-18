"""Drawings, revisions, files, relationships and tags."""

from __future__ import annotations

from datetime import date
from decimal import Decimal
from typing import TYPE_CHECKING

from sqlalchemy import (
    Boolean,
    CheckConstraint,
    Date,
    Float,
    ForeignKey,
    Index,
    Integer,
    Numeric,
    String,
    Text,
    UniqueConstraint,
)
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db.base import Base, BigIntPK, StandardMixin, TimestampMixin

if TYPE_CHECKING:
    from app.models.bom import BOMHeader
    from app.models.project import Area, Discipline, Project, System

WEIGHT = Numeric(18, 3)


class DrawingStatus:
    PRELIMINARY = "PRELIM"
    IFR = "IFR"  # Issued for review
    IFA = "IFA"  # Issued for approval
    IFC = "IFC"  # Issued for construction
    AFC = "AFC"  # Approved for construction
    AS_BUILT = "AS-BUILT"
    SUPERSEDED = "SUPERSEDED"
    VOID = "VOID"

    ALL = (PRELIMINARY, IFR, IFA, IFC, AFC, AS_BUILT, SUPERSEDED, VOID)


class Drawing(StandardMixin, Base):
    __tablename__ = "drawings"
    __table_args__ = (
        UniqueConstraint("project_id", "drawing_number"),
        Index("ix_drawings_project_discipline", "project_id", "discipline_id"),
        Index("ix_drawings_title", "title"),
        Index("ix_drawings_status", "status"),
    )

    project_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("projects.id", ondelete="CASCADE"), index=True)
    discipline_id: Mapped[int | None] = mapped_column(BigIntPK, ForeignKey("disciplines.id", ondelete="SET NULL"))
    area_id: Mapped[int | None] = mapped_column(BigIntPK, ForeignKey("areas.id", ondelete="SET NULL"), index=True)
    system_id: Mapped[int | None] = mapped_column(BigIntPK, ForeignKey("systems.id", ondelete="SET NULL"), index=True)

    drawing_number: Mapped[str] = mapped_column(String(120), index=True)
    title: Mapped[str] = mapped_column(String(500), default="")
    drawing_type: Mapped[str | None] = mapped_column(String(60))
    status: Mapped[str] = mapped_column(String(20), default=DrawingStatus.PRELIMINARY)
    current_revision: Mapped[str | None] = mapped_column(String(20))
    scale: Mapped[str | None] = mapped_column(String(40))
    sheet_number: Mapped[str | None] = mapped_column(String(20))
    total_sheets: Mapped[int | None] = mapped_column(Integer)
    vendor: Mapped[str | None] = mapped_column(String(200))
    originator: Mapped[str | None] = mapped_column(String(200))
    folder_path: Mapped[str | None] = mapped_column(String(1000))
    current_weight_kg: Mapped[Decimal] = mapped_column(WEIGHT, default=Decimal("0"), server_default="0")
    remarks: Mapped[str | None] = mapped_column(Text)

    project: Mapped[Project] = relationship(back_populates="drawings")
    discipline: Mapped[Discipline | None] = relationship(lazy="joined")
    area: Mapped[Area | None] = relationship()
    system: Mapped[System | None] = relationship()
    revisions: Mapped[list[DrawingRevision]] = relationship(
        back_populates="drawing", cascade="all, delete-orphan", order_by="DrawingRevision.revision_seq"
    )
    files: Mapped[list[DrawingFile]] = relationship(back_populates="drawing", cascade="all, delete-orphan")
    tags: Mapped[list[DrawingTag]] = relationship(back_populates="drawing", cascade="all, delete-orphan")


class DrawingRevision(StandardMixin, Base):
    __tablename__ = "drawing_revisions"
    __table_args__ = (
        UniqueConstraint("drawing_id", "revision"),
        Index("ix_drawing_revisions_date", "revision_date"),
        CheckConstraint("revision_seq >= 0", name="revision_seq_positive"),
    )

    drawing_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("drawings.id", ondelete="CASCADE"), index=True)
    revision: Mapped[str] = mapped_column(String(20))
    revision_seq: Mapped[int] = mapped_column(Integer, default=0)
    revision_date: Mapped[date | None] = mapped_column(Date)
    status: Mapped[str] = mapped_column(String(20), default=DrawingStatus.PRELIMINARY)
    description: Mapped[str | None] = mapped_column(String(500))
    drawn_by: Mapped[str | None] = mapped_column(String(120))
    checker: Mapped[str | None] = mapped_column(String(120))
    approver: Mapped[str | None] = mapped_column(String(120))
    is_current: Mapped[bool] = mapped_column(Boolean, default=False, server_default="false", index=True)
    total_weight_kg: Mapped[Decimal] = mapped_column(WEIGHT, default=Decimal("0"), server_default="0")
    ocr_confidence: Mapped[float | None] = mapped_column(Float)

    drawing: Mapped[Drawing] = relationship(back_populates="revisions")
    files: Mapped[list[DrawingFile]] = relationship(back_populates="revision")
    bom_header: Mapped[BOMHeader | None] = relationship(back_populates="revision", uselist=False)


class FileStatus:
    PENDING = "pending"
    PROCESSING = "processing"
    PROCESSED = "processed"
    FAILED = "failed"


class DrawingFile(StandardMixin, Base):
    __tablename__ = "drawing_files"
    __table_args__ = (Index("ix_drawing_files_sha", "sha256"), Index("ix_drawing_files_status", "upload_status"))

    drawing_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("drawings.id", ondelete="CASCADE"), index=True)
    revision_id: Mapped[int | None] = mapped_column(
        BigIntPK, ForeignKey("drawing_revisions.id", ondelete="SET NULL"), index=True
    )
    original_filename: Mapped[str] = mapped_column(String(500))
    relative_path: Mapped[str | None] = mapped_column(String(1000))
    storage_path: Mapped[str] = mapped_column(String(1000))
    mime_type: Mapped[str] = mapped_column(String(100), default="application/pdf")
    file_size: Mapped[int] = mapped_column(Integer, default=0)
    sha256: Mapped[str] = mapped_column(String(64))
    page_count: Mapped[int | None] = mapped_column(Integer)
    is_scanned: Mapped[bool | None] = mapped_column(Boolean)
    upload_status: Mapped[str] = mapped_column(String(20), default=FileStatus.PENDING)
    error_message: Mapped[str | None] = mapped_column(Text)

    drawing: Mapped[Drawing] = relationship(back_populates="files")
    revision: Mapped[DrawingRevision | None] = relationship(back_populates="files")


class DrawingRelationship(StandardMixin, Base):
    __tablename__ = "drawing_relationships"
    __table_args__ = (
        UniqueConstraint("source_drawing_id", "target_drawing_id", "relationship_type"),
        CheckConstraint("source_drawing_id <> target_drawing_id", name="no_self_reference"),
    )

    source_drawing_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("drawings.id", ondelete="CASCADE"), index=True)
    target_drawing_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("drawings.id", ondelete="CASCADE"), index=True)
    relationship_type: Mapped[str] = mapped_column(String(30))  # reference | continuation | supersedes | detail
    notes: Mapped[str | None] = mapped_column(String(255))


class DrawingTag(TimestampMixin, Base):
    __tablename__ = "drawing_tags"
    __table_args__ = (UniqueConstraint("drawing_id", "tag"), Index("ix_drawing_tags_tag", "tag"))

    drawing_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("drawings.id", ondelete="CASCADE"), index=True)
    tag: Mapped[str] = mapped_column(String(80))

    drawing: Mapped[Drawing] = relationship(back_populates="tags")
