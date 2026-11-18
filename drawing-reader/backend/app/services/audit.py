"""Audit trail helpers."""

from __future__ import annotations

from datetime import date, datetime
from decimal import Decimal
from typing import Any

from sqlalchemy.orm import Session

from app.core.logging import request_id_ctx
from app.models import AuditLog


def _jsonable(value: Any) -> Any:
    if isinstance(value, Decimal):
        return float(value)
    if isinstance(value, datetime | date):
        return value.isoformat()
    return value


def record(
    db: Session,
    *,
    action: str,
    entity: str,
    entity_id: int | None,
    user_id: int | None,
    changes: dict[str, Any] | None = None,
    ip: str | None = None,
    user_agent: str | None = None,
) -> None:
    db.add(
        AuditLog(
            user_id=user_id,
            action=action,
            entity_type=entity,
            entity_id=entity_id,
            changes={k: _jsonable(v) for k, v in (changes or {}).items()},
            ip_address=ip,
            user_agent=(user_agent or "")[:300] or None,
            request_id=request_id_ctx.get(),
        )
    )


def diff_fields(obj: Any, updates: dict[str, Any]) -> dict[str, Any]:
    """Apply ``updates`` to ``obj`` and return {field: {old, new}} for changed fields."""
    changes: dict[str, Any] = {}
    for key, new in updates.items():
        old = getattr(obj, key)
        if old != new:
            changes[key] = {"old": _jsonable(old), "new": _jsonable(new)}
            setattr(obj, key, new)
    return changes
