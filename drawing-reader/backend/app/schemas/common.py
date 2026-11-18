"""Shared schema building blocks: base model, envelopes, pagination and errors."""

from __future__ import annotations

from datetime import datetime
from decimal import Decimal
from typing import Annotated, Any, Literal, TypeVar

from pydantic import BaseModel, ConfigDict, Field, PlainSerializer

T = TypeVar("T")

# Decimals are serialised as JSON numbers (engineering weights fit comfortably in a float).
DecimalFloat = Annotated[Decimal, PlainSerializer(lambda v: float(v) if v is not None else None, return_type=float)]


class BaseSchema(BaseModel):
    model_config = ConfigDict(from_attributes=True, populate_by_name=True, str_strip_whitespace=True)


class TimestampedSchema(BaseSchema):
    id: int
    created_at: datetime
    updated_at: datetime


class ErrorDetail(BaseSchema):
    code: str = Field(examples=["validation_error"])
    message: str = Field(examples=["Request validation failed"])
    details: Any | None = Field(default=None, examples=[[{"loc": ["body", "code"], "msg": "Field required"}]])


class ErrorResponse(BaseSchema):
    success: Literal[False] = False
    error: ErrorDetail
    request_id: str | None = None


class ValidationErrorItem(BaseSchema):
    loc: list[str | int]
    msg: str
    type: str


class ValidationErrorResponse(ErrorResponse):
    model_config = ConfigDict(
        json_schema_extra={
            "example": {
                "success": False,
                "error": {
                    "code": "validation_error",
                    "message": "Request validation failed",
                    "details": [{"loc": ["body", "code"], "msg": "Field required", "type": "missing"}],
                },
                "request_id": "7d1c2c55a0f04e4bb5d0",
            }
        }
    )


class ApiResponse[T](BaseSchema):
    success: bool = True
    data: T
    message: str | None = None


class PaginationMeta(BaseSchema):
    page: int = Field(ge=1, examples=[1])
    page_size: int = Field(ge=1, le=1000, examples=[50])
    total: int = Field(ge=0, examples=[1240])
    pages: int = Field(ge=0, examples=[25])


class PaginationResponse[T](BaseSchema):
    success: bool = True
    data: list[T]
    meta: PaginationMeta


class MessageResponse(BaseSchema):
    success: bool = True
    message: str


ERROR_RESPONSES: dict[int | str, dict[str, Any]] = {
    400: {"model": ErrorResponse, "description": "Bad request"},
    401: {"model": ErrorResponse, "description": "Missing or invalid credentials"},
    403: {"model": ErrorResponse, "description": "Insufficient permissions"},
    404: {"model": ErrorResponse, "description": "Resource not found"},
    409: {"model": ErrorResponse, "description": "Conflict with existing resource"},
    422: {"model": ValidationErrorResponse, "description": "Validation error"},
}
