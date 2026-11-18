"""Project breakdown structure: projects, disciplines, areas, systems."""

from __future__ import annotations

from datetime import date
from typing import TYPE_CHECKING

from sqlalchemy import Date, ForeignKey, Index, String, Text, UniqueConstraint
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db.base import Base, BigIntPK, StandardMixin

if TYPE_CHECKING:
    from app.models.drawing import Drawing


class Project(StandardMixin, Base):
    __tablename__ = "projects"

    code: Mapped[str] = mapped_column(String(40), unique=True, index=True)
    name: Mapped[str] = mapped_column(String(255), index=True)
    client: Mapped[str | None] = mapped_column(String(255))
    location: Mapped[str | None] = mapped_column(String(255))
    description: Mapped[str | None] = mapped_column(Text)
    status: Mapped[str] = mapped_column(String(30), default="active", server_default="active")
    currency: Mapped[str] = mapped_column(String(3), default="USD", server_default="USD")
    start_date: Mapped[date | None] = mapped_column(Date)
    end_date: Mapped[date | None] = mapped_column(Date)

    areas: Mapped[list[Area]] = relationship(
        back_populates="project", cascade="all, delete-orphan", passive_deletes=True
    )
    systems: Mapped[list[System]] = relationship(
        back_populates="project", cascade="all, delete-orphan", passive_deletes=True
    )
    drawings: Mapped[list[Drawing]] = relationship(back_populates="project", passive_deletes=True)


class Discipline(StandardMixin, Base):
    __tablename__ = "disciplines"

    code: Mapped[str] = mapped_column(String(10), unique=True, index=True)
    name: Mapped[str] = mapped_column(String(100), unique=True)
    description: Mapped[str | None] = mapped_column(String(255))
    color: Mapped[str | None] = mapped_column(String(9))


class Area(StandardMixin, Base):
    __tablename__ = "areas"
    __table_args__ = (UniqueConstraint("project_id", "code"),)

    project_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("projects.id", ondelete="CASCADE"), index=True)
    code: Mapped[str] = mapped_column(String(40))
    name: Mapped[str] = mapped_column(String(200))
    description: Mapped[str | None] = mapped_column(String(255))

    project: Mapped[Project] = relationship(back_populates="areas")


class System(StandardMixin, Base):
    __tablename__ = "systems"
    __table_args__ = (UniqueConstraint("project_id", "code"), Index("ix_systems_area", "area_id"))

    project_id: Mapped[int] = mapped_column(BigIntPK, ForeignKey("projects.id", ondelete="CASCADE"), index=True)
    area_id: Mapped[int | None] = mapped_column(BigIntPK, ForeignKey("areas.id", ondelete="SET NULL"))
    code: Mapped[str] = mapped_column(String(40))
    name: Mapped[str] = mapped_column(String(200))
    description: Mapped[str | None] = mapped_column(String(255))

    project: Mapped[Project] = relationship(back_populates="systems")
    area: Mapped[Area | None] = relationship()
