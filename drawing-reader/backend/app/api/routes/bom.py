from __future__ import annotations

from typing import Annotated

from fastapi import APIRouter, Depends, Query, Request
from sqlalchemy import select

from app.api.deps import CurrentUser, DbSession, Paging, apply_sort, client_info, require
from app.core.exceptions import NotFoundError
from app.models import BOMHeader, BOMItem, Drawing, DrawingRevision, MaterialRate, User
from app.schemas.bom import (
    BOMItemOut,
    BOMReplaceRequest,
    BOQRow,
    MaterialRateIn,
    MaterialRateOut,
    MaterialSummaryRow,
    WeightSummary,
)
from app.schemas.common import ERROR_RESPONSES, ApiResponse, PaginationResponse
from app.services import analytics, audit
from app.services.bom_service import rebuild_boq, save_bom
from app.services.cache import invalidate

router = APIRouter(prefix="/bom", tags=["BOM & Weights"], responses=ERROR_RESPONSES)

SORT = {
    "line_no": BOMItem.line_no,
    "item_number": BOMItem.item_number,
    "material": BOMItem.material,
    "total_weight_kg": BOMItem.total_weight_kg,
    "quantity": BOMItem.quantity,
    "description": BOMItem.description,
}


@router.get("", response_model=PaginationResponse[BOMItemOut], summary="Query BOM items")
def list_bom(
    db: DbSession,
    _: CurrentUser,
    paging: Paging,
    drawing_id: int | None = None,
    revision_id: int | None = None,
    project_id: int | None = None,
    material: str | None = None,
    grade: str | None = None,
    search: Annotated[str | None, Query(max_length=100)] = None,
    current_only: bool = True,
) -> PaginationResponse[BOMItemOut]:
    stmt = (
        select(BOMItem, Drawing.id, Drawing.drawing_number, DrawingRevision.revision)
        .join(BOMHeader, BOMHeader.id == BOMItem.bom_header_id)
        .join(DrawingRevision, DrawingRevision.id == BOMHeader.revision_id)
        .join(Drawing, Drawing.id == BOMHeader.drawing_id)
        .where(BOMItem.is_deleted.is_(False), Drawing.is_deleted.is_(False))
    )
    if revision_id:
        stmt = stmt.where(BOMHeader.revision_id == revision_id)
    elif current_only:
        stmt = stmt.where(DrawingRevision.is_current.is_(True))
    if drawing_id:
        stmt = stmt.where(Drawing.id == drawing_id)
    if project_id:
        stmt = stmt.where(Drawing.project_id == project_id)
    if material:
        stmt = stmt.where(BOMItem.material.ilike(material))
    if grade:
        stmt = stmt.where(BOMItem.grade.ilike(grade))
    if search:
        stmt = stmt.where(BOMItem.description.ilike(f"%{search}%") | BOMItem.part_number.ilike(f"%{search}%"))
    stmt = apply_sort(stmt, paging.sort, SORT, BOMItem.line_no)
    from sqlalchemy import func

    total = db.scalar(select(func.count()).select_from(stmt.order_by(None).subquery())) or 0
    rows = db.execute(stmt.offset(paging.offset).limit(paging.page_size)).all()
    data = [
        BOMItemOut.model_validate(item).model_copy(update={"drawing_id": did, "drawing_number": num, "revision": rev})
        for item, did, num, rev in rows
    ]
    pages = -(-total // paging.page_size) if total else 0
    return PaginationResponse(
        data=data, meta={"page": paging.page, "page_size": paging.page_size, "total": total, "pages": pages}
    )


@router.put(
    "/revisions/{revision_id}",
    response_model=ApiResponse[list[BOMItemOut]],
    summary="Replace the BOM of a revision (manual entry / correction)",
)
def replace_bom(
    revision_id: int,
    body: BOMReplaceRequest,
    db: DbSession,
    request: Request,
    user: Annotated[User, Depends(require("bom.edit"))],
) -> ApiResponse[list[BOMItemOut]]:
    revision = db.get(DrawingRevision, revision_id)
    if revision is None:
        raise NotFoundError(f"Revision {revision_id} not found")
    header = save_bom(db, revision, [i.model_dump() for i in body.items], source="manual", user_id=user.id)
    drawing = db.get(Drawing, revision.drawing_id)
    rebuild_boq(db, drawing.project_id)
    audit.record(
        db,
        action="bom_replace",
        entity="drawing_revision",
        entity_id=revision.id,
        user_id=user.id,
        changes={"items": len(body.items), "total_weight_kg": header.total_weight_kg},
        **client_info(request),
    )
    invalidate()
    return ApiResponse(data=[BOMItemOut.model_validate(i) for i in header.items])


@router.get(
    "/material-summary",
    response_model=ApiResponse[list[MaterialSummaryRow]],
    summary="Quantities and weights grouped by material/grade/unit",
)
def material_summary(
    db: DbSession,
    _: CurrentUser,
    project_id: int | None = None,
    discipline_id: int | None = None,
    current_only: bool = True,
) -> ApiResponse[list[MaterialSummaryRow]]:
    rows = analytics.material_summary(db, project_id, discipline_id, current_only)
    return ApiResponse(data=[MaterialSummaryRow.model_validate(r) for r in rows])


@router.get(
    "/weight-summary",
    response_model=ApiResponse[WeightSummary],
    summary="Current weight rolled up by discipline, project, area, status or drawing",
)
def weight_summary(
    db: DbSession,
    _: CurrentUser,
    group_by: Annotated[str, Query(pattern="^(discipline|project|area|drawing|status)$")] = "discipline",
    project_id: int | None = None,
) -> ApiResponse[WeightSummary]:
    return ApiResponse(data=WeightSummary.model_validate(analytics.weight_summary(db, group_by, project_id)))


@router.get("/boq", response_model=ApiResponse[list[BOQRow]], summary="BOQ summary (current revisions)")
def boq(db: DbSession, _: CurrentUser, project_id: int | None = None) -> ApiResponse[list[BOQRow]]:
    return ApiResponse(data=[BOQRow.model_validate(r) for r in analytics.boq_rows(db, project_id)])


@router.post("/boq/rebuild", response_model=ApiResponse[int], summary="Recalculate BOQ for a project")
def boq_rebuild(project_id: int, db: DbSession, _: Annotated[User, Depends(require("bom.edit"))]) -> ApiResponse[int]:
    count = rebuild_boq(db, project_id)
    invalidate()
    return ApiResponse(data=count)


@router.get("/rates", response_model=ApiResponse[list[MaterialRateOut]], summary="Material rates")
def list_rates(db: DbSession, _: CurrentUser) -> ApiResponse[list[MaterialRateOut]]:
    rows = db.scalars(select(MaterialRate).where(MaterialRate.is_deleted.is_(False)).order_by(MaterialRate.material))
    return ApiResponse(data=[MaterialRateOut.model_validate(r) for r in rows])


@router.post("/rates", response_model=ApiResponse[MaterialRateOut], status_code=201, summary="Add a material rate")
def add_rate(
    body: MaterialRateIn, db: DbSession, user: Annotated[User, Depends(require("rates.manage"))]
) -> ApiResponse[MaterialRateOut]:
    from datetime import date

    rate = MaterialRate(**body.model_dump(), effective_from=date.today(), created_by_id=user.id)
    db.add(rate)
    db.flush()
    invalidate()
    return ApiResponse(data=MaterialRateOut.model_validate(rate))
