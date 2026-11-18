"""Users, roles and permissions."""

from __future__ import annotations

from datetime import datetime

from sqlalchemy import Boolean, Column, DateTime, ForeignKey, String, Table
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db.base import Base, BigIntPK, SoftDeleteMixin, TimestampMixin

user_roles = Table(
    "user_roles",
    Base.metadata,
    Column("user_id", BigIntPK, ForeignKey("users.id", ondelete="CASCADE"), primary_key=True),
    Column("role_id", BigIntPK, ForeignKey("roles.id", ondelete="CASCADE"), primary_key=True),
)

role_permissions = Table(
    "role_permissions",
    Base.metadata,
    Column("role_id", BigIntPK, ForeignKey("roles.id", ondelete="CASCADE"), primary_key=True),
    Column("permission_id", BigIntPK, ForeignKey("permissions.id", ondelete="CASCADE"), primary_key=True),
)


class Permission(TimestampMixin, Base):
    __tablename__ = "permissions"

    code: Mapped[str] = mapped_column(String(100), unique=True, index=True)
    description: Mapped[str | None] = mapped_column(String(255))


class Role(TimestampMixin, Base):
    __tablename__ = "roles"

    name: Mapped[str] = mapped_column(String(80), unique=True, index=True)
    description: Mapped[str | None] = mapped_column(String(255))
    permissions: Mapped[list[Permission]] = relationship(secondary=role_permissions, lazy="selectin")


class User(TimestampMixin, SoftDeleteMixin, Base):
    __tablename__ = "users"

    email: Mapped[str] = mapped_column(String(255), unique=True, index=True)
    username: Mapped[str] = mapped_column(String(80), unique=True, index=True)
    full_name: Mapped[str] = mapped_column(String(200))
    hashed_password: Mapped[str] = mapped_column(String(255))
    job_title: Mapped[str | None] = mapped_column(String(120))
    is_active: Mapped[bool] = mapped_column(Boolean, default=True, server_default="true")
    is_superuser: Mapped[bool] = mapped_column(Boolean, default=False, server_default="false")
    last_login_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))

    roles: Mapped[list[Role]] = relationship(secondary=user_roles, lazy="selectin")

    @property
    def permission_codes(self) -> set[str]:
        return {p.code for r in self.roles for p in r.permissions}

    def has_permission(self, code: str) -> bool:
        return self.is_superuser or code in self.permission_codes
