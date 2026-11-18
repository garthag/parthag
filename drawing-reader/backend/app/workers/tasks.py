"""Celery tasks."""

from __future__ import annotations

import logging

from app.db.session import SessionLocal
from app.services import ocr_service
from app.services.cache import invalidate
from app.workers.celery_app import celery_app

logger = logging.getLogger(__name__)


@celery_app.task(name="ocr.run_job", bind=True, max_retries=2, default_retry_delay=30)
def run_ocr_job(self, job_id: int) -> dict:
    try:
        with SessionLocal() as db:
            job = ocr_service.run_job(db, job_id)
            invalidate()
            return {"job_id": job.id, "status": job.status}
    except Exception as exc:  # pragma: no cover - infrastructure failure (DB down etc.)
        logger.exception("OCR task %s crashed", job_id)
        raise self.retry(exc=exc) from exc
