from __future__ import annotations

from typing import Annotated

from fastapi import APIRouter, Query
from sqlalchemy import func, select

from app.api.deps import CurrentUser, DbSession, Paging, apply_sort
from app.core.exceptions import NotFoundError
from app.models import BOMHeader, Discipline, Drawing, DrawingFile, DrawingRevision
from app.schemas.common import ERROR_RESPONSES, ApiResponse, PaginationResponse
from app.schemas.revision import RevisionComparison, RevisionListItem, VisualDiff
from app.services import storage
from app.services.revision_engine import compare_revisions, resolve_revision
from app.services.visual_diff import visual_diff

router = APIRouter(prefix="/revisions", tags=["Revisions"], responses=ERROR_RESPONSES)

SORT = {
    "revision_date": DrawingRevision.revision_date,
    "revision_seq": DrawingRevision.revision_seq,
    "drawing_number": Drawing.drawing_number,
    "total_weight_kg": DrawingRevision.total_weight_kg,
    "created_at": DrawingRevision.created_at,
}


@router.get("", response_model=PaginationResponse[RevisionListItem], summary="List revisions")
def list_revisions(
    db: DbSession,
    _: CurrentUser,
    paging: Paging,
    drawing_id: int | None = None,
    project_id: int | None = None,
    status: str | None = None,
    current_only: bool = False,
) -> PaginationResponse[RevisionListItem]:
    stmt = (
        select(
            DrawingRevision,
            Drawing.drawing_number,
            Drawing.title,
            Drawing.project_id,
            Discipline.code,
            func.coalesce(BOMHeader.item_count, 0),
        )
        .join(Drawing, Drawing.id == DrawingRevision.drawing_id)
        .outerjoin(Discipline, Discipline.id == Drawing.discipline_id)
        .outerjoin(BOMHeader, BOMHeader.revision_id == DrawingRevision.id)
        .where(DrawingRevision.is_deleted.is_(False), Drawing.is_deleted.is_(False))
    )
    if drawing_id:
        stmt = stmt.where(DrawingRevision.drawing_id == drawing_id)
    if project_id:
        stmt = stmt.where(Drawing.project_id == project_id)
    if status:
        stmt = stmt.where(DrawingRevision.status == status.upper())
    if current_only:
        stmt = stmt.where(DrawingRevision.is_current.is_(True))
    default = DrawingRevision.revision_seq.desc() if drawing_id else DrawingRevision.id.desc()
    stmt = apply_sort(stmt, paging.sort, SORT, default)
    total = db.scalar(select(func.count()).select_from(stmt.order_by(None).subquery())) or 0
    rows = db.execute(stmt.offset(paging.offset).limit(paging.page_size)).all()
    data = []
    for rev, number, title, project_id_, disc, count in rows:
        data.append(
            RevisionListItem.model_validate(
                {
                    **{
                        k: getattr(rev, k)
                        for k in (
                            "id",
                            "drawing_id",
                            "revision",
                            "revision_seq",
                            "revision_date",
                            "status",
                            "is_current",
                            "total_weight_kg",
                        )
                    },
                    "drawing_number": number,
                    "title": title,
                    "project_id": project_id_,
                    "discipline_code": disc,
                    "bom_item_count": count,
                }
            )
        )
    pages = -(-total // paging.page_size) if total else 0
    return PaginationResponse(
        data=data, meta={"page": paging.page, "page_size": paging.page_size, "total": total, "pages": pages}
    )


@router.get(
    "/compare",
    response_model=ApiResponse[RevisionComparison],
    summary="Compare two revisions: added/removed/modified BOM items, weight and cost impact",
)
def compare(
    db: DbSession,
    _: CurrentUser,
    from_revision_id: int | None = None,
    to_revision_id: int | None = None,
    drawing_id: Annotated[int | None, Query(description="Required when using revision codes")] = None,
    from_revision: Annotated[str | None, Query(description="Revision code, e.g. B or R2")] = None,
    to_revision: str | None = None,
) -> ApiResponse[RevisionComparison]:
    a = resolve_revision(db, None, from_revision_id) if from_revision_id else None
    b = resolve_revision(db, None, to_revision_id) if to_revision_id else None
    if a is None:
        if not (drawing_id and from_revision):
            raise NotFoundError("Specify from_revision_id or drawing_id + from_revision")
        a = resolve_revision(db, drawing_id, from_revision)
    if b is None:
        if not (drawing_id or a) or not to_revision:
            raise NotFoundError("Specify to_revision_id or drawing_id + to_revision")
        b = resolve_revision(db, drawing_id or a.drawing_id, to_revision)
    return ApiResponse(data=RevisionComparison.model_validate(compare_revisions(db, a, b)))


def _latest_file(db: DbSession, revision_id: int) -> DrawingFile:
    f = db.scalar(
        select(DrawingFile)
        .where(DrawingFile.revision_id == revision_id, DrawingFile.is_deleted.is_(False))
        .order_by(DrawingFile.id.desc())
    )
    if f is None:
        raise NotFoundError(f"Revision {revision_id} has no file")
    return f


@router.get(
    "/visual-diff",
    response_model=ApiResponse[VisualDiff],
    summary="Changed regions and detected revision clouds between two revision PDFs",
)
def visual(
    db: DbSession,
    _: CurrentUser,
    from_revision_id: int,
    to_revision_id: int,
    page: Annotated[int, Query(ge=0, le=500)] = 0,
) -> ApiResponse[VisualDiff]:
    fa = _latest_file(db, from_revision_id)
    fb = _latest_file(db, to_revision_id)
    result = visual_diff(storage.absolute_path(fa.storage_path), storage.absolute_path(fb.storage_path), page)
    return ApiResponse(data=VisualDiff(from_revision_id=from_revision_id, to_revision_id=to_revision_id, **result))
