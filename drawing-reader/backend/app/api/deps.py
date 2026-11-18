"""Shared FastAPI dependencies: DB session, authentication, permissions, pagination, sorting."""

from __future__ import annotations

import math
from collections.abc import Callable
from dataclasses import dataclass
from typing import Annotated, Any

from fastapi import Depends, Query, Request
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy import Select, func, select
from sqlalchemy.orm import Session

from app.core.exceptions import AuthenticationError, PermissionDeniedError, ValidationFailedError
from app.core.security import decode_token
from app.db.session import get_db
from app.models import User

DbSession = Annotated[Session, Depends(get_db)]
bearer = HTTPBearer(auto_error=False, description="JWT access token from POST /api/auth/login")


def get_current_user(
    db: DbSession, credentials: Annotated[HTTPAuthorizationCredentials | None, Depends(bearer)]
) -> User:
    if credentials is None:
        raise AuthenticationError("Not authenticated")
    payload = decode_token(credentials.credentials, "access")
    user = db.get(User, int(payload["sub"]))
    if user is None or not user.is_active or user.is_deleted:
        raise AuthenticationError("User inactive or not found")
    return user


CurrentUser = Annotated[User, Depends(get_current_user)]


def require(permission: str) -> Callable[[User], User]:
    def checker(user: CurrentUser) -> User:
        if not user.has_permission(permission):
            raise PermissionDeniedError(f"Missing permission: {permission}")
        return user

    return checker


@dataclass
class PageParams:
    page: int
    page_size: int
    sort: str | None

    @property
    def offset(self) -> int:
        return (self.page - 1) * self.page_size


def page_params(
    page: Annotated[int, Query(ge=1, description="1-based page number")] = 1,
    page_size: Annotated[int, Query(ge=1, le=1000, description="Items per page")] = 50,
    sort: Annotated[
        str | None, Query(description="Sort field; prefix with '-' for descending", examples=["-updated_at"])
    ] = None,
) -> PageParams:
    return PageParams(page, page_size, sort)


Paging = Annotated[PageParams, Depends(page_params)]


def apply_sort(stmt: Select, sort: str | None, allowed: dict[str, Any], default: Any) -> Select:
    if not sort:
        return stmt.order_by(default)
    clauses = []
    for part in sort.split(","):
        part = part.strip()
        desc = part.startswith("-")
        key = part.lstrip("-+")
        if key not in allowed:
            raise ValidationFailedError(f"Cannot sort by '{key}'", details={"allowed": sorted(allowed)})
        clauses.append(allowed[key].desc() if desc else allowed[key].asc())
    return stmt.order_by(*clauses)


def paginate(db: Session, stmt: Select, paging: PageParams) -> tuple[list[Any], dict[str, int]]:
    total = db.scalar(select(func.count()).select_from(stmt.order_by(None).subquery())) or 0
    items = list(db.scalars(stmt.offset(paging.offset).limit(paging.page_size)).unique())
    meta = {
        "page": paging.page,
        "page_size": paging.page_size,
        "total": total,
        "pages": math.ceil(total / paging.page_size) if total else 0,
    }
    return items, meta


def client_info(request: Request) -> dict[str, str | None]:
    forwarded = request.headers.get("x-forwarded-for")
    ip = forwarded.split(",")[0].strip() if forwarded else (request.client.host if request.client else None)
    return {"ip": ip, "user_agent": request.headers.get("user-agent")}
