"""OCR job orchestration: queueing, execution (Celery or in-process), and applying results."""

from __future__ import annotations

import contextlib
import logging
import time
from datetime import UTC, date, datetime
from typing import Any

from sqlalchemy import select
from sqlalchemy.orm import Session

from app.core.config import settings
from app.core.exceptions import NotFoundError
from app.models import (
    Drawing,
    DrawingFile,
    DrawingRevision,
    DrawingStatus,
    FileStatus,
    OCRJob,
    OCRJobStatus,
    OCRResult,
)
from app.services import storage
from app.services.bom.extractor import extract_bom
from app.services.bom_service import rebuild_boq, save_bom
from app.services.categorize import revision_sequence
from app.services.drawing_service import set_current_revision
from app.services.ocr.backends import get_backend
from app.services.ocr.engine import process_pdf

logger = logging.getLogger(__name__)

REGISTER_FIELDS = ("title", "scale", "sheet_number", "vendor")


def create_jobs(
    db: Session, file_ids: list[int], *, extract_bom: bool = True, user_id: int | None = None
) -> list[OCRJob]:
    jobs: list[OCRJob] = []
    files = db.scalars(select(DrawingFile).where(DrawingFile.id.in_(file_ids), DrawingFile.is_deleted.is_(False))).all()
    for f in files:
        job = OCRJob(
            drawing_file_id=f.id,
            revision_id=f.revision_id,
            status=OCRJobStatus.QUEUED,
            extract_bom=extract_bom,
            requested_by_id=user_id,
        )
        f.upload_status = FileStatus.PENDING
        db.add(job)
        jobs.append(job)
    db.flush()
    return jobs


def dispatch(job_ids: list[int]) -> str:
    """Send jobs to Celery when configured; otherwise caller runs them in-process."""
    if settings.OCR_EAGER or not settings.broker_url:
        return "inline"
    from app.workers.tasks import run_ocr_job

    for job_id in job_ids:
        run_ocr_job.delay(job_id)
    return "celery"


def _apply_to_register(db: Session, file: DrawingFile, fields: dict[str, Any], confidence: float) -> None:
    drawing = db.get(Drawing, file.drawing_id)
    if drawing is None:
        return
    for key in REGISTER_FIELDS:
        if fields.get(key):
            setattr(drawing, key, str(fields[key])[:500])
    revision = db.get(DrawingRevision, file.revision_id) if file.revision_id else None
    ocr_rev = fields.get("revision")
    if revision is not None and ocr_rev and revision.revision != ocr_rev:
        clash = db.scalar(
            select(DrawingRevision).where(DrawingRevision.drawing_id == drawing.id, DrawingRevision.revision == ocr_rev)
        )
        # Only rename placeholder revisions (from file names without a revision code)
        if clash is None and (revision.revision.startswith("U") or revision.revision == "0"):
            revision.revision = ocr_rev
            revision.revision_seq = revision_sequence(ocr_rev)
    if revision is not None:
        revision.ocr_confidence = confidence
        if fields.get("status") in DrawingStatus.ALL:
            revision.status = fields["status"]
        if fields.get("date"):
            with contextlib.suppress(ValueError):
                revision.revision_date = date.fromisoformat(fields["date"])
        for key in ("checker", "approver", "drawn_by"):
            if fields.get(key):
                setattr(revision, key, str(fields[key])[:120])
    db.flush()
    db.refresh(drawing, ["revisions"])
    set_current_revision(drawing)


def run_job(db: Session, job_id: int, *, apply_to_register: bool = True) -> OCRJob:
    job = db.get(OCRJob, job_id)
    if job is None:
        raise NotFoundError(f"OCR job {job_id} not found")
    file = db.get(DrawingFile, job.drawing_file_id)
    if file is None:
        raise NotFoundError("Drawing file missing")
    job.status = OCRJobStatus.RUNNING
    job.started_at = datetime.now(UTC)
    job.progress = 10
    file.upload_status = FileStatus.PROCESSING
    db.commit()
    started = time.perf_counter()
    try:
        backend = get_backend(settings.OCR_ENGINE)
        doc = process_pdf(storage.absolute_path(file.storage_path), filename=file.original_filename, backend=backend)
        job.progress = 60
        result = OCRResult(
            job_id=job.id,
            drawing_file_id=file.id,
            extracted=doc.as_json(),
            field_confidences=doc.confidences,
            overall_confidence=doc.overall_confidence,
            revision_block=doc.revision_block,
            notes=doc.notes,
            regions=doc.regions,
            rotation=doc.rotation,
            is_scanned=doc.is_scanned,
            engine=doc.engine,
            raw_text=doc.raw_text,
            needs_review=doc.needs_review,
        )
        db.add(result)
        file.is_scanned = doc.is_scanned
        if apply_to_register:
            _apply_to_register(db, file, doc.fields, doc.overall_confidence)
        if job.extract_bom and file.revision_id:
            extraction = extract_bom(doc.pages, backend if doc.is_scanned else None)
            if extraction.items:
                revision = db.get(DrawingRevision, file.revision_id)
                save_bom(
                    db,
                    revision,
                    extraction.items,
                    source="ocr" if doc.is_scanned else "native",
                    extraction=extraction,
                    user_id=job.requested_by_id,
                )
                drawing = db.get(Drawing, file.drawing_id)
                if drawing is not None:
                    rebuild_boq(db, drawing.project_id)
        job.status = OCRJobStatus.COMPLETED
        job.engine = doc.engine
        job.progress = 100
        file.upload_status = FileStatus.PROCESSED
        file.error_message = None
    except Exception as exc:
        db.rollback()
        logger.exception("OCR job %s failed", job_id)
        job = db.get(OCRJob, job_id)
        file = db.get(DrawingFile, job.drawing_file_id)
        job.status = OCRJobStatus.FAILED
        job.error = str(exc)[:2000]
        file.upload_status = FileStatus.FAILED
        file.error_message = str(exc)[:2000]
    job.finished_at = datetime.now(UTC)
    job.duration_ms = int((time.perf_counter() - started) * 1000)
    db.commit()
    return job


def apply_override(
    db: Session, result: OCRResult, overrides: dict[str, Any], user_id: int | None, apply: bool
) -> OCRResult:
    clean = {k: v for k, v in overrides.items() if v not in (None, "")}
    result.overrides = {**(result.overrides or {}), **clean}
    result.is_overridden = True
    result.needs_review = False
    result.reviewed_by_id = user_id
    result.reviewed_at = datetime.now(UTC)
    if apply:
        file = db.get(DrawingFile, result.drawing_file_id)
        if file is not None:
            effective = result.effective
            drawing = db.get(Drawing, file.drawing_id)
            if drawing is not None and clean.get("drawing_number"):
                drawing.drawing_number = clean["drawing_number"]
            if drawing is not None and clean.get("revision") and file.revision_id:
                revision = db.get(DrawingRevision, file.revision_id)
                if revision is not None:
                    revision.revision = clean["revision"]
                    revision.revision_seq = revision_sequence(clean["revision"])
            _apply_to_register(db, file, effective, 1.0)
    db.flush()
    return result
