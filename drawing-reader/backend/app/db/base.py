"""Declarative base and reusable column mixins."""

from __future__ import annotations

from datetime import UTC, datetime
from typing import Any

from sqlalchemy import JSON, BigInteger, Boolean, DateTime, ForeignKey, Integer, MetaData, func
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import DeclarativeBase, Mapped, declared_attr, mapped_column

NAMING_CONVENTION = {
    "ix": "ix_%(column_0_label)s",
    "uq": "uq_%(table_name)s_%(column_0_N_name)s",
    "ck": "ck_%(table_name)s_%(constraint_name)s",
    "fk": "fk_%(table_name)s_%(column_0_name)s_%(referred_table_name)s",
    "pk": "pk_%(table_name)s",
}

# BIGINT on PostgreSQL, INTEGER on SQLite so that autoincrement works in tests.
BigIntPK = BigInteger().with_variant(Integer, "sqlite")
JSONType = JSON().with_variant(JSONB(), "postgresql")


def utcnow() -> datetime:
    return datetime.now(UTC)


class Base(DeclarativeBase):
    metadata = MetaData(naming_convention=NAMING_CONVENTION)
    type_annotation_map = {dict[str, Any]: JSONType, list[Any]: JSONType}

    id: Mapped[int] = mapped_column(BigIntPK, primary_key=True, autoincrement=True)

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"<{type(self).__name__} id={self.id}>"


class TimestampMixin:
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), default=utcnow, server_default=func.now(), nullable=False, index=True
    )
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), default=utcnow, onupdate=utcnow, server_default=func.now(), nullable=False
    )


class SoftDeleteMixin:
    is_deleted: Mapped[bool] = mapped_column(Boolean, default=False, server_default="false", nullable=False, index=True)
    deleted_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)

    def soft_delete(self) -> None:
        self.is_deleted = True
        self.deleted_at = utcnow()


class AuditMixin:
    @declared_attr
    def created_by_id(cls) -> Mapped[int | None]:  # noqa: N805
        return mapped_column(BigIntPK, ForeignKey("users.id", ondelete="SET NULL"), nullable=True)

    @declared_attr
    def updated_by_id(cls) -> Mapped[int | None]:  # noqa: N805
        return mapped_column(BigIntPK, ForeignKey("users.id", ondelete="SET NULL"), nullable=True)


class StandardMixin(TimestampMixin, SoftDeleteMixin, AuditMixin):
    """created/updated timestamps + soft delete + audit columns."""
