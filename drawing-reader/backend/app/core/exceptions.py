"""Domain exceptions and FastAPI exception handlers producing a uniform error envelope."""

from __future__ import annotations

import logging
from typing import Any

from fastapi import FastAPI, Request, status
from fastapi.encoders import jsonable_encoder
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from sqlalchemy.exc import IntegrityError
from starlette.exceptions import HTTPException as StarletteHTTPException

from app.core.logging import request_id_ctx

logger = logging.getLogger(__name__)


class AppError(Exception):
    status_code: int = status.HTTP_400_BAD_REQUEST
    code: str = "bad_request"

    def __init__(self, message: str, *, details: Any = None, code: str | None = None) -> None:
        super().__init__(message)
        self.message = message
        self.details = details
        if code:
            self.code = code


class NotFoundError(AppError):
    status_code = status.HTTP_404_NOT_FOUND
    code = "not_found"


class ConflictError(AppError):
    status_code = status.HTTP_409_CONFLICT
    code = "conflict"


class AuthenticationError(AppError):
    status_code = status.HTTP_401_UNAUTHORIZED
    code = "unauthorized"


class PermissionDeniedError(AppError):
    status_code = status.HTTP_403_FORBIDDEN
    code = "forbidden"


class ValidationFailedError(AppError):
    status_code = 422
    code = "validation_error"


def _envelope(code: str, message: str, details: Any = None) -> dict[str, Any]:
    return {
        "success": False,
        "error": {"code": code, "message": message, "details": details},
        "request_id": request_id_ctx.get(),
    }


def register_exception_handlers(app: FastAPI) -> None:
    @app.exception_handler(AppError)
    async def _app_error(_: Request, exc: AppError) -> JSONResponse:
        headers = {"WWW-Authenticate": "Bearer"} if exc.status_code == 401 else None
        return JSONResponse(
            status_code=exc.status_code,
            content=jsonable_encoder(_envelope(exc.code, exc.message, exc.details)),
            headers=headers,
        )

    @app.exception_handler(RequestValidationError)
    async def _validation_error(_: Request, exc: RequestValidationError) -> JSONResponse:
        details = [
            {"loc": list(err.get("loc", [])), "msg": err.get("msg"), "type": err.get("type")} for err in exc.errors()
        ]
        return JSONResponse(
            status_code=422,
            content=jsonable_encoder(_envelope("validation_error", "Request validation failed", details)),
        )

    @app.exception_handler(StarletteHTTPException)
    async def _http_error(_: Request, exc: StarletteHTTPException) -> JSONResponse:
        return JSONResponse(
            status_code=exc.status_code,
            content=_envelope("http_error", str(exc.detail)),
            headers=getattr(exc, "headers", None),
        )

    @app.exception_handler(IntegrityError)
    async def _integrity_error(_: Request, exc: IntegrityError) -> JSONResponse:
        logger.warning("Integrity error: %s", exc.orig)
        return JSONResponse(
            status_code=status.HTTP_409_CONFLICT,
            content=_envelope("conflict", "Resource violates a uniqueness or reference constraint"),
        )

    @app.exception_handler(Exception)
    async def _unhandled(_: Request, exc: Exception) -> JSONResponse:  # pragma: no cover - safety net
        logger.exception("Unhandled error: %s", exc)
        return JSONResponse(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            content=_envelope("internal_error", "An unexpected error occurred"),
        )
