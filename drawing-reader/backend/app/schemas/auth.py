from __future__ import annotations

from datetime import datetime

from pydantic import EmailStr, Field

from app.schemas.common import BaseSchema


class LoginRequest(BaseSchema):
    username: str = Field(min_length=1, max_length=255, description="Username or e-mail", examples=["admin"])
    password: str = Field(min_length=6, max_length=128, examples=["Admin@12345"])


class RefreshRequest(BaseSchema):
    refresh_token: str = Field(min_length=10)


class LogoutRequest(BaseSchema):
    refresh_token: str | None = None


class TokenPair(BaseSchema):
    access_token: str
    refresh_token: str
    token_type: str = "bearer"  # noqa: S105 - OAuth2 token type, not a secret
    expires_in: int = Field(description="Access token lifetime in seconds", examples=[1800])


class RoleOut(BaseSchema):
    id: int
    name: str
    description: str | None = None


class UserOut(BaseSchema):
    id: int
    email: str
    username: str
    full_name: str
    job_title: str | None = None
    is_active: bool
    is_superuser: bool
    last_login_at: datetime | None = None
    roles: list[RoleOut] = []
    permissions: list[str] = []


class UserCreate(BaseSchema):
    email: EmailStr
    username: str = Field(min_length=3, max_length=80, pattern=r"^[A-Za-z0-9_.-]+$")
    full_name: str = Field(min_length=1, max_length=200)
    password: str = Field(min_length=8, max_length=128)
    job_title: str | None = None
    roles: list[str] = []


class PasswordChange(BaseSchema):
    current_password: str = Field(min_length=1, max_length=128)
    new_password: str = Field(min_length=8, max_length=128)
