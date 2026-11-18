from __future__ import annotations

from typing import Annotated

from fastapi import APIRouter, Depends, Query, Request
from sqlalchemy import func, or_, select

from app.api.deps import CurrentUser, DbSession, Paging, apply_sort, client_info, paginate, require
from app.core.exceptions import ConflictError
from app.models import Area, Discipline, Drawing, Project, System, User
from app.schemas.common import ERROR_RESPONSES, ApiResponse, MessageResponse, PaginationResponse
from app.schemas.project import (
    AreaOut,
    DisciplineOut,
    ProjectCreate,
    ProjectDetail,
    ProjectList,
    ProjectUpdate,
    SystemOut,
)
from app.services import audit
from app.services.cache import invalidate
from app.services.drawing_service import get_project

router = APIRouter(tags=["Projects"], responses=ERROR_RESPONSES)

SORT = {"code": Project.code, "name": Project.name, "created_at": Project.created_at, "status": Project.status}


def _counts(db: DbSession, ids: list[int]) -> dict[int, int]:
    return dict(
        db.execute(
            select(Drawing.project_id, func.count(Drawing.id))
            .where(Drawing.project_id.in_(ids), Drawing.is_deleted.is_(False))
            .group_by(Drawing.project_id)
        ).all()
    )


@router.get("/projects", response_model=PaginationResponse[ProjectList], summary="List projects")
def list_projects(
    db: DbSession,
    _: CurrentUser,
    paging: Paging,
    search: Annotated[str | None, Query(max_length=100)] = None,
    status: str | None = None,
) -> PaginationResponse[ProjectList]:
    stmt = select(Project).where(Project.is_deleted.is_(False))
    if search:
        stmt = stmt.where(or_(Project.code.ilike(f"%{search}%"), Project.name.ilike(f"%{search}%")))
    if status:
        stmt = stmt.where(Project.status == status)
    stmt = apply_sort(stmt, paging.sort, SORT, Project.code)
    items, meta = paginate(db, stmt, paging)
    counts = _counts(db, [p.id for p in items])
    data = [ProjectList.model_validate(p).model_copy(update={"drawing_count": counts.get(p.id, 0)}) for p in items]
    return PaginationResponse(data=data, meta=meta)


@router.post("/projects", response_model=ApiResponse[ProjectDetail], status_code=201, summary="Create a project")
def create_project(
    body: ProjectCreate, db: DbSession, request: Request, user: Annotated[User, Depends(require("projects.manage"))]
) -> ApiResponse[ProjectDetail]:
    if db.scalar(select(Project).where(func.upper(Project.code) == body.code.upper())):
        raise ConflictError(f"Project code {body.code} already exists")
    project = Project(**body.model_dump(), created_by_id=user.id, updated_by_id=user.id)
    project.code = project.code.upper()
    db.add(project)
    db.flush()
    audit.record(
        db,
        action="create",
        entity="project",
        entity_id=project.id,
        user_id=user.id,
        changes=body.model_dump(mode="json"),
        **client_info(request),
    )
    invalidate()
    return ApiResponse(data=ProjectDetail.model_validate(project))


@router.get("/projects/{project_id}", response_model=ApiResponse[ProjectDetail], summary="Project detail")
def get_project_detail(project_id: int, db: DbSession, _: CurrentUser) -> ApiResponse[ProjectDetail]:
    project = get_project(db, project_id)
    detail = ProjectDetail.model_validate(project).model_copy(
        update={"drawing_count": _counts(db, [project.id]).get(project.id, 0)}
    )
    return ApiResponse(data=detail)


@router.put("/projects/{project_id}", response_model=ApiResponse[ProjectDetail], summary="Update a project")
def update_project(
    project_id: int,
    body: ProjectUpdate,
    db: DbSession,
    request: Request,
    user: Annotated[User, Depends(require("projects.manage"))],
) -> ApiResponse[ProjectDetail]:
    project = get_project(db, project_id)
    changes = audit.diff_fields(project, body.model_dump(exclude_unset=True))
    project.updated_by_id = user.id
    if project.start_date and project.end_date and project.end_date < project.start_date:
        from app.core.exceptions import ValidationFailedError

        raise ValidationFailedError("end_date must be on or after start_date")
    audit.record(
        db,
        action="update",
        entity="project",
        entity_id=project.id,
        user_id=user.id,
        changes=changes,
        **client_info(request),
    )
    invalidate()
    return ApiResponse(data=ProjectDetail.model_validate(project))


@router.delete("/projects/{project_id}", response_model=MessageResponse, summary="Soft-delete a project")
def delete_project(
    project_id: int, db: DbSession, request: Request, user: Annotated[User, Depends(require("projects.manage"))]
) -> MessageResponse:
    project = get_project(db, project_id)
    project.soft_delete()
    project.updated_by_id = user.id
    audit.record(db, action="delete", entity="project", entity_id=project.id, user_id=user.id, **client_info(request))
    invalidate()
    return MessageResponse(message=f"Project {project.code} deleted")


@router.get("/disciplines", response_model=ApiResponse[list[DisciplineOut]], tags=["Lookups"])
def list_disciplines(db: DbSession, _: CurrentUser) -> ApiResponse[list[DisciplineOut]]:
    rows = db.scalars(select(Discipline).where(Discipline.is_deleted.is_(False)).order_by(Discipline.code)).all()
    return ApiResponse(data=[DisciplineOut.model_validate(d) for d in rows])


@router.get("/projects/{project_id}/areas", response_model=ApiResponse[list[AreaOut]], tags=["Lookups"])
def list_areas(project_id: int, db: DbSession, _: CurrentUser) -> ApiResponse[list[AreaOut]]:
    rows = db.scalars(select(Area).where(Area.project_id == project_id, Area.is_deleted.is_(False)).order_by(Area.code))
    return ApiResponse(data=[AreaOut.model_validate(a) for a in rows])


@router.get("/projects/{project_id}/systems", response_model=ApiResponse[list[SystemOut]], tags=["Lookups"])
def list_systems(project_id: int, db: DbSession, _: CurrentUser) -> ApiResponse[list[SystemOut]]:
    rows = db.scalars(
        select(System).where(System.project_id == project_id, System.is_deleted.is_(False)).order_by(System.code)
    )
    return ApiResponse(data=[SystemOut.model_validate(s) for s in rows])
