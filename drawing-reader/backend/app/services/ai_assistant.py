"""AI engineering assistant: natural-language questions answered by SQL over the register.

Answers always come from database queries (so they match the dashboard). The question is mapped
to one of a fixed set of *intents* by a deterministic rule parser; if no rule matches and an
Anthropic API key is configured, Claude classifies the question into the same intent schema
(structured JSON output) within a strict timeout. The LLM never writes SQL.
"""

from __future__ import annotations

import json
import logging
import re
import time
from collections.abc import Callable
from typing import Any

from sqlalchemy import Select, func, select
from sqlalchemy.orm import Session, aliased

from app.core.config import settings
from app.models import (
    BOMHeader,
    BOMItem,
    Discipline,
    Drawing,
    DrawingRevision,
    DrawingStatus,
    OCRJob,
    OCRJobStatus,
    RevisionDifference,
)
from app.services import analytics
from app.services.revision_engine import compare_revisions, resolve_revision

logger = logging.getLogger(__name__)

INTENTS = (
    "compare_revisions",
    "list_drawings",
    "weight_increase",
    "top_material_increase",
    "procurement_impact",
    "weight_by_discipline",
    "drawings_by_discipline",
    "material_summary",
    "pending_ocr",
    "total_weight",
)

SUGGESTIONS = [
    "What changed between R2 and R3 on <drawing number>?",
    "Show all IFC drawings",
    "Show drawings with weight increase over 5 tons",
    "Which drawing added most steel?",
    "Show procurement impact",
    "Total weight by discipline",
    "How many drawings per discipline?",
    "Show pending OCR jobs",
]

DISCIPLINE_WORDS = {
    "civil": "CIV",
    "structural": "STR",
    "mechanical": "MEC",
    "piping": "PIP",
    "electrical": "ELE",
    "instrumentation": "INS",
    "architectural": "ARC",
    "process": "PRO",
    "hvac": "HVAC",
}
MATERIAL_WORDS = {
    "steel": ["CS", "STEEL", "S355", "S275", "A36", "A572", "SS"],
    "stainless": ["SS", "STAINLESS"],
    "concrete": ["CONCRETE", "RCC"],
    "aluminium": ["AL", "ALUMIN"],
    "copper": ["CU", "COPPER"],
    "pipe": ["A106", "API 5L", "PIPE"],
    "cable": ["CU", "CABLE", "XLPE"],
}
DRAWING_NO = re.compile(r"\b([A-Z0-9]{2,}(?:-[A-Z0-9]+){2,})\b", re.IGNORECASE)


def _weight_kg(text: str) -> float | None:
    m = re.search(r"(\d+(?:\.\d+)?)\s*(t|ton|tons|tonnes|mt|kg|kgs)\b", text, re.IGNORECASE)
    if not m:
        return None
    value = float(m.group(1))
    return value if m.group(2).lower().startswith("kg") else value * 1000


def parse_intent(question: str) -> tuple[str, dict[str, Any]] | None:
    """Deterministic intent parser for the common engineering questions."""
    q = question.strip()
    ql = q.lower()
    dwg = DRAWING_NO.search(q)
    revs = re.findall(
        r"\b(?:rev(?:ision)?\.?\s*)?(R?\d{1,3}|[A-Z]\d?|P\d)\b(?=\s*(?:and|to|vs\.?|versus|$|\?|,|on|for|of))",
        q,
        re.IGNORECASE,
    )
    if re.search(r"\b(chang|differ|compare|delta)", ql) and re.search(r"\bbetween\b|\bvs\b|\bversus\b|\bto\b", ql):
        pair = re.search(
            r"(?:rev(?:ision)?\.?\s*)?\b([A-Z]?\d{0,3}[A-Z]?)\s+(?:and|to|vs\.?|versus)\s+(?:rev(?:ision)?\.?\s*)?\b([A-Z]?\d{0,3}[A-Z]?)\b",
            q,
            re.IGNORECASE,
        )
        if pair and pair.group(1) and pair.group(2):
            return "compare_revisions", {
                "drawing_number": dwg.group(1).upper() if dwg else None,
                "from_revision": pair.group(1).upper(),
                "to_revision": pair.group(2).upper(),
            }
        if len(revs) >= 2:
            return "compare_revisions", {
                "drawing_number": dwg.group(1).upper() if dwg else None,
                "from_revision": revs[0].upper(),
                "to_revision": revs[1].upper(),
            }
    if "procurement" in ql or "cost impact" in ql or "purchase" in ql:
        return "procurement_impact", {}
    if re.search(r"(which|what|top).*(drawing).*(add|increase|most)", ql) or "added most" in ql:
        material = next((w for w in MATERIAL_WORDS if w in ql), None)
        return "top_material_increase", {"material": material}
    if "weight" in ql and re.search(r"increase|grew|growth|more than|over|exceed|above", ql) and _weight_kg(q):
        return "weight_increase", {"threshold_kg": _weight_kg(q)}
    if ("pending" in ql and ("ocr" in ql or "processing" in ql)) or "ocr queue" in ql:
        return "pending_ocr", {}
    if "weight" in ql and ("discipline" in ql or "by disc" in ql):
        return "weight_by_discipline", {}
    if re.search(r"how many|count|number of", ql) and "discipline" in ql:
        return "drawings_by_discipline", {}
    if re.search(r"material (summary|takeoff|take-off)|\bmto\b|\bboq\b|bill of quantities", ql):
        return "material_summary", {}
    if re.search(r"total (project )?weight|overall weight|how heavy", ql):
        return "total_weight", {}
    status = next((s for s in DrawingStatus.ALL if re.search(rf"\b{re.escape(s.lower())}\b", ql)), None)
    discipline = next((code for word, code in DISCIPLINE_WORDS.items() if word in ql), None)
    if re.search(r"\b(show|list|find|which|all)\b.*\bdrawings?\b", ql) or status or discipline:
        params: dict[str, Any] = {"status": status, "discipline": discipline}
        if m := re.search(r"(?:titled|title|containing|about|for)\s+[\"']?([\w\s-]{3,40})[\"']?$", q, re.IGNORECASE):
            params["search"] = m.group(1).strip()
        return "list_drawings", params
    return None


# --------------------------------------------------------------------------- LLM fallback

INTENT_SCHEMA = {
    "type": "object",
    "properties": {
        "intent": {"type": "string", "enum": [*INTENTS, "unsupported"]},
        "drawing_number": {"type": ["string", "null"]},
        "from_revision": {"type": ["string", "null"]},
        "to_revision": {"type": ["string", "null"]},
        "status": {"type": ["string", "null"], "enum": [*DrawingStatus.ALL, None]},
        "discipline": {"type": ["string", "null"], "enum": [*DISCIPLINE_WORDS.values(), None]},
        "material": {"type": ["string", "null"]},
        "threshold_kg": {"type": ["number", "null"]},
        "search": {"type": ["string", "null"]},
    },
    "required": [
        "intent",
        "drawing_number",
        "from_revision",
        "to_revision",
        "status",
        "discipline",
        "material",
        "threshold_kg",
        "search",
    ],
    "additionalProperties": False,
}

CLASSIFIER_PROMPT = (
    "You route questions about an engineering drawing register (drawings, revisions, BOM, weights, costs) to "
    "one intent. Intents: compare_revisions (BOM changes between two revisions of one drawing), list_drawings "
    "(filter by status/discipline/text), weight_increase (revisions whose weight grew more than threshold_kg), "
    "top_material_increase (drawings that added the most of a material), procurement_impact (cost impact by "
    "material), weight_by_discipline, drawings_by_discipline, material_summary (MTO/BOQ), pending_ocr, "
    "total_weight. Convert tonnes to kg for threshold_kg. Use 'unsupported' if none fits. Fill unused fields "
    "with null."
)


def classify_with_llm(question: str) -> tuple[str, dict[str, Any]] | None:
    if not settings.ANTHROPIC_API_KEY:
        return None
    try:
        import anthropic
    except ImportError:  # pragma: no cover - optional dependency
        return None
    client = anthropic.Anthropic(api_key=settings.ANTHROPIC_API_KEY)
    try:
        response = client.with_options(timeout=settings.AI_TIMEOUT_SECONDS, max_retries=0).beta.messages.create(
            model=settings.AI_MODEL,
            max_tokens=1024,
            system=CLASSIFIER_PROMPT,
            messages=[{"role": "user", "content": question}],
            output_config={"effort": "low", "format": {"type": "json_schema", "schema": INTENT_SCHEMA}},
            betas=["server-side-fallback-2026-07-01"],
            fallbacks="default",
        )
    except anthropic.RateLimitError:
        logger.warning("AI classifier rate limited")
        return None
    except anthropic.APIStatusError as exc:
        logger.warning("AI classifier API error %s", exc.status_code)
        return None
    except anthropic.APIConnectionError:  # includes APITimeoutError
        logger.warning("AI classifier unreachable or timed out")
        return None
    if response.stop_reason == "refusal":
        return None
    text = next((b.text for b in response.content if b.type == "text"), None)
    if not text:
        return None
    try:
        data = json.loads(text)
    except json.JSONDecodeError:
        return None
    intent = data.pop("intent", "unsupported")
    if intent not in INTENTS:
        return None
    return intent, {k: v for k, v in data.items() if v is not None}


# --------------------------------------------------------------------------- execution


def _sql(db: Session, stmt: Select) -> str | None:
    try:
        return str(stmt.compile(dialect=db.get_bind().dialect, compile_kwargs={"literal_binds": True}))
    except Exception:  # noqa: BLE001 - some types can't render literally; SQL text is informational
        return str(stmt)


def _rows(db: Session, stmt: Select, columns: list[str]) -> list[dict[str, Any]]:
    out = []
    for row in db.execute(stmt).all():
        out.append(
            {
                c: (float(v) if hasattr(v, "as_integer_ratio") and not isinstance(v, int | bool) else v)
                for c, v in zip(columns, row, strict=True)
            }
        )
    return out


def _scoped(stmt: Select, project_id: int | None) -> Select:
    return stmt.where(Drawing.project_id == project_id) if project_id else stmt


def _run_compare(db: Session, p: dict, project_id: int | None) -> dict:
    number = p.get("drawing_number")
    if not number:
        return {
            "answer": "Please include the drawing number, e.g. 'What changed between R2 and R3 on ABC-ST-001?'",
            "columns": [],
            "rows": [],
            "sql": None,
        }
    stmt = _scoped(
        select(Drawing).where(Drawing.drawing_number.ilike(number), Drawing.is_deleted.is_(False)), project_id
    )
    drawing = db.scalars(stmt).first()
    if drawing is None:
        return {"answer": f"No drawing {number} found.", "columns": [], "rows": [], "sql": _sql(db, stmt)}
    a = resolve_revision(db, drawing.id, p["from_revision"])
    b = resolve_revision(db, drawing.id, p["to_revision"])
    cmp = compare_revisions(db, a, b)
    rows = [
        {
            "change": d["change_type"],
            "item": d["item_number"],
            "part_number": d["part_number"],
            "description": d["description"],
            "qty_delta": d["quantity_delta"],
            "weight_delta_kg": d["weight_delta_kg"],
            "cost_delta": d["cost_delta"],
            "fields": ", ".join(c["field"] for c in d["changes"]),
        }
        for d in cmp["added"] + cmp["removed"] + cmp["modified"]
    ]
    s = cmp["summary"]
    answer = (
        f"{drawing.drawing_number} rev {a.revision} → {b.revision}: {s['added']} added, {s['removed']} removed, "
        f"{s['modified']} modified ({s['quantity_changes']} quantity, {s['material_changes']} material, "
        f"{s['specification_changes']} specification changes). Weight {cmp['from_weight_kg']:,.1f} → "
        f"{cmp['to_weight_kg']:,.1f} kg (Δ {cmp['weight_delta_kg']:+,.1f} kg), cost Δ "
        f"{cmp['cost_delta']:+,.2f} {cmp['currency']}."
    )
    return {
        "answer": answer,
        "columns": list(rows[0].keys()) if rows else [],
        "rows": rows,
        "sql": "-- BOM items of both revisions loaded by id and compared in the revision engine",
    }


def _run_list(db: Session, p: dict, project_id: int | None) -> dict:
    cols = ["id", "drawing_number", "title", "discipline", "status", "revision", "weight_kg"]
    stmt = (
        select(
            Drawing.id,
            Drawing.drawing_number,
            Drawing.title,
            Discipline.code,
            Drawing.status,
            Drawing.current_revision,
            Drawing.current_weight_kg,
        )
        .outerjoin(Discipline, Discipline.id == Drawing.discipline_id)
        .where(Drawing.is_deleted.is_(False))
        .order_by(Drawing.drawing_number)
        .limit(500)
    )
    if p.get("status"):
        stmt = stmt.where(Drawing.status == p["status"].upper())
    if p.get("discipline"):
        stmt = stmt.where(Discipline.code == p["discipline"].upper())
    if p.get("search"):
        stmt = stmt.where(Drawing.title.ilike(f"%{p['search']}%") | Drawing.drawing_number.ilike(f"%{p['search']}%"))
    stmt = _scoped(stmt, project_id)
    rows = _rows(db, stmt, cols)
    filters = ", ".join(f"{k}={v}" for k, v in p.items() if v) or "no filter"
    return {
        "answer": f"Found {len(rows)} drawing(s) ({filters}).",
        "columns": cols,
        "rows": rows,
        "sql": _sql(db, stmt),
    }


def _run_weight_increase(db: Session, p: dict, project_id: int | None) -> dict:
    threshold = float(p.get("threshold_kg") or 5000)
    fr, to = aliased(DrawingRevision), aliased(DrawingRevision)
    cols = ["drawing_id", "drawing_number", "title", "from_revision", "to_revision", "weight_delta_kg", "cost_delta"]
    stmt = (
        select(
            Drawing.id,
            Drawing.drawing_number,
            Drawing.title,
            fr.revision,
            to.revision,
            RevisionDifference.weight_delta_kg,
            RevisionDifference.cost_delta,
        )
        .join(Drawing, Drawing.id == RevisionDifference.drawing_id)
        .join(fr, fr.id == RevisionDifference.from_revision_id)
        .join(to, to.id == RevisionDifference.to_revision_id)
        .where(RevisionDifference.weight_delta_kg > threshold, Drawing.is_deleted.is_(False))
        .order_by(RevisionDifference.weight_delta_kg.desc())
    )
    stmt = _scoped(stmt, project_id)
    rows = _rows(db, stmt, cols)
    return {
        "answer": f"{len(rows)} revision change(s) increased weight by more than {threshold / 1000:,.2f} t.",
        "columns": cols,
        "rows": rows,
        "sql": _sql(db, stmt),
    }


def _material_filter(material: str | None):
    tokens = MATERIAL_WORDS.get((material or "steel").lower(), [material.upper()] if material else ["CS"])
    from sqlalchemy import or_

    return or_(
        *[func.upper(BOMItem.material).contains(t) for t in tokens],
        *[func.upper(BOMItem.grade).contains(t) for t in tokens],
    )


def _run_top_material(db: Session, p: dict, project_id: int | None) -> dict:
    """Weight added per drawing between its first and current revision for one material family."""
    material = p.get("material") or "steel"
    first_seq = (
        select(DrawingRevision.drawing_id, func.min(DrawingRevision.revision_seq).label("seq"))
        .where(DrawingRevision.is_deleted.is_(False))
        .group_by(DrawingRevision.drawing_id)
        .subquery()
    )
    weight = func.sum(BOMItem.total_weight_kg)
    cur = (
        select(BOMHeader.drawing_id.label("did"), weight.label("w"))
        .join(BOMItem, BOMItem.bom_header_id == BOMHeader.id)
        .join(DrawingRevision, DrawingRevision.id == BOMHeader.revision_id)
        .where(DrawingRevision.is_current.is_(True), _material_filter(material))
        .group_by(BOMHeader.drawing_id)
        .subquery()
    )
    first = (
        select(BOMHeader.drawing_id.label("did"), weight.label("w"))
        .join(BOMItem, BOMItem.bom_header_id == BOMHeader.id)
        .join(DrawingRevision, DrawingRevision.id == BOMHeader.revision_id)
        .join(
            first_seq,
            (first_seq.c.drawing_id == DrawingRevision.drawing_id) & (first_seq.c.seq == DrawingRevision.revision_seq),
        )
        .where(_material_filter(material))
        .group_by(BOMHeader.drawing_id)
        .subquery()
    )
    added = (func.coalesce(cur.c.w, 0) - func.coalesce(first.c.w, 0)).label("added_kg")
    cols = [
        "drawing_id",
        "drawing_number",
        "title",
        "current_revision",
        "first_weight_kg",
        "current_weight_kg",
        "added_kg",
    ]
    stmt = (
        select(
            Drawing.id,
            Drawing.drawing_number,
            Drawing.title,
            Drawing.current_revision,
            func.coalesce(first.c.w, 0),
            func.coalesce(cur.c.w, 0),
            added,
        )
        .join(cur, cur.c.did == Drawing.id)
        .outerjoin(first, first.c.did == Drawing.id)
        .where(Drawing.is_deleted.is_(False))
        .order_by(added.desc())
        .limit(20)
    )
    stmt = _scoped(stmt, project_id)
    rows = _rows(db, stmt, cols)
    if not rows:
        return {"answer": f"No {material} found in current BOMs.", "columns": cols, "rows": [], "sql": _sql(db, stmt)}
    top = rows[0]
    return {
        "answer": f"{top['drawing_number']} added the most {material}: {top['added_kg']:+,.1f} kg since its first "
        f"revision (now {top['current_weight_kg']:,.1f} kg at rev {top['current_revision']}).",
        "columns": cols,
        "rows": rows,
        "sql": _sql(db, stmt),
    }


def _simple(rows: list[dict], answer: str, sql: str | None = None) -> dict:
    return {"answer": answer, "columns": list(rows[0].keys()) if rows else [], "rows": rows, "sql": sql}


def _run_procurement(db: Session, p: dict, project_id: int | None) -> dict:
    rows = analytics.procurement_impact(db, project_id)
    total = sum(r["cost_delta"] for r in rows)
    weight = sum(r["weight_delta_kg"] for r in rows)
    cur = rows[0]["currency"] if rows else settings.DEFAULT_CURRENCY
    return _simple(
        rows,
        f"Revision changes moved procurement by {weight:+,.1f} kg and {total:+,.2f} {cur} "
        f"across {len(rows)} material/grade lines.",
        "SELECT material, grade, SUM(weight_delta_kg), SUM(cost_delta) FROM cost_impacts GROUP BY material, grade",
    )


def _run_weight_by_discipline(db: Session, p: dict, project_id: int | None) -> dict:
    data = analytics.weight_summary(db, "discipline", project_id)
    rows = [
        {"discipline": r["group_label"], "drawings": r["drawing_count"], "weight_t": r["total_weight_t"]}
        for r in data["rows"]
    ]
    return _simple(
        rows,
        f"Total current weight is {float(data['total_weight_kg']) / 1000:,.2f} t across {len(rows)} disciplines.",
        "SELECT discipline, COUNT(*), SUM(current_weight_kg) FROM drawings GROUP BY discipline",
    )


def _run_drawings_by_discipline(db: Session, p: dict, project_id: int | None) -> dict:
    charts = analytics.dashboard_charts(db, project_id)
    rows = [{"discipline": r["label"], "drawings": r["value"]} for r in charts["drawings_by_discipline"]]
    return _simple(
        rows,
        f"{sum(r['drawings'] for r in rows)} drawings across {len(rows)} disciplines.",
        "SELECT discipline, COUNT(*) FROM drawings GROUP BY discipline",
    )


def _run_material_summary(db: Session, p: dict, project_id: int | None) -> dict:
    rows = [
        {
            "material": r["material"],
            "grade": r["grade"],
            "unit": r["unit"],
            "quantity": float(r["total_quantity"]),
            "weight_kg": float(r["total_weight_kg"]),
            "estimated_cost": float(r["estimated_cost"]),
        }
        for r in analytics.material_summary(db, project_id)
    ]
    return _simple(rows, f"{len(rows)} material lines, {sum(r['weight_kg'] for r in rows) / 1000:,.2f} t in total.")


def _run_pending(db: Session, p: dict, project_id: int | None) -> dict:
    cols = ["job_id", "file_id", "status", "progress", "created_at"]
    stmt = (
        select(OCRJob.id, OCRJob.drawing_file_id, OCRJob.status, OCRJob.progress, OCRJob.created_at)
        .where(OCRJob.status.in_([OCRJobStatus.QUEUED, OCRJobStatus.RUNNING]))
        .order_by(OCRJob.id)
        .limit(500)
    )
    rows = _rows(db, stmt, cols)
    return {"answer": f"{len(rows)} OCR job(s) pending.", "columns": cols, "rows": rows, "sql": _sql(db, stmt)}


def _run_total(db: Session, p: dict, project_id: int | None) -> dict:
    s = analytics.dashboard_summary(db, project_id)
    rows = [{"drawings": s["total_drawings"], "revisions": s["total_revisions"], "weight_t": s["total_weight_t"]}]
    return _simple(rows, f"Total current weight is {s['total_weight_t']:,.2f} t over {s['total_drawings']} drawings.")


HANDLERS: dict[str, Callable[[Session, dict, int | None], dict]] = {
    "compare_revisions": _run_compare,
    "list_drawings": _run_list,
    "weight_increase": _run_weight_increase,
    "top_material_increase": _run_top_material,
    "procurement_impact": _run_procurement,
    "weight_by_discipline": _run_weight_by_discipline,
    "drawings_by_discipline": _run_drawings_by_discipline,
    "material_summary": _run_material_summary,
    "pending_ocr": _run_pending,
    "total_weight": _run_total,
}


def answer(db: Session, question: str, project_id: int | None = None) -> dict[str, Any]:
    started = time.perf_counter()
    parsed = parse_intent(question) or classify_with_llm(question)
    if parsed is None:
        return {
            "question": question,
            "intent": "unsupported",
            "columns": [],
            "rows": [],
            "sql": None,
            "parameters": {},
            "elapsed_ms": int((time.perf_counter() - started) * 1000),
            "answer": "I could not map that question to a register query. Try one of the suggestions.",
            "suggestions": SUGGESTIONS,
        }
    intent, params = parsed
    try:
        result = HANDLERS[intent](db, params, project_id)
    except Exception as exc:  # noqa: BLE001 - user-facing message, details logged
        logger.info("AI intent %s failed: %s", intent, exc)
        result = {
            "answer": f"Could not answer: {getattr(exc, 'message', str(exc))}",
            "columns": [],
            "rows": [],
            "sql": None,
        }
    return {
        "question": question,
        "intent": intent,
        "parameters": params,
        "elapsed_ms": int((time.perf_counter() - started) * 1000),
        "suggestions": [],
        **result,
    }
