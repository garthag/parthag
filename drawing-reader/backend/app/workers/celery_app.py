"""Celery application (broker/result backend: Redis)."""

from __future__ import annotations

from celery import Celery

from app.core.config import settings
from app.core.logging import configure_logging

celery_app = Celery(
    "drawing_reader", broker=settings.broker_url, backend=settings.result_backend, include=["app.workers.tasks"]
)
celery_app.conf.update(
    task_serializer="json",
    result_serializer="json",
    accept_content=["json"],
    task_acks_late=True,
    task_reject_on_worker_lost=True,
    worker_prefetch_multiplier=1,
    task_time_limit=900,
    task_soft_time_limit=840,
    task_default_queue="ocr",
    broker_connection_retry_on_startup=True,
    result_expires=86400,
)
configure_logging(settings.LOG_LEVEL, settings.LOG_JSON)
