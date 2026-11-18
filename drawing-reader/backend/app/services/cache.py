"""Tiny read-through cache: Redis when available, otherwise in-process TTL dict."""

from __future__ import annotations

import json
import logging
import threading
import time
from collections.abc import Callable
from typing import Any

from app.core.config import settings

logger = logging.getLogger(__name__)
_local: dict[str, tuple[float, Any]] = {}
_lock = threading.Lock()
_redis = None
_checked = False


def _client():
    global _redis, _checked
    if _checked:
        return _redis
    _checked = True
    if settings.REDIS_URL and settings.ENVIRONMENT != "test":
        try:
            import redis

            c = redis.Redis.from_url(settings.REDIS_URL, socket_connect_timeout=0.5)
            c.ping()
            _redis = c
        except Exception:  # noqa: BLE001
            logger.info("Redis unavailable, using in-process cache")
    return _redis


def cached(key: str, producer: Callable[[], Any], ttl: int | None = None) -> Any:
    ttl = settings.CACHE_TTL_SECONDS if ttl is None else ttl
    if ttl <= 0:
        return producer()
    client = _client()
    if client is not None:
        hit = client.get(f"cache:{key}")
        if hit is not None:
            return json.loads(hit)
        value = producer()
        client.setex(f"cache:{key}", ttl, json.dumps(value, default=str))
        return value
    now = time.time()
    with _lock:
        if key in _local and _local[key][0] > now:
            return _local[key][1]
    value = producer()
    with _lock:
        _local[key] = (now + ttl, value)
    return value


def invalidate(prefix: str = "") -> None:
    client = _client()
    if client is not None:
        for k in client.scan_iter(f"cache:{prefix}*"):
            client.delete(k)
    with _lock:
        for k in [k for k in _local if k.startswith(prefix)]:
            del _local[k]
