"""Password hashing, JWT issuance/verification and token revocation."""

from __future__ import annotations

import logging
import threading
import time
import uuid
from datetime import UTC, datetime, timedelta
from typing import Any, Literal

import bcrypt
from jose import JWTError, jwt

from app.core.config import settings
from app.core.exceptions import AuthenticationError

logger = logging.getLogger(__name__)

TokenType = Literal["access", "refresh"]


def hash_password(password: str) -> str:
    return bcrypt.hashpw(password.encode("utf-8"), bcrypt.gensalt(rounds=12)).decode("utf-8")


def verify_password(password: str, hashed: str) -> bool:
    try:
        return bcrypt.checkpw(password.encode("utf-8"), hashed.encode("utf-8"))
    except ValueError:
        return False


def create_token(subject: str | int, token_type: TokenType, extra: dict[str, Any] | None = None) -> str:
    minutes = settings.ACCESS_TOKEN_EXPIRE_MINUTES if token_type == "access" else settings.REFRESH_TOKEN_EXPIRE_MINUTES  # noqa: S105
    now = datetime.now(UTC)
    payload: dict[str, Any] = {
        "sub": str(subject),
        "type": token_type,
        "iat": int(now.timestamp()),
        "exp": int((now + timedelta(minutes=minutes)).timestamp()),
        "jti": uuid.uuid4().hex,
    }
    if extra:
        payload.update(extra)
    return jwt.encode(payload, settings.SECRET_KEY, algorithm=settings.JWT_ALGORITHM)


def decode_token(token: str, expected_type: TokenType) -> dict[str, Any]:
    try:
        payload = jwt.decode(token, settings.SECRET_KEY, algorithms=[settings.JWT_ALGORITHM])
    except JWTError as exc:
        raise AuthenticationError("Invalid or expired token") from exc
    if payload.get("type") != expected_type:
        raise AuthenticationError("Invalid token type")
    if token_blacklist.is_revoked(payload.get("jti", "")):
        raise AuthenticationError("Token has been revoked")
    return payload


class TokenBlacklist:
    """Revoked JWT ids. Uses Redis when reachable, otherwise a process-local store."""

    def __init__(self) -> None:
        self._local: dict[str, float] = {}
        self._lock = threading.Lock()
        self._redis = None
        self._redis_checked = False

    def _client(self):
        if self._redis_checked:
            return self._redis
        self._redis_checked = True
        if not settings.REDIS_URL or settings.ENVIRONMENT == "test":
            return None
        try:
            import redis

            client = redis.Redis.from_url(settings.REDIS_URL, socket_connect_timeout=0.5)
            client.ping()
            self._redis = client
        except Exception:  # noqa: BLE001
            logger.warning("Redis unavailable; token blacklist is process-local")
        return self._redis

    def revoke(self, jti: str, expires_at: int) -> None:
        ttl = max(int(expires_at - time.time()), 1)
        client = self._client()
        if client is not None:
            client.setex(f"jwt:revoked:{jti}", ttl, "1")
            return
        with self._lock:
            self._local[jti] = expires_at

    def is_revoked(self, jti: str) -> bool:
        client = self._client()
        if client is not None:
            return bool(client.exists(f"jwt:revoked:{jti}"))
        with self._lock:
            now = time.time()
            for key in [k for k, exp in self._local.items() if exp < now]:
                del self._local[key]
            return jti in self._local


token_blacklist = TokenBlacklist()
