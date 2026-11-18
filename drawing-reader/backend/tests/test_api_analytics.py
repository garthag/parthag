"""BOM, revisions, dashboard, analytics, AI assistant and report endpoints — values cross-checked with SQL."""

from __future__ import annotations

import csv
import io
from decimal import Decimal

import pytest
from fastapi.testclient import TestClient
from openpyxl import load_workbook
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.models import BOMHeader, BOMItem, BOQSummary, Drawing, DrawingRevision, Project
from app.services import ai_assistant


def test_bom_listing_and_replace(client: TestClient, auth: dict, viewer: dict) -> None:
    rows = client.get("/api/bom?page_size=5&sort=-total_weight_kg", headers=auth).json()
    weights = [r["total_weight_kg"] for r in rows["data"]]
    assert weights == sorted(weights, reverse=True) and rows["meta"]["total"] > 5
    first = rows["data"][0]
    assert first["drawing_number"] and first["revision"]
    filtered = client.get(
        f"/api/bom?material=CS&grade={first['grade']}&project_id=1&search=&current_only=false", headers=auth
    )
    assert filtered.status_code == 200

    rev = client.get("/api/revisions?current_only=true&page_size=1", headers=auth).json()["data"][0]
    items = [
        {
            "item_number": "1",
            "part_number": "M-1",
            "description": "PL 10 x 200 x 800",
            "material": "CS",
            "grade": "S355JR",
            "quantity": 2,
        },
        {"item_number": "2", "description": "HANDRAIL", "quantity": 3, "unit_weight_kg": 10},
    ]
    res = client.put(f"/api/bom/revisions/{rev['id']}", headers=auth, json={"items": items})
    assert res.status_code == 200
    saved = res.json()["data"]
    assert saved[0]["total_weight_kg"] == 25.12 and saved[0]["weight_calculated"]
    assert saved[1]["total_weight_kg"] == 30
    drawing = client.get(f"/api/drawings/{rev['drawing_id']}", headers=auth).json()["data"]
    assert drawing["current_weight_kg"] == pytest.approx(55.12)
    by_rev = client.get(f"/api/bom?revision_id={rev['id']}", headers=auth).json()
    assert by_rev["meta"]["total"] == 2
    assert client.put(f"/api/bom/revisions/{rev['id']}", headers=viewer, json={"items": items}).status_code == 403
    assert client.put("/api/bom/revisions/999999", headers=auth, json={"items": items}).status_code == 404
    bad = client.put(
        f"/api/bom/revisions/{rev['id']}", headers=auth, json={"items": [{"description": "x", "quantity": -1}]}
    )
    assert bad.status_code == 422


def test_weight_summary_matches_database(client: TestClient, auth: dict, db: Session) -> None:
    total = db.scalar(select(func.sum(Drawing.current_weight_kg)).where(Drawing.is_deleted.is_(False)))
    for group in ("discipline", "project", "area", "status", "drawing"):
        data = client.get(f"/api/bom/weight-summary?group_by={group}", headers=auth).json()["data"]
        assert data["total_weight_kg"] == pytest.approx(float(total), abs=0.01), group
        assert sum(r["total_weight_kg"] for r in data["rows"]) == pytest.approx(float(total), abs=0.01)
    assert client.get("/api/bom/weight-summary?group_by=nope", headers=auth).status_code == 422
    summary = client.get("/api/dashboard/summary", headers=auth).json()["data"]
    assert summary["total_weight_kg"] == pytest.approx(float(total), abs=0.01)


def test_material_summary_and_boq(client: TestClient, auth: dict, db: Session) -> None:
    current_items = db.scalar(
        select(func.sum(BOMItem.total_weight_kg))
        .join(BOMHeader, BOMHeader.id == BOMItem.bom_header_id)
        .join(DrawingRevision, DrawingRevision.id == BOMHeader.revision_id)
        .join(Drawing, Drawing.id == BOMHeader.drawing_id)
        .where(DrawingRevision.is_current.is_(True), Drawing.is_deleted.is_(False))
    )
    rows = client.get("/api/bom/material-summary", headers=auth).json()["data"]
    assert sum(r["total_weight_kg"] for r in rows) == pytest.approx(float(current_items), abs=0.01)
    assert all(r["estimated_cost"] >= 0 for r in rows)
    assert client.get("/api/bom/material-summary?project_id=1&discipline_id=2", headers=auth).status_code == 200

    rebuilt = client.post("/api/bom/boq/rebuild?project_id=1", headers=auth)
    assert rebuilt.status_code == 200 and rebuilt.json()["data"] > 0
    boq = client.get("/api/bom/boq?project_id=1", headers=auth).json()["data"]
    db_total = db.scalar(select(func.sum(BOQSummary.total_weight_kg)).where(BOQSummary.project_id == 1))
    assert sum(r["total_weight_kg"] for r in boq) == pytest.approx(float(db_total), abs=0.01)


def test_rates(client: TestClient, auth: dict, viewer: dict) -> None:
    res = client.post("/api/bom/rates", headers=auth, json={"material": "TI", "grade": "GR2", "rate": 35})
    assert res.status_code == 201
    assert any(r["material"] == "TI" for r in client.get("/api/bom/rates", headers=auth).json()["data"])
    assert client.post("/api/bom/rates", headers=viewer, json={"material": "X", "rate": 1}).status_code == 403
    assert client.post("/api/bom/rates", headers=auth, json={"material": "X", "rate": 0}).status_code == 422


def test_revisions_list_and_compare_errors(client: TestClient, auth: dict) -> None:
    listing = client.get("/api/revisions?project_id=1&page_size=100", headers=auth).json()
    assert listing["meta"]["total"] > 0
    drawing_id = listing["data"][0]["drawing_id"]
    per_drawing = client.get(f"/api/revisions?drawing_id={drawing_id}", headers=auth).json()["data"]
    seqs = [r["revision_seq"] for r in per_drawing]
    assert seqs == sorted(seqs, reverse=True)
    assert client.get("/api/revisions?status=ifc&sort=-revision_date", headers=auth).status_code == 200
    assert client.get("/api/revisions/compare", headers=auth).status_code == 404
    assert (
        client.get(f"/api/revisions/compare?drawing_id={drawing_id}&from_revision=A", headers=auth).status_code == 404
    )
    multi = next(
        (
            d
            for d in client.get("/api/drawings?page_size=100", headers=auth).json()["data"]
            if d["revision_count"] >= 2 and d["project_id"] == 1
        ),
        None,
    )
    assert multi is not None
    revs = client.get(f"/api/revisions?drawing_id={multi['id']}", headers=auth).json()["data"]
    ok = client.get(
        f"/api/revisions/compare?from_revision_id={revs[-1]['id']}&to_revision_id={revs[0]['id']}", headers=auth
    )
    assert ok.status_code == 200
    body = ok.json()["data"]
    assert body["weight_delta_kg"] == pytest.approx(revs[0]["total_weight_kg"] - revs[-1]["total_weight_kg"], abs=0.01)
    by_code = client.get(
        f"/api/revisions/compare?from_revision_id={revs[-1]['id']}&to_revision={revs[0]['revision']}", headers=auth
    )
    assert by_code.status_code == 200
    same = client.get(
        f"/api/revisions/compare?from_revision_id={revs[0]['id']}&to_revision_id={revs[0]['id']}", headers=auth
    )
    assert same.status_code == 422
    assert (
        client.get("/api/revisions/visual-diff?from_revision_id=999998&to_revision_id=999999", headers=auth).status_code
        == 404
    )


def test_dashboard_matches_database(client: TestClient, auth: dict, db: Session) -> None:
    summary = client.get("/api/dashboard/summary", headers=auth).json()["data"]
    assert summary["total_drawings"] == db.scalar(select(func.count(Drawing.id)).where(Drawing.is_deleted.is_(False)))
    assert summary["projects"] == db.scalar(select(func.count(Project.id)).where(Project.is_deleted.is_(False)))
    assert summary["total_weight_t"] == pytest.approx(summary["total_weight_kg"] / 1000, abs=0.001)
    charts = client.get("/api/dashboard/charts", headers=auth).json()["data"]
    assert sum(p["value"] for p in charts["drawings_by_discipline"]) == summary["total_drawings"]
    assert sum(p["value"] for p in charts["weight_by_discipline"]) == pytest.approx(summary["total_weight_t"], abs=0.01)
    assert sum(p["value"] for p in charts["status_breakdown"]) == summary["total_drawings"]
    assert charts["revision_trend"] and charts["monthly_upload_trend"]
    scoped = client.get("/api/dashboard/summary?project_id=1", headers=auth).json()["data"]
    assert scoped["projects"] == 1 and scoped["total_drawings"] <= summary["total_drawings"]
    assert client.get("/api/dashboard/charts?project_id=1", headers=auth).status_code == 200


def test_analytics_endpoints(client: TestClient, auth: dict) -> None:
    material = client.get("/api/analytics/material-analysis", headers=auth).json()["data"]
    assert sum(m["share_pct"] for m in material) == pytest.approx(100, abs=0.1)
    growth = client.get("/api/analytics/weight-growth", headers=auth).json()["data"]
    assert growth and all(s["points"] for s in growth)
    for s in growth:
        cum = 0.0
        for p in s["points"]:
            cum += p["delta_kg"]
            assert p["cumulative_kg"] == pytest.approx(cum, abs=0.01)
    by_disc = client.get("/api/analytics/weight-growth?group_by=discipline&project_id=1", headers=auth)
    assert by_disc.status_code == 200
    impact = client.get("/api/analytics/cost-impact?limit=5", headers=auth).json()["data"]
    costs = [abs(r["cost_delta"]) for r in impact]
    assert costs == sorted(costs, reverse=True)
    proc = client.get("/api/analytics/procurement-impact?project_id=1", headers=auth)
    assert proc.status_code == 200


@pytest.mark.parametrize(
    ("question", "intent", "params"),
    [
        (
            "What changed between R2 and R3 on ABC-ST-0001?",
            "compare_revisions",
            {"drawing_number": "ABC-ST-0001", "from_revision": "R2", "to_revision": "R3"},
        ),
        (
            "compare rev A to rev C for NRP-ST-A01-GA-0001",
            "compare_revisions",
            {"drawing_number": "NRP-ST-A01-GA-0001", "from_revision": "A", "to_revision": "C"},
        ),
        ("Show all IFC drawings", "list_drawings", {"status": "IFC", "discipline": None}),
        ("list structural drawings", "list_drawings", {"status": None, "discipline": "STR"}),
        ("Show drawings with weight increase over 5 tons", "weight_increase", {"threshold_kg": 5000.0}),
        ("weight increased more than 750 kg", "weight_increase", {"threshold_kg": 750.0}),
        ("Which drawing added most steel?", "top_material_increase", {"material": "steel"}),
        ("Show procurement impact", "procurement_impact", {}),
        ("total weight by discipline", "weight_by_discipline", {}),
        ("how many drawings per discipline", "drawings_by_discipline", {}),
        ("give me the MTO", "material_summary", {}),
        ("pending OCR jobs", "pending_ocr", {}),
        ("what is the total project weight", "total_weight", {}),
    ],
)
def test_parse_intent(question: str, intent: str, params: dict) -> None:
    parsed = ai_assistant.parse_intent(question)
    assert parsed is not None and parsed[0] == intent
    for k, v in params.items():
        assert parsed[1].get(k) == v, k


def test_ai_answers_match_database(client: TestClient, auth: dict, db: Session) -> None:
    def ask(q: str, **kw) -> dict:
        res = client.post("/api/ai/query", headers=auth, json={"question": q, **kw})
        assert res.status_code == 200
        return res.json()["data"]

    ifc = ask("Show all IFC drawings")
    assert len(ifc["rows"]) == db.scalar(
        select(func.count(Drawing.id)).where(Drawing.status == "IFC", Drawing.is_deleted.is_(False))
    )
    assert ifc["sql"] and "drawings" in ifc["sql"].lower()
    assert ifc["elapsed_ms"] < 5000
    inc = ask("Show drawings with weight increase over 0.5 tons", project_id=1)
    assert all(r["weight_delta_kg"] > 500 for r in inc["rows"])
    top = ask("Which drawing added most steel?")
    assert top["rows"] and top["rows"][0]["added_kg"] == max(r["added_kg"] for r in top["rows"])
    assert ask("Which drawing added most concrete?")["intent"] == "top_material_increase"
    proc = ask("Show procurement impact")
    assert proc["intent"] == "procurement_impact"
    for q in ("total weight by discipline", "how many drawings per discipline", "MTO", "pending OCR", "total weight"):
        assert ask(q)["intent"] != "unsupported"

    multi = db.scalars(select(Drawing).where(Drawing.project_id == 1, Drawing.is_deleted.is_(False))).all()
    drawing = next(d for d in multi if len([r for r in d.revisions if not r.is_deleted]) >= 2)
    revs = sorted(drawing.revisions, key=lambda r: r.revision_seq)
    cmp = ask(f"What changed between {revs[0].revision} and {revs[1].revision} on {drawing.drawing_number}?")
    assert cmp["intent"] == "compare_revisions" and drawing.drawing_number in cmp["answer"]
    assert "Please include" in ask("what changed between A and B")["answer"]
    assert "No drawing" in ask("what changed between A and B on NOPE-XX-0000")["answer"]
    assert "Could not answer" in ask(f"what changed between A and QQ on {drawing.drawing_number}")["answer"]
    unknown = ask("tell me a joke")
    assert unknown["intent"] == "unsupported" and unknown["suggestions"]


def test_ai_llm_fallback(monkeypatch: pytest.MonkeyPatch, db: Session) -> None:
    import anthropic

    from app.core.config import settings

    class Block:
        type = "text"
        text = (
            '{"intent": "list_drawings", "drawing_number": null, "from_revision": null, "to_revision": null, '
            '"status": "IFC", "discipline": null, "material": null, "threshold_kg": null, "search": null}'
        )

    class Response:
        stop_reason = "end_turn"
        content = [Block()]

    calls: dict = {}

    class Messages:
        def create(self, **kwargs):
            calls.update(kwargs)
            return Response()

    class Client:
        def __init__(self, **_: object) -> None:
            self.beta = type("B", (), {"messages": Messages()})()

        def with_options(self, **opts):
            calls["options"] = opts
            return self

    monkeypatch.setattr(settings, "ANTHROPIC_API_KEY", "test-key")
    monkeypatch.setattr(anthropic, "Anthropic", Client)
    result = ai_assistant.answer(db, "anything flagged for building?")
    assert result["intent"] == "list_drawings" and result["parameters"] == {"status": "IFC"}
    assert calls["options"] == {"timeout": settings.AI_TIMEOUT_SECONDS, "max_retries": 0}
    assert calls["output_config"]["format"]["type"] == "json_schema"
    assert calls["model"] == settings.AI_MODEL

    Response.stop_reason = "refusal"
    assert ai_assistant.classify_with_llm("x") is None
    Response.stop_reason = "end_turn"
    Block.text = "not json"
    assert ai_assistant.classify_with_llm("x") is None
    Block.text = '{"intent": "unsupported"}'
    assert ai_assistant.classify_with_llm("x") is None

    def boom(**_: object):
        raise anthropic.APIConnectionError(request=None)  # type: ignore[arg-type]

    monkeypatch.setattr(Messages, "create", lambda self, **kw: boom())
    assert ai_assistant.classify_with_llm("x") is None


@pytest.mark.parametrize("report", ["drawing-register", "boq", "mto", "cost-impact", "weight-growth"])
@pytest.mark.parametrize("fmt", ["csv", "xlsx", "pdf"])
def test_reports_download(client: TestClient, auth: dict, report: str, fmt: str) -> None:
    res = client.get(f"/api/reports/{report}?format={fmt}", headers=auth)
    assert res.status_code == 200, res.text
    assert f"{report}.{fmt}" in res.headers["content-disposition"]
    if fmt == "pdf":
        assert res.content.startswith(b"%PDF")
    elif fmt == "xlsx":
        assert res.content[:2] == b"PK"


def test_report_values_match_dashboard(client: TestClient, auth: dict) -> None:
    res = client.get("/api/reports/drawing-register?format=csv&project_id=1", headers=auth)
    rows = list(csv.reader(io.StringIO(res.content.decode("utf-8-sig"))))
    summary = client.get("/api/dashboard/summary?project_id=1", headers=auth).json()["data"]
    assert len(rows) - 1 == summary["total_drawings"]
    weight_col = rows[0].index("Weight (kg)")
    assert sum(Decimal(r[weight_col]) for r in rows[1:]) == pytest.approx(
        Decimal(str(summary["total_weight_kg"])), abs=Decimal("0.01")
    )
    wb = load_workbook(io.BytesIO(client.get("/api/reports/boq?format=xlsx&project_id=1", headers=auth).content))
    sheet = wb.active
    values = list(sheet.iter_rows(values_only=True))
    total_row = values[-1]
    boq = client.get("/api/bom/boq?project_id=1", headers=auth).json()["data"]
    assert total_row[0] == "TOTAL"
    assert total_row[5] == pytest.approx(sum(r["total_weight_kg"] for r in boq), abs=0.01)


def test_revision_difference_report(client: TestClient, auth: dict) -> None:
    multi = next(
        d for d in client.get("/api/drawings?page_size=100", headers=auth).json()["data"] if d["revision_count"] >= 2
    )
    revs = client.get(f"/api/revisions?drawing_id={multi['id']}", headers=auth).json()["data"]
    url = (
        f"/api/reports/revision-difference?drawing_id={multi['id']}&from_revision={revs[-1]['revision']}"
        f"&to_revision={revs[0]['revision']}"
    )
    for fmt in ("csv", "xlsx", "pdf"):
        assert client.get(f"{url}&format={fmt}", headers=auth).status_code == 200
    assert client.get("/api/reports/revision-difference?format=csv", headers=auth).status_code == 422
    assert client.get("/api/reports/unknown?format=csv", headers=auth).status_code == 422
    assert client.get("/api/reports/boq?format=doc", headers=auth).status_code == 422
