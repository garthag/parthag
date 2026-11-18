from __future__ import annotations

import io
import logging
import zipfile
from pathlib import PurePosixPath
from typing import Annotated

from fastapi import APIRouter, BackgroundTasks, Depends, File, Form, Query, Request, UploadFile
from fastapi.responses import FileResponse
from sqlalchemy import select

from app.api.deps import CurrentUser, DbSession, Paging, apply_sort, client_info, paginate, require
from app.core.config import settings
from app.core.exceptions import NotFoundError, ValidationFailedError
from app.db.session import SessionLocal
from app.models import Drawing, DrawingFile, DrawingRevision, DrawingStatus, DrawingTag, User
from app.schemas.common import ERROR_RESPONSES, ApiResponse, MessageResponse, PaginationResponse
from app.schemas.drawing import (
    DrawingDetail,
    DrawingList,
    DrawingUpdate,
    RevisionCreate,
    RevisionOut,
    TreeNode,
    UploadItemResult,
    UploadSummary,
)
from app.services import audit, ocr_service, storage
from app.services.cache import invalidate
from app.services.categorize import revision_sequence
from app.services.drawing_service import (
    SORTABLE,
    LookupCache,
    build_tree,
    drawing_query,
    get_drawing,
    get_project,
    ingest_file,
    serialize_detail,
    serialize_list,
    set_current_revision,
)

logger = logging.getLogger(__name__)
router = APIRouter(prefix="/drawings", tags=["Drawings"], responses=ERROR_RESPONSES)
Uploader = Annotated[User, Depends(require("drawings.upload"))]
Editor = Annotated[User, Depends(require("drawings.edit"))]

MAX_ZIP_ENTRIES = 20_000


def run_ocr_jobs(job_ids: list[int]) -> None:
    """Background task: process OCR jobs in-process with a dedicated session."""
    with SessionLocal() as db:
        for job_id in job_ids:
            try:
                ocr_service.run_job(db, job_id)
            except Exception:
                logger.exception("Background OCR job %s crashed", job_id)
    invalidate()


def _iter_zip(upload: UploadFile):
    """Yield (relative_path, stream) for PDFs inside a zip, rejecting unsafe paths."""
    with zipfile.ZipFile(upload.file) as zf:
        infos = [i for i in zf.infolist() if not i.is_dir()]
        if len(infos) > MAX_ZIP_ENTRIES:
            raise ValidationFailedError(f"Archive has more than {MAX_ZIP_ENTRIES} entries")
        for info in infos:
            path = PurePosixPath(info.filename.replace("\\", "/"))
            if path.is_absolute() or ".." in path.parts or path.name.startswith("."):
                continue
            if info.file_size > settings.MAX_UPLOAD_MB * 1024 * 1024:
                yield path.as_posix(), None
                continue
            with zf.open(info) as fh:
                yield path.as_posix(), io.BytesIO(fh.read())


def _process_uploads(
    db: DbSession,
    background: BackgroundTasks,
    user: User,
    project_id: int,
    files: list[UploadFile],
    paths: list[str] | None,
    discipline_id: int | None,
    run_ocr: bool,
    request: Request,
) -> UploadSummary:
    project = get_project(db, project_id)
    lookups = LookupCache(db, project)
    results: list[UploadItemResult] = []
    paths = paths or []
    for idx, upload in enumerate(files):
        rel = paths[idx] if idx < len(paths) and paths[idx] else upload.filename
        name = PurePosixPath((rel or "upload.pdf").replace("\\", "/")).name
        if name.lower().endswith(".zip"):
            try:
                for inner_rel, stream in _iter_zip(upload):
                    if stream is None:
                        results.append(
                            UploadItemResult(
                                filename=PurePosixPath(inner_rel).name,
                                relative_path=inner_rel,
                                status="failed",
                                error="File too large",
                            )
                        )
                        continue
                    results.append(
                        ingest_file(
                            db,
                            project=project,
                            stream=stream,
                            filename=PurePosixPath(inner_rel).name,
                            relative_path=inner_rel,
                            user_id=user.id,
                            lookups=lookups,
                            discipline_id=discipline_id,
                        )
                    )
            except zipfile.BadZipFile:
                results.append(UploadItemResult(filename=name, status="failed", error="Invalid ZIP archive"))
            continue
        results.append(
            ingest_file(
                db,
                project=project,
                stream=upload.file,
                filename=name,
                relative_path=rel if rel != name else None,
                user_id=user.id,
                lookups=lookups,
                discipline_id=discipline_id,
            )
        )
        if len(results) % settings.UPLOAD_BATCH_COMMIT == 0:
            db.commit()
    file_ids = [r.file_id for r in results if r.status in ("created", "new_revision") and r.file_id]
    if run_ocr and file_ids:
        jobs = ocr_service.create_jobs(db, file_ids, user_id=user.id)
        by_file = {j.drawing_file_id: j.id for j in jobs}
        for r in results:
            if r.file_id in by_file:
                r.ocr_job_id = by_file[r.file_id]
        db.commit()
        if ocr_service.dispatch([j.id for j in jobs]) == "inline":
            background.add_task(run_ocr_jobs, [j.id for j in jobs])
    succeeded = sum(1 for r in results if r.status in ("created", "new_revision"))
    duplicates = sum(1 for r in results if r.status == "duplicate")
    failed = sum(1 for r in results if r.status == "failed")
    audit.record(
        db,
        action="upload",
        entity="project",
        entity_id=project.id,
        user_id=user.id,
        changes={"files": len(results), "succeeded": succeeded, "failed": failed},
        **client_info(request),
    )
    invalidate()
    total = len(results)
    return UploadSummary(
        total=total,
        succeeded=succeeded,
        failed=failed,
        duplicates=duplicates,
        success_rate=round((succeeded + duplicates) / total * 100, 2) if total else 0.0,
        items=results,
    )


@router.post(
    "/upload",
    response_model=ApiResponse[UploadSummary],
    status_code=201,
    summary="Upload one or more PDF drawings (or ZIP archives)",
)
def upload_drawings(
    db: DbSession,
    background: BackgroundTasks,
    request: Request,
    user: Uploader,
    project_id: Annotated[int, Form()],
    files: Annotated[list[UploadFile], File(description="PDF files or ZIP archives")],
    discipline_id: Annotated[int | None, Form()] = None,
    run_ocr: Annotated[bool, Form()] = True,
) -> ApiResponse[UploadSummary]:
    summary = _process_uploads(db, background, user, project_id, files, None, discipline_id, run_ocr, request)
    return ApiResponse(data=summary)


@router.post(
    "/folder-upload",
    response_model=ApiResponse[UploadSummary],
    status_code=201,
    summary="Upload a folder hierarchy; sub-folders drive discipline/area categorisation",
)
def folder_upload(
    db: DbSession,
    background: BackgroundTasks,
    request: Request,
    user: Uploader,
    project_id: Annotated[int, Form()],
    files: Annotated[list[UploadFile], File()],
    paths: Annotated[list[str] | None, Form(description="Relative path of each file, same order as files")] = None,
    run_ocr: Annotated[bool, Form()] = True,
) -> ApiResponse[UploadSummary]:
    if paths and len(paths) != len(files):
        raise ValidationFailedError("'paths' must have one entry per file")
    summary = _process_uploads(db, background, user, project_id, files, paths, None, run_ocr, request)
    return ApiResponse(data=summary)


@router.get("", response_model=PaginationResponse[DrawingList], summary="Search and filter the drawing register")
def list_drawings(
    db: DbSession,
    _: CurrentUser,
    paging: Paging,
    project_id: int | None = None,
    discipline_id: int | None = None,
    discipline: Annotated[str | None, Query(description="Discipline code or name, e.g. STR")] = None,
    area_id: int | None = None,
    status: Annotated[str | None, Query(description="Comma separated statuses, e.g. IFC,AFC")] = None,
    drawing_type: str | None = None,
    search: Annotated[
        str | None, Query(max_length=200, description="Matches number, title, vendor, discipline")
    ] = None,
    drawing_number: str | None = None,
    title: str | None = None,
    folder: str | None = None,
    tag: str | None = None,
    min_weight_kg: Annotated[float | None, Query(ge=0)] = None,
) -> PaginationResponse[DrawingList]:
    stmt = drawing_query(
        project_id=project_id,
        discipline_id=discipline_id,
        discipline_code=discipline,
        area_id=area_id,
        status=status,
        drawing_type=drawing_type,
        search=search,
        drawing_number=drawing_number,
        title=title,
        folder=folder,
        tag=tag,
        min_weight_kg=min_weight_kg,
    )
    stmt = apply_sort(stmt, paging.sort, SORTABLE, Drawing.drawing_number)
    items, meta = paginate(db, stmt, paging)
    return PaginationResponse(data=[DrawingList.model_validate(d) for d in serialize_list(db, items)], meta=meta)


@router.get("/tree", response_model=ApiResponse[list[TreeNode]], summary="Project > Discipline > Area > Drawing tree")
def drawing_tree(
    db: DbSession, _: CurrentUser, project_id: int | None = None, search: str | None = None
) -> ApiResponse[list[TreeNode]]:
    return ApiResponse(data=[TreeNode.model_validate(n) for n in build_tree(db, project_id, search)])


@router.get(
    "/{drawing_id}", response_model=ApiResponse[DrawingDetail], summary="Drawing detail with revisions and files"
)
def get_drawing_detail(drawing_id: int, db: DbSession, _: CurrentUser) -> ApiResponse[DrawingDetail]:
    return ApiResponse(data=DrawingDetail.model_validate(serialize_detail(db, get_drawing(db, drawing_id))))


@router.put("/{drawing_id}", response_model=ApiResponse[DrawingDetail], summary="Update drawing metadata")
def update_drawing(
    drawing_id: int, body: DrawingUpdate, db: DbSession, request: Request, user: Editor
) -> ApiResponse[DrawingDetail]:
    drawing = get_drawing(db, drawing_id)
    data = body.model_dump(exclude_unset=True)
    tags = data.pop("tags", None)
    changes = audit.diff_fields(drawing, data)
    if tags is not None:
        drawing.tags = [DrawingTag(tag=t.strip()[:80]) for t in sorted(set(tags)) if t.strip()]
        changes["tags"] = tags
    drawing.updated_by_id = user.id
    audit.record(
        db,
        action="update",
        entity="drawing",
        entity_id=drawing.id,
        user_id=user.id,
        changes=changes,
        **client_info(request),
    )
    db.flush()
    invalidate()
    return ApiResponse(data=DrawingDetail.model_validate(serialize_detail(db, drawing)))


@router.delete("/{drawing_id}", response_model=MessageResponse, summary="Soft-delete a drawing")
def delete_drawing(drawing_id: int, db: DbSession, request: Request, user: Editor) -> MessageResponse:
    drawing = get_drawing(db, drawing_id)
    drawing.soft_delete()
    drawing.updated_by_id = user.id
    audit.record(db, action="delete", entity="drawing", entity_id=drawing.id, user_id=user.id, **client_info(request))
    invalidate()
    return MessageResponse(message=f"Drawing {drawing.drawing_number} deleted")


@router.post(
    "/{drawing_id}/revisions",
    response_model=ApiResponse[RevisionOut],
    status_code=201,
    summary="Register a revision manually",
)
def create_revision(drawing_id: int, body: RevisionCreate, db: DbSession, user: Editor) -> ApiResponse[RevisionOut]:
    drawing = get_drawing(db, drawing_id)
    if any(r.revision == body.revision.upper() for r in drawing.revisions):
        from app.core.exceptions import ConflictError

        raise ConflictError(f"Revision {body.revision} already exists")
    rev = DrawingRevision(
        drawing_id=drawing.id,
        revision=body.revision.upper(),
        revision_seq=revision_sequence(body.revision),
        revision_date=body.revision_date,
        status=body.status,
        description=body.description,
        checker=body.checker,
        approver=body.approver,
        created_by_id=user.id,
    )
    drawing.revisions.append(rev)
    db.flush()
    set_current_revision(drawing)
    invalidate()
    return ApiResponse(data=RevisionOut.model_validate(rev))


@router.get(
    "/files/{file_id}/content",
    summary="Stream a drawing PDF (for the PDF viewer)",
    response_class=FileResponse,
    responses={200: {"content": {"application/pdf": {}}}},
)
def file_content(file_id: int, db: DbSession, _: CurrentUser) -> FileResponse:
    file = db.get(DrawingFile, file_id)
    if file is None or file.is_deleted:
        raise NotFoundError("File not found")
    path = storage.absolute_path(file.storage_path)
    if not path.exists():
        raise NotFoundError("File content missing from storage")
    return FileResponse(
        path,
        media_type="application/pdf",
        filename=file.original_filename,
        content_disposition_type="inline",
        headers={"Cache-Control": "private, max-age=3600"},
    )


@router.get("/{drawing_id}/revisions/{revision_id}/file", summary="Latest PDF of a revision")
def revision_file(drawing_id: int, revision_id: int, db: DbSession, user: CurrentUser) -> FileResponse:
    file = db.scalar(
        select(DrawingFile)
        .where(
            DrawingFile.drawing_id == drawing_id,
            DrawingFile.revision_id == revision_id,
            DrawingFile.is_deleted.is_(False),
        )
        .order_by(DrawingFile.id.desc())
    )
    if file is None:
        raise NotFoundError("No file for this revision")
    return file_content(file.id, db, user)


STATUSES = list(DrawingStatus.ALL)
