"""Drawing register: ingestion of uploads, revision management, queries and tree navigation."""

from __future__ import annotations

import logging
from collections import defaultdict
from datetime import date
from typing import BinaryIO

from sqlalchemy import Select, func, or_, select
from sqlalchemy.orm import Session, selectinload

from app.core.exceptions import NotFoundError, ValidationFailedError
from app.models import (
    Area,
    Discipline,
    Drawing,
    DrawingFile,
    DrawingRevision,
    DrawingStatus,
    DrawingTag,
    FileStatus,
    OCRJob,
    OCRResult,
    Project,
)
from app.schemas.drawing import UploadItemResult
from app.services import storage
from app.services.categorize import categorize, revision_sequence
from app.services.ocr.pdf_loader import page_count

logger = logging.getLogger(__name__)


class LookupCache:
    """Per-request cache of disciplines/areas to avoid N queries during bulk uploads."""

    def __init__(self, db: Session, project: Project) -> None:
        self.db = db
        self.project = project
        self.disciplines = {d.code: d for d in db.scalars(select(Discipline).where(Discipline.is_deleted.is_(False)))}
        self.areas = {a.code.upper(): a for a in db.scalars(select(Area).where(Area.project_id == project.id))}

    def area(self, code: str | None, create: bool = True) -> Area | None:
        if not code:
            return None
        key = code.upper()
        if key not in self.areas and create:
            area = Area(project_id=self.project.id, code=key, name=f"Area {key}")
            self.db.add(area)
            self.db.flush()
            self.areas[key] = area
        return self.areas.get(key)


def get_project(db: Session, project_id: int) -> Project:
    project = db.get(Project, project_id)
    if project is None or project.is_deleted:
        raise NotFoundError(f"Project {project_id} not found")
    return project


def set_current_revision(drawing: Drawing) -> DrawingRevision | None:
    live = [r for r in drawing.revisions if not r.is_deleted]
    if not live:
        drawing.current_revision = None
        return None
    current = max(live, key=lambda r: (r.revision_seq, r.id or 0))
    for rev in live:
        rev.is_current = rev is current
    drawing.current_revision = current.revision
    drawing.current_weight_kg = current.total_weight_kg or 0
    if current.status:
        drawing.status = current.status
    return current


def ingest_file(
    db: Session,
    *,
    project: Project,
    stream: BinaryIO,
    filename: str,
    relative_path: str | None,
    user_id: int | None,
    lookups: LookupCache,
    discipline_id: int | None = None,
    default_status: str = DrawingStatus.IFR,
) -> UploadItemResult:
    """Store one PDF and register it as a drawing revision. Never raises for per-file problems."""
    result = UploadItemResult(filename=filename, relative_path=relative_path, status="failed")
    if not filename.lower().endswith(".pdf"):
        result.error = "Only PDF files are supported"
        return result
    try:
        storage_path, sha, size = storage.save_stream(stream, project.code)
    except ValidationFailedError as exc:
        result.error = exc.message
        return result

    dup = db.scalar(
        select(DrawingFile)
        .join(Drawing, Drawing.id == DrawingFile.drawing_id)
        .where(Drawing.project_id == project.id, DrawingFile.sha256 == sha, DrawingFile.is_deleted.is_(False))
    )
    if dup is not None:
        result.status = "duplicate"
        result.drawing_id, result.file_id, result.revision_id = dup.drawing_id, dup.id, dup.revision_id
        return result

    cat = categorize(filename, relative_path)
    discipline = None
    if discipline_id:
        discipline = db.get(Discipline, discipline_id)
    elif cat.discipline_code:
        discipline = lookups.disciplines.get(cat.discipline_code)
    area = lookups.area(cat.area_code)

    try:
        pages = page_count(storage.absolute_path(storage_path))
    except Exception as exc:  # noqa: BLE001 - corrupt PDF
        result.error = f"Unreadable PDF: {exc}"
        return result

    with db.begin_nested():
        drawing = db.scalar(
            select(Drawing)
            .options(selectinload(Drawing.revisions))
            .where(Drawing.project_id == project.id, Drawing.drawing_number == cat.drawing_number)
        )
        created = drawing is None
        if drawing is None:
            drawing = Drawing(
                project_id=project.id,
                drawing_number=cat.drawing_number or filename,
                title="",
                discipline_id=discipline.id if discipline else None,
                area_id=area.id if area else None,
                drawing_type=cat.drawing_type,
                status=default_status,
                folder_path=cat.folder_path,
                total_sheets=pages,
                created_by_id=user_id,
                updated_by_id=user_id,
            )
            db.add(drawing)
            db.flush()
        else:
            drawing.updated_by_id = user_id
            if discipline and drawing.discipline_id is None:
                drawing.discipline_id = discipline.id

        rev_code = cat.revision or ("0" if created else None)
        if rev_code is None:
            # same drawing uploaded again without a revision in its name: next sequential placeholder
            rev_code = f"U{len(drawing.revisions) + 1}"
        revision = next((r for r in drawing.revisions if r.revision == rev_code), None)
        if revision is None:
            revision = DrawingRevision(
                drawing_id=drawing.id,
                revision=rev_code,
                revision_seq=revision_sequence(rev_code),
                revision_date=date.today(),
                status=default_status,
                created_by_id=user_id,
            )
            drawing.revisions.append(revision)
            db.flush()
        file = DrawingFile(
            drawing_id=drawing.id,
            revision_id=revision.id,
            original_filename=filename,
            relative_path=relative_path,
            storage_path=storage_path,
            file_size=size,
            sha256=sha,
            page_count=pages,
            upload_status=FileStatus.PENDING,
            created_by_id=user_id,
        )
        db.add(file)
        set_current_revision(drawing)
        db.flush()

    result.status = "created" if created else "new_revision"
    result.drawing_id, result.revision_id, result.file_id = drawing.id, revision.id, file.id
    result.drawing_number, result.revision = drawing.drawing_number, revision.revision
    result.discipline = discipline.code if discipline else None
    return result


# ---------------------------------------------------------------- queries

SORTABLE = {
    "drawing_number": Drawing.drawing_number,
    "title": Drawing.title,
    "status": Drawing.status,
    "current_revision": Drawing.current_revision,
    "created_at": Drawing.created_at,
    "updated_at": Drawing.updated_at,
    "current_weight_kg": Drawing.current_weight_kg,
}


def drawing_query(
    *,
    project_id: int | None = None,
    discipline_id: int | None = None,
    discipline_code: str | None = None,
    area_id: int | None = None,
    status: str | None = None,
    drawing_type: str | None = None,
    search: str | None = None,
    drawing_number: str | None = None,
    title: str | None = None,
    folder: str | None = None,
    tag: str | None = None,
    min_weight_kg: float | None = None,
) -> Select:
    stmt = select(Drawing).where(Drawing.is_deleted.is_(False))
    if project_id:
        stmt = stmt.where(Drawing.project_id == project_id)
    if discipline_id:
        stmt = stmt.where(Drawing.discipline_id == discipline_id)
    if discipline_code:
        stmt = stmt.join(Discipline, Discipline.id == Drawing.discipline_id).where(
            or_(
                func.upper(Discipline.code) == discipline_code.upper(),
                func.upper(Discipline.name) == discipline_code.upper(),
            )
        )
    if area_id:
        stmt = stmt.where(Drawing.area_id == area_id)
    if status:
        stmt = stmt.where(Drawing.status.in_([s.strip().upper() for s in status.split(",")]))
    if drawing_type:
        stmt = stmt.where(Drawing.drawing_type == drawing_type.upper())
    if drawing_number:
        stmt = stmt.where(Drawing.drawing_number.ilike(f"%{drawing_number.strip()}%"))
    if title:
        stmt = stmt.where(Drawing.title.ilike(f"%{title.strip()}%"))
    if folder:
        stmt = stmt.where(Drawing.folder_path.ilike(f"{folder.strip()}%"))
    if tag:
        stmt = stmt.where(Drawing.id.in_(select(DrawingTag.drawing_id).where(DrawingTag.tag == tag)))
    if min_weight_kg is not None:
        stmt = stmt.where(Drawing.current_weight_kg >= min_weight_kg)
    if search:
        term = f"%{search.strip()}%"
        stmt = stmt.outerjoin(Discipline, Discipline.id == Drawing.discipline_id) if not discipline_code else stmt
        stmt = stmt.where(
            or_(
                Drawing.drawing_number.ilike(term),
                Drawing.title.ilike(term),
                Drawing.vendor.ilike(term),
                Discipline.name.ilike(term),
                Discipline.code.ilike(term),
            )
        )
    return stmt


def serialize_list(db: Session, drawings: list[Drawing]) -> list[dict]:
    if not drawings:
        return []
    ids = [d.id for d in drawings]
    rev_counts = dict(
        db.execute(
            select(DrawingRevision.drawing_id, func.count())
            .where(DrawingRevision.drawing_id.in_(ids), DrawingRevision.is_deleted.is_(False))
            .group_by(DrawingRevision.drawing_id)
        ).all()
    )
    confidences = dict(
        db.execute(
            select(DrawingRevision.drawing_id, DrawingRevision.ocr_confidence).where(
                DrawingRevision.drawing_id.in_(ids), DrawingRevision.is_current.is_(True)
            )
        ).all()
    )
    project_codes = dict(db.execute(select(Project.id, Project.code)).all())
    area_codes = dict(
        db.execute(select(Area.id, Area.code).where(Area.project_id.in_({d.project_id for d in drawings}))).all()
    )
    out = []
    for d in drawings:
        out.append(
            {
                **{
                    c: getattr(d, c)
                    for c in (
                        "id",
                        "created_at",
                        "updated_at",
                        "project_id",
                        "discipline_id",
                        "area_id",
                        "system_id",
                        "drawing_number",
                        "title",
                        "drawing_type",
                        "status",
                        "current_revision",
                        "scale",
                        "sheet_number",
                        "vendor",
                        "folder_path",
                        "current_weight_kg",
                        "total_sheets",
                        "originator",
                        "remarks",
                    )
                },
                "project_code": project_codes.get(d.project_id),
                "discipline_code": d.discipline.code if d.discipline else None,
                "area_code": area_codes.get(d.area_id),
                "revision_count": rev_counts.get(d.id, 0),
                "ocr_confidence": confidences.get(d.id),
            }
        )
    return out


def get_drawing(db: Session, drawing_id: int) -> Drawing:
    drawing = db.scalar(
        select(Drawing)
        .options(selectinload(Drawing.revisions), selectinload(Drawing.files), selectinload(Drawing.tags))
        .where(Drawing.id == drawing_id, Drawing.is_deleted.is_(False))
    )
    if drawing is None:
        raise NotFoundError(f"Drawing {drawing_id} not found")
    return drawing


def serialize_detail(db: Session, drawing: Drawing) -> dict:
    base = serialize_list(db, [drawing])[0]
    latest_result = db.scalar(
        select(OCRResult.id)
        .join(DrawingFile, DrawingFile.id == OCRResult.drawing_file_id)
        .where(DrawingFile.drawing_id == drawing.id)
        .order_by(OCRResult.id.desc())
        .limit(1)
    )
    base.update(
        {
            "tags": sorted(t.tag for t in drawing.tags),
            "revisions": sorted(
                [r for r in drawing.revisions if not r.is_deleted], key=lambda r: r.revision_seq, reverse=True
            ),
            "files": [f for f in drawing.files if not f.is_deleted],
            "latest_ocr_result_id": latest_result,
        }
    )
    return base


def build_tree(db: Session, project_id: int | None = None, search: str | None = None) -> list[dict]:
    """Project > Discipline > Area > Drawing navigation tree (drawings capped per area for UI speed)."""
    stmt = drawing_query(project_id=project_id, search=search).order_by(Drawing.drawing_number)
    rows = db.execute(
        stmt.with_only_columns(
            Drawing.id,
            Drawing.project_id,
            Drawing.discipline_id,
            Drawing.area_id,
            Drawing.drawing_number,
            Drawing.title,
            Drawing.current_revision,
            Drawing.status,
        )
    ).all()
    projects = {p.id: p for p in db.scalars(select(Project).where(Project.is_deleted.is_(False)))}
    disciplines = {d.id: d for d in db.scalars(select(Discipline))}
    areas = {a.id: a for a in db.scalars(select(Area))}
    grouped: dict[int, dict[int | None, dict[int | None, list]]] = defaultdict(
        lambda: defaultdict(lambda: defaultdict(list))
    )
    for row in rows:
        grouped[row.project_id][row.discipline_id][row.area_id].append(row)
    tree = []
    for pid in sorted(grouped, key=lambda p: projects[p].code if p in projects else ""):
        project = projects.get(pid)
        if project is None:
            continue
        p_children = []
        p_count = 0
        for did, by_area in sorted(
            grouped[pid].items(), key=lambda kv: disciplines[kv[0]].code if kv[0] in disciplines else "ZZZ"
        ):
            disc = disciplines.get(did)
            d_children = []
            d_count = 0
            for aid, drawings in sorted(by_area.items(), key=lambda kv: areas[kv[0]].code if kv[0] in areas else "ZZZ"):
                area = areas.get(aid)
                d_count += len(drawings)
                d_children.append(
                    {
                        "id": f"a-{pid}-{did}-{aid}",
                        "label": f"{area.code} – {area.name}" if area else "Unassigned area",
                        "type": "area",
                        "entity_id": aid,
                        "count": len(drawings),
                        "meta": {},
                        "children": [
                            {
                                "id": f"d-{r.id}",
                                "label": r.drawing_number,
                                "type": "drawing",
                                "entity_id": r.id,
                                "count": 0,
                                "meta": {"title": r.title, "revision": r.current_revision, "status": r.status},
                                "children": [],
                            }
                            for r in drawings[:500]
                        ],
                    }
                )
            p_count += d_count
            p_children.append(
                {
                    "id": f"disc-{pid}-{did}",
                    "label": f"{disc.code} – {disc.name}" if disc else "Unclassified",
                    "type": "discipline",
                    "entity_id": did,
                    "count": d_count,
                    "meta": {"color": disc.color if disc else None},
                    "children": d_children,
                }
            )
        tree.append(
            {
                "id": f"p-{pid}",
                "label": f"{project.code} – {project.name}",
                "type": "project",
                "entity_id": pid,
                "count": p_count,
                "meta": {},
                "children": p_children,
            }
        )
    return tree


def pending_ocr_jobs_for(db: Session, file_ids: list[int]) -> dict[int, int]:
    rows = db.execute(select(OCRJob.drawing_file_id, OCRJob.id).where(OCRJob.drawing_file_id.in_(file_ids))).all()
    return dict(rows)
