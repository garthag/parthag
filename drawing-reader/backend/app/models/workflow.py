"""OCR jobs/results, transmittals, review, approvals, notifications and audit log."""

from __future__ import annotations

from datetime import date, datetime
from typing import Any

from sqlalchemy import (
    Boolean,
    Column,
    Date,
    DateTime,
    Float,
    ForeignKey,
    Index,
    Integer,
    String,
    Table,
    Text,
    UniqueConstraint,
)
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db.base import Base, BigIntPK, JSONType, StandardMixin, TimestampMixin


class OCRJobStatus:
    QUEUED = "queued"
    RUNNING = "running"
    COMPLETED = "completed"
    FAILED = "failed"


class OCRJob(TimestampMixin, Base):
    __tablename__ = "ocr_jobs"
    __table_args__ = (Index("ix_ocr_jobs_status", "status"),)

    drawing_file_id: Mapped[int] = mapped_column(
        BigIntPK, ForeignKey("drawing_files.id", ondelete="CASCADE"), index=True
    )
    revision_id: Mapped[int | None] = mapped_column(BigIntPK, ForeignKey("drawing_revisions.id", ondelete="SET NULL"))
    status: Mapped[str] = mapped_column(String(20), default=OCRJobStatus.QUEUED)
    engine: Mapped[str | None] = mapped_column(String(30))
    progress: Mapped[int] = mapped_column(Integer, default=0)
    celery_task_id: Mapped[str | None] = mapped_column(String(64))
    extract_bom: Mapped[bool] = mapped_column(Boolean, default=True)
    started_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    finished_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    duration_ms: Mapped[int | None] = mapped_column(Integer)
    error: Mapped[str | None] = mapped_column(Text)
    requested_by_id: Mapped[int | None] = mapped_column(BigIntPK, ForeignKey("users.id", ondelete="SET NULL"))

    result: Mapped[OCRResult | None] = relationship(back_populates="job", uselist=False, cascade="all, delete-orphan")


class OCRResult(TimestampMixin, Base):
    __tablename__ = "ocr_results"
    __table_args__ = (UniqueConstraint("job_id"),)

    job_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("ocr_jobs.id", ondelete="CASCADE"))
    drawing_file_id: Mapped[int] = mapped_column(
        BigIntPK, ForeignKey("drawing_files.id", ondelete="CASCADE"), index=True
    )
    extracted: Mapped[dict[str, Any]] = mapped_column(JSONType, default=dict)
    field_confidences: Mapped[dict[str, Any]] = mapped_column(JSONType, default=dict)
    overrides: Mapped[dict[str, Any]] = mapped_column(JSONType, default=dict)
    revision_block: Mapped[list[Any]] = mapped_column(JSONType, default=list)
    notes: Mapped[list[Any]] = mapped_column(JSONType, default=list)
    regions: Mapped[dict[str, Any]] = mapped_column(JSONType, default=dict)
    overall_confidence: Mapped[float] = mapped_column(Float, default=0.0)
    rotation: Mapped[int] = mapped_column(Integer, default=0)
    is_scanned: Mapped[bool] = mapped_column(Boolean, default=False)
    engine: Mapped[str | None] = mapped_column(String(30))
    raw_text: Mapped[str | None] = mapped_column(Text)
    is_overridden: Mapped[bool] = mapped_column(Boolean, default=False)
    needs_review: Mapped[bool] = mapped_column(Boolean, default=False, index=True)
    reviewed_by_id: Mapped[int | None] = mapped_column(BigIntPK, ForeignKey("users.id", ondelete="SET NULL"))
    reviewed_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))

    job: Mapped[OCRJob] = relationship(back_populates="result")

    @property
    def effective(self) -> dict[str, Any]:
        merged = dict(self.extracted or {})
        merged.update({k: v for k, v in (self.overrides or {}).items() if v not in (None, "")})
        return merged


transmittal_items = Table(
    "transmittal_items",
    Base.metadata,
    Column("transmittal_id", BigIntPK, ForeignKey("document_transmittals.id", ondelete="CASCADE"), primary_key=True),
    Column("revision_id", BigIntPK, ForeignKey("drawing_revisions.id", ondelete="CASCADE"), primary_key=True),
)


class DocumentTransmittal(StandardMixin, Base):
    __tablename__ = "document_transmittals"

    project_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("projects.id", ondelete="CASCADE"), index=True)
    transmittal_number: Mapped[str] = mapped_column(String(60), unique=True)
    recipient: Mapped[str] = mapped_column(String(200))
    purpose: Mapped[str] = mapped_column(String(40))  # IFR | IFA | IFC | INFO
    issued_date: Mapped[date | None] = mapped_column(Date)
    status: Mapped[str] = mapped_column(String(20), default="draft")
    remarks: Mapped[str | None] = mapped_column(Text)


class ReviewComment(StandardMixin, Base):
    __tablename__ = "review_comments"

    revision_id: Mapped[int] = mapped_column(
        BigIntPK, ForeignKey("drawing_revisions.id", ondelete="CASCADE"), index=True
    )
    author_id: Mapped[int | None] = mapped_column(BigIntPK, ForeignKey("users.id", ondelete="SET NULL"))
    comment: Mapped[str] = mapped_column(Text)
    status: Mapped[str] = mapped_column(String(20), default="open")
    page: Mapped[int | None] = mapped_column(Integer)
    x: Mapped[float | None] = mapped_column(Float)
    y: Mapped[float | None] = mapped_column(Float)
    resolved_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))


class ApprovalWorkflow(StandardMixin, Base):
    __tablename__ = "approval_workflows"
    __table_args__ = (UniqueConstraint("revision_id", "step"),)

    revision_id: Mapped[int] = mapped_column(
        BigIntPK, ForeignKey("drawing_revisions.id", ondelete="CASCADE"), index=True
    )
    step: Mapped[int] = mapped_column(Integer, default=1)
    step_name: Mapped[str] = mapped_column(String(60))  # Check | Review | Approve
    approver_id: Mapped[int | None] = mapped_column(BigIntPK, ForeignKey("users.id", ondelete="SET NULL"))
    status: Mapped[str] = mapped_column(String(20), default="pending")
    decided_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    remarks: Mapped[str | None] = mapped_column(Text)


class Notification(TimestampMixin, Base):
    __tablename__ = "notifications"
    __table_args__ = (Index("ix_notifications_user_read", "user_id", "is_read"),)

    user_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("users.id", ondelete="CASCADE"))
    title: Mapped[str] = mapped_column(String(200))
    message: Mapped[str] = mapped_column(Text)
    category: Mapped[str] = mapped_column(String(30), default="info")
    link: Mapped[str | None] = mapped_column(String(500))
    is_read: Mapped[bool] = mapped_column(Boolean, default=False)


class AuditLog(TimestampMixin, Base):
    __tablename__ = "audit_logs"
    __table_args__ = (Index("ix_audit_logs_entity", "entity_type", "entity_id"),)

    user_id: Mapped[int | None] = mapped_column(BigIntPK, ForeignKey("users.id", ondelete="SET NULL"), index=True)
    action: Mapped[str] = mapped_column(String(40))
    entity_type: Mapped[str] = mapped_column(String(60))
    entity_id: Mapped[int | None] = mapped_column(BigIntPK)
    changes: Mapped[dict[str, Any]] = mapped_column(JSONType, default=dict)
    ip_address: Mapped[str | None] = mapped_column(String(64))
    user_agent: Mapped[str | None] = mapped_column(String(300))
    request_id: Mapped[str | None] = mapped_column(String(64))
