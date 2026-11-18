from __future__ import annotations

from datetime import UTC, datetime
from typing import Annotated

from fastapi import APIRouter, Depends, Request
from fastapi.security import HTTPAuthorizationCredentials
from sqlalchemy import func, or_, select

from app.api.deps import CurrentUser, DbSession, bearer, client_info, require
from app.core.config import settings
from app.core.exceptions import AuthenticationError, ConflictError
from app.core.security import create_token, decode_token, hash_password, token_blacklist, verify_password
from app.models import Role, User
from app.schemas.auth import (
    LoginRequest,
    LogoutRequest,
    PasswordChange,
    RefreshRequest,
    TokenPair,
    UserCreate,
    UserOut,
)
from app.schemas.common import ERROR_RESPONSES, ApiResponse, MessageResponse
from app.services import audit

router = APIRouter(prefix="/auth", tags=["Authentication"], responses=ERROR_RESPONSES)


def _user_out(user: User) -> UserOut:
    out = UserOut.model_validate(user)
    out.permissions = sorted(user.permission_codes) if not user.is_superuser else ["*"]
    return out


def _tokens(user: User) -> TokenPair:
    roles = [r.name for r in user.roles]
    return TokenPair(
        access_token=create_token(user.id, "access", {"roles": roles}),
        refresh_token=create_token(user.id, "refresh"),
        expires_in=settings.ACCESS_TOKEN_EXPIRE_MINUTES * 60,
    )


@router.post("/login", response_model=ApiResponse[TokenPair], summary="Obtain access and refresh tokens")
def login(body: LoginRequest, db: DbSession, request: Request) -> ApiResponse[TokenPair]:
    ident = body.username.lower()
    user = db.scalar(select(User).where(or_(func.lower(User.username) == ident, func.lower(User.email) == ident)))
    if user is None or not verify_password(body.password, user.hashed_password) or not user.is_active:
        raise AuthenticationError("Incorrect username or password")
    user.last_login_at = datetime.now(UTC)
    audit.record(db, action="login", entity="user", entity_id=user.id, user_id=user.id, **client_info(request))
    return ApiResponse(data=_tokens(user))


@router.post("/refresh", response_model=ApiResponse[TokenPair], summary="Rotate tokens using a refresh token")
def refresh(body: RefreshRequest, db: DbSession) -> ApiResponse[TokenPair]:
    payload = decode_token(body.refresh_token, "refresh")
    user = db.get(User, int(payload["sub"]))
    if user is None or not user.is_active:
        raise AuthenticationError("User inactive or not found")
    token_blacklist.revoke(payload["jti"], payload["exp"])  # refresh tokens are single-use
    return ApiResponse(data=_tokens(user))


@router.post("/logout", response_model=MessageResponse, summary="Revoke the current tokens")
def logout(
    body: LogoutRequest,
    user: CurrentUser,
    db: DbSession,
    credentials: Annotated[HTTPAuthorizationCredentials | None, Depends(bearer)],
) -> MessageResponse:
    if credentials is not None:
        access = decode_token(credentials.credentials, "access")
        token_blacklist.revoke(access["jti"], access["exp"])
    if body.refresh_token:
        try:
            ref = decode_token(body.refresh_token, "refresh")
            token_blacklist.revoke(ref["jti"], ref["exp"])
        except AuthenticationError:
            pass
    audit.record(db, action="logout", entity="user", entity_id=user.id, user_id=user.id)
    return MessageResponse(message="Logged out")


@router.get("/me", response_model=ApiResponse[UserOut], summary="Current user profile")
def me(user: CurrentUser) -> ApiResponse[UserOut]:
    return ApiResponse(data=_user_out(user))


@router.post("/change-password", response_model=MessageResponse, summary="Change the current user's password")
def change_password(body: PasswordChange, user: CurrentUser, db: DbSession) -> MessageResponse:
    if not verify_password(body.current_password, user.hashed_password):
        raise AuthenticationError("Current password is incorrect")
    if body.new_password == body.current_password:
        from app.core.exceptions import ValidationFailedError

        raise ValidationFailedError("New password must differ from the current one")
    user.hashed_password = hash_password(body.new_password)
    audit.record(db, action="change_password", entity="user", entity_id=user.id, user_id=user.id)
    return MessageResponse(message="Password changed")


@router.post("/users", response_model=ApiResponse[UserOut], status_code=201, summary="Create a user (admin)")
def create_user(
    body: UserCreate, db: DbSession, admin: Annotated[User, Depends(require("users.manage"))]
) -> ApiResponse[UserOut]:
    exists = db.scalar(select(User).where(or_(User.email == body.email, User.username == body.username)))
    if exists:
        raise ConflictError("User with this e-mail or username already exists")
    roles = list(db.scalars(select(Role).where(Role.name.in_(body.roles)))) if body.roles else []
    user = User(
        email=body.email,
        username=body.username,
        full_name=body.full_name,
        job_title=body.job_title,
        hashed_password=hash_password(body.password),
        roles=roles,
    )
    db.add(user)
    db.flush()
    audit.record(
        db, action="create", entity="user", entity_id=user.id, user_id=admin.id, changes={"username": user.username}
    )
    return ApiResponse(data=_user_out(user))
