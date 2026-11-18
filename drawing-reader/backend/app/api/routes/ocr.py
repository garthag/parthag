from __future__ import annotations

from typing import Annotated

from fastapi import APIRouter, BackgroundTasks, Depends, Query, Request
from sqlalchemy import func, select

from app.api.deps import CurrentUser, DbSession, Paging, client_info, paginate, require
from app.api.routes.drawings import run_ocr_jobs
from app.core.exceptions import NotFoundError, ValidationFailedError
from app.models import BOMHeader, DrawingFile, OCRJob, OCRResult, User
from app.schemas.common import ERROR_RESPONSES, ApiResponse, PaginationResponse
from app.schemas.ocr import OCRJobOut, OCROverrideRequest, OCRProcessRequest, OCRResultOut
from app.services import audit, ocr_service
from app.services.cache import invalidate

router = APIRouter(prefix="/ocr", tags=["OCR"], responses=ERROR_RESPONSES)
Processor = Annotated[User, Depends(require("ocr.process"))]


def _job_out(db: DbSession, job: OCRJob) -> OCRJobOut:
    result_id = db.scalar(select(OCRResult.id).where(OCRResult.job_id == job.id))
    return OCRJobOut.model_validate(job).model_copy(update={"result_id": result_id})


def _result_out(db: DbSession, result: OCRResult) -> OCRResultOut:
    file = db.get(DrawingFile, result.drawing_file_id)
    count = 0
    if file and file.revision_id:
        count = db.scalar(select(BOMHeader.item_count).where(BOMHeader.revision_id == file.revision_id)) or 0
    data = {c: getattr(result, c) for c in OCRResultOut.model_fields if hasattr(result, c)}
    data["bom_item_count"] = count
    return OCRResultOut.model_validate(data)


@router.post(
    "/process",
    response_model=ApiResponse[list[OCRJobOut]],
    status_code=202,
    summary="Queue OCR for files or for the latest files of drawings",
)
def process(
    body: OCRProcessRequest, db: DbSession, background: BackgroundTasks, request: Request, user: Processor
) -> ApiResponse[list[OCRJobOut]]:
    file_ids = set(body.file_ids)
    if body.drawing_ids:
        latest = (
            select(func.max(DrawingFile.id))
            .where(DrawingFile.drawing_id.in_(body.drawing_ids), DrawingFile.is_deleted.is_(False))
            .group_by(DrawingFile.drawing_id, DrawingFile.revision_id)
        )
        file_ids.update(db.scalars(latest))
    if not file_ids:
        raise ValidationFailedError("Provide file_ids or drawing_ids")
    jobs = ocr_service.create_jobs(db, sorted(file_ids), extract_bom=body.extract_bom, user_id=user.id)
    if not jobs:
        raise NotFoundError("No matching files")
    audit.record(
        db,
        action="ocr_queue",
        entity="ocr_job",
        entity_id=None,
        user_id=user.id,
        changes={"jobs": [j.id for j in jobs]},
        **client_info(request),
    )
    db.commit()
    if ocr_service.dispatch([j.id for j in jobs]) == "inline":
        background.add_task(run_ocr_jobs, [j.id for j in jobs])
    return ApiResponse(data=[_job_out(db, j) for j in jobs])


@router.get("/jobs", response_model=PaginationResponse[OCRJobOut], summary="List OCR jobs")
def list_jobs(
    db: DbSession,
    _: CurrentUser,
    paging: Paging,
    status: Annotated[str | None, Query(pattern="^(queued|running|completed|failed)$")] = None,
) -> PaginationResponse[OCRJobOut]:
    stmt = select(OCRJob).order_by(OCRJob.id.desc())
    if status:
        stmt = stmt.where(OCRJob.status == status)
    items, meta = paginate(db, stmt, paging)
    return PaginationResponse(data=[_job_out(db, j) for j in items], meta=meta)


@router.get("/job/{job_id}", response_model=ApiResponse[OCRJobOut], summary="OCR job status")
def get_job(job_id: int, db: DbSession, _: CurrentUser) -> ApiResponse[OCRJobOut]:
    job = db.get(OCRJob, job_id)
    if job is None:
        raise NotFoundError(f"OCR job {job_id} not found")
    return ApiResponse(data=_job_out(db, job))


@router.get("/result/{result_id}", response_model=ApiResponse[OCRResultOut], summary="OCR extraction result")
def get_result(result_id: int, db: DbSession, _: CurrentUser) -> ApiResponse[OCRResultOut]:
    result = db.get(OCRResult, result_id)
    if result is None:
        raise NotFoundError(f"OCR result {result_id} not found")
    return ApiResponse(data=_result_out(db, result))


@router.get(
    "/review-queue",
    response_model=PaginationResponse[OCRResultOut],
    summary="Results below the confidence threshold awaiting manual review",
)
def review_queue(db: DbSession, _: CurrentUser, paging: Paging) -> PaginationResponse[OCRResultOut]:
    stmt = select(OCRResult).where(OCRResult.needs_review.is_(True)).order_by(OCRResult.overall_confidence)
    items, meta = paginate(db, stmt, paging)
    return PaginationResponse(data=[_result_out(db, r) for r in items], meta=meta)


@router.put("/result/{result_id}", response_model=ApiResponse[OCRResultOut], summary="Manually override OCR fields")
def override_result(
    result_id: int,
    body: OCROverrideRequest,
    db: DbSession,
    request: Request,
    user: Annotated[User, Depends(require("drawings.edit"))],
) -> ApiResponse[OCRResultOut]:
    result = db.get(OCRResult, result_id)
    if result is None:
        raise NotFoundError(f"OCR result {result_id} not found")
    values = body.model_dump(exclude={"apply_to_register"}, exclude_unset=True)
    ocr_service.apply_override(db, result, values, user.id, body.apply_to_register)
    audit.record(
        db,
        action="ocr_override",
        entity="ocr_result",
        entity_id=result.id,
        user_id=user.id,
        changes=values,
        **client_info(request),
    )
    invalidate()
    return ApiResponse(data=_result_out(db, result))
