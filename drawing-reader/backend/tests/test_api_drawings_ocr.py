"""Drawing upload (files, folders, ZIP), OCR pipeline, search, tree, revisions and overrides."""

from __future__ import annotations

import io
import zipfile

from fastapi.testclient import TestClient

from tests.conftest import make_bom, make_pdf


def _upload(client: TestClient, auth: dict, project_id: int, files: list[tuple[str, bytes]], **form) -> dict:
    res = client.post(
        "/api/drawings/upload",
        headers=auth,
        data={"project_id": str(project_id), **{k: str(v) for k, v in form.items()}},
        files=[("files", (name, data, "application/pdf")) for name, data in files],
    )
    assert res.status_code == 201, res.text
    return res.json()["data"]


def test_upload_runs_ocr_and_bom(client: TestClient, auth: dict, project_id: int) -> None:
    pdf = make_pdf("TST-ST-A01-GA-0001", "A", bom=make_bom(6), title="ACCESS PLATFORM GENERAL ARRANGEMENT")
    summary = _upload(client, auth, project_id, [("TST-ST-A01-GA-0001.pdf", pdf)])
    assert summary["succeeded"] == 1 and summary["success_rate"] == 100.0
    item = summary["items"][0]
    assert item["status"] == "created" and item["discipline"] == "STR" and item["ocr_job_id"]

    job = client.get(f"/api/ocr/job/{item['ocr_job_id']}", headers=auth).json()["data"]
    assert job["status"] == "completed" and job["result_id"]
    result = client.get(f"/api/ocr/result/{job['result_id']}", headers=auth).json()["data"]
    assert result["extracted"]["drawing_number"] == "TST-ST-A01-GA-0001"
    assert result["overall_confidence"] > 0.9 and result["bom_item_count"] == 6

    drawing = client.get(f"/api/drawings/{item['drawing_id']}", headers=auth).json()["data"]
    assert drawing["title"] == "ACCESS PLATFORM GENERAL ARRANGEMENT"
    assert drawing["current_revision"] == "A"  # placeholder "0" renamed from OCR
    assert drawing["status"] == "IFC" and drawing["scale"] == "1:50" and drawing["vendor"] == "ACME STEEL"
    assert drawing["area_code"] == "A01" and drawing["drawing_type"] == "GA"
    assert drawing["current_weight_kg"] == sum(float(b.total_weight_kg) for b in make_bom(6))
    assert drawing["files"][0]["upload_status"] == "processed"
    assert drawing["latest_ocr_result_id"] == job["result_id"]

    # duplicate content is detected
    again = _upload(client, auth, project_id, [("copy.pdf", pdf)])
    assert again["duplicates"] == 1 and again["items"][0]["drawing_id"] == item["drawing_id"]

    # next revision with changed BOM, then compare
    pdf_b = make_pdf("TST-ST-A01-GA-0001", "B", bom=make_bom(6, qty=3) + make_bom(1, start=50))
    rev_b = _upload(client, auth, project_id, [("TST-ST-A01-GA-0001_RevB.pdf", pdf_b)])["items"][0]
    assert rev_b["status"] == "new_revision" and rev_b["revision"] == "B"
    cmp = client.get(
        f"/api/revisions/compare?drawing_id={item['drawing_id']}&from_revision=A&to_revision=B", headers=auth
    ).json()["data"]
    assert cmp["summary"]["added"] == 1 and cmp["summary"]["modified"] == 6
    assert cmp["weight_delta_kg"] > 0 and cmp["cost_delta"] > 0
    files = client.get(f"/api/drawings/{item['drawing_id']}", headers=auth).json()["data"]["files"]
    visual = client.get(
        f"/api/revisions/visual-diff?from_revision_id={files[0]['revision_id']}"
        f"&to_revision_id={files[-1]['revision_id']}",
        headers=auth,
    )
    assert visual.status_code == 200 and visual.json()["data"]["regions"]


def test_upload_rejects_bad_files(client: TestClient, auth: dict, project_id: int) -> None:
    summary = _upload(client, auth, project_id, [("notes.txt", b"hello"), ("fake.pdf", b"not a pdf")])
    assert summary["failed"] == 2 and summary["succeeded"] == 0
    errors = {i["filename"]: i["error"] for i in summary["items"]}
    assert errors["notes.txt"] == "Only PDF files are supported"
    assert errors["fake.pdf"] == "File is not a valid PDF"
    res = client.post(
        "/api/drawings/upload",
        headers=auth,
        data={"project_id": "999999"},
        files=[("files", ("a.pdf", make_pdf(), "application/pdf"))],
    )
    assert res.status_code == 404


def test_folder_upload_with_paths(client: TestClient, auth: dict, project_id: int) -> None:
    files = [
        ("files", ("1001.pdf", make_pdf("1001", bom=[]), "application/pdf")),
        ("files", ("2002_RevC.pdf", make_pdf("2002", "C", bom=[]), "application/pdf")),
        ("files", ("3003.pdf", make_pdf("3003", bom=[]), "application/pdf")),
    ]
    paths = [
        "Site/02_Piping/Area 03/1001.pdf",
        "Site/05_Electrical/Area 01/2002_RevC.pdf",
        "Site/Misc/deep/nested/3003.pdf",
    ]
    res = client.post(
        "/api/drawings/folder-upload",
        headers=auth,
        data={"project_id": str(project_id), "paths": paths, "run_ocr": "false"},
        files=files,
    )
    assert res.status_code == 201, res.text
    items = {i["drawing_number"]: i for i in res.json()["data"]["items"]}
    assert items["1001"]["discipline"] == "PIP" and items["2002"]["discipline"] == "ELE"
    assert items["2002"]["revision"] == "C" and items["3003"]["discipline"] is None
    assert all(i["ocr_job_id"] is None for i in items.values())
    detail = client.get(f"/api/drawings/{items['1001']['drawing_id']}", headers=auth).json()["data"]
    assert detail["folder_path"] == "Site/02_Piping/Area 03" and detail["area_code"] == "AREA03"
    by_folder = client.get(f"/api/drawings?project_id={project_id}&folder=Site/05", headers=auth).json()
    assert by_folder["meta"]["total"] == 1
    mismatch = client.post(
        "/api/drawings/folder-upload",
        headers=auth,
        data={"project_id": str(project_id), "paths": ["only-one"]},
        files=files[:2],
    )
    assert mismatch.status_code == 422


def test_zip_upload_nested_folders(client: TestClient, auth: dict, project_id: int) -> None:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        for i in range(1, 6):
            zf.writestr(f"Package/Structural/A02/Z-ST-A02-FAB-{i:04d}.pdf", make_pdf(f"Z-ST-A02-FAB-{i:04d}", bom=[]))
        zf.writestr("../evil.pdf", make_pdf(bom=[]))
        zf.writestr("Package/.hidden.pdf", b"x")
        zf.writestr("Package/readme.txt", b"hello")
    summary = _upload(client, auth, project_id, [("package.zip", buf.getvalue())], run_ocr="false")
    assert summary["succeeded"] == 5 and summary["failed"] == 1  # readme.txt
    assert all(i["discipline"] == "STR" for i in summary["items"] if i["status"] == "created")
    tree = client.get(f"/api/drawings/tree?project_id={project_id}", headers=auth).json()["data"]
    disc = next(c for c in tree[0]["children"] if c["label"].startswith("STR"))
    area = next(a for a in disc["children"] if a["label"].startswith("A02"))
    assert area["count"] == 5 and area["children"][0]["type"] == "drawing"
    bad = _upload(client, auth, project_id, [("broken.zip", b"PK-not-really")])
    assert bad["items"][0]["error"] == "Invalid ZIP archive"


def test_search_filters(client: TestClient, auth: dict) -> None:
    by_number = client.get("/api/drawings?drawing_number=NRP-ST", headers=auth).json()
    assert by_number["meta"]["total"] >= 1
    assert all("NRP-ST" in d["drawing_number"] for d in by_number["data"])
    title = by_number["data"][0]["title"].split()[0]
    by_title = client.get(f"/api/drawings?title={title}", headers=auth).json()["data"]
    assert all(title.lower() in d["title"].lower() for d in by_title)
    by_disc = client.get("/api/drawings?discipline=STR", headers=auth).json()["data"]
    assert by_disc and all(d["discipline_code"] == "STR" for d in by_disc)
    by_name = client.get("/api/drawings?discipline=structural", headers=auth).json()["meta"]["total"]
    assert by_name == len(by_disc) or by_name >= len(by_disc)
    free = client.get("/api/drawings?search=piping", headers=auth).json()["data"]
    assert free and all(d["discipline_code"] == "PIP" or "PIPING" in d["title"] for d in free)
    status = client.get("/api/drawings?status=IFC,AFC&sort=-current_weight_kg", headers=auth).json()["data"]
    weights = [d["current_weight_kg"] for d in status]
    assert weights == sorted(weights, reverse=True) and all(d["status"] in ("IFC", "AFC") for d in status)
    assert client.get("/api/drawings?min_weight_kg=1000000000", headers=auth).json()["meta"]["total"] == 0
    for q in ("drawing_type=GA", "area_id=1", "discipline_id=2", "tag=none", "project_id=1"):
        assert client.get(f"/api/drawings?{q}", headers=auth).status_code == 200


def test_update_tags_delete_and_manual_revision(client: TestClient, auth: dict, viewer: dict) -> None:
    did = client.get("/api/drawings?project_id=1&page_size=1", headers=auth).json()["data"][0]["id"]
    res = client.put(f"/api/drawings/{did}", headers=auth, json={"remarks": "Checked", "tags": ["critical", "steel"]})
    assert res.status_code == 200 and res.json()["data"]["tags"] == ["critical", "steel"]
    assert client.get("/api/drawings?tag=critical", headers=auth).json()["meta"]["total"] == 1
    assert client.put(f"/api/drawings/{did}", headers=auth, json={"status": "BOGUS"}).status_code == 422
    assert client.put(f"/api/drawings/{did}", headers=viewer, json={"remarks": "x"}).status_code == 403

    rev = client.post(f"/api/drawings/{did}/revisions", headers=auth, json={"revision": "z9", "status": "IFR"})
    assert rev.status_code == 201 and rev.json()["data"]["revision"] == "Z9"
    assert client.post(f"/api/drawings/{did}/revisions", headers=auth, json={"revision": "Z9"}).status_code == 409

    files = client.get(f"/api/drawings/{did}", headers=auth).json()["data"]["files"]
    pdf = client.get(f"/api/drawings/files/{files[0]['id']}/content", headers=auth)
    assert pdf.status_code == 200 and pdf.content.startswith(b"%PDF")
    assert pdf.headers["content-type"] == "application/pdf"
    by_rev = client.get(f"/api/drawings/{did}/revisions/{files[0]['revision_id']}/file", headers=auth)
    assert by_rev.status_code == 200
    assert client.get(f"/api/drawings/{did}/revisions/999999/file", headers=auth).status_code == 404
    assert client.get("/api/drawings/files/999999/content", headers=auth).status_code == 404

    victim = client.get("/api/drawings?project_id=2&page_size=1&sort=-drawing_number", headers=auth).json()["data"][0]
    assert client.delete(f"/api/drawings/{victim['id']}", headers=auth).status_code == 200
    assert client.get(f"/api/drawings/{victim['id']}", headers=auth).status_code == 404


def test_ocr_process_review_and_override(client: TestClient, auth: dict, project_id: int) -> None:
    summary = _upload(client, auth, project_id, [("blurry.pdf", make_pdf(scanned=True, dpi=60, bom=[]))])
    job_id = summary["items"][0]["ocr_job_id"]
    job = client.get(f"/api/ocr/job/{job_id}", headers=auth).json()["data"]
    assert job["status"] == "completed"
    queue = client.get("/api/ocr/review-queue", headers=auth).json()
    result = next(r for r in queue["data"] if r["id"] == job["result_id"])
    assert result["needs_review"] and result["is_scanned"]

    override = client.put(
        f"/api/ocr/result/{result['id']}",
        headers=auth,
        json={"drawing_number": "TST-CV-A05-FDN-0099", "title": "PUMP FOUNDATION", "revision": "B", "status": "IFA"},
    )
    assert override.status_code == 200
    data = override.json()["data"]
    assert data["is_overridden"] and not data["needs_review"]
    assert data["effective"]["title"] == "PUMP FOUNDATION"
    drawing = client.get(f"/api/drawings/{summary['items'][0]['drawing_id']}", headers=auth).json()["data"]
    assert drawing["drawing_number"] == "TST-CV-A05-FDN-0099" and drawing["title"] == "PUMP FOUNDATION"
    assert drawing["current_revision"] == "B" and drawing["status"] == "IFA"

    reprocess = client.post("/api/ocr/process", headers=auth, json={"drawing_ids": [drawing["id"]]})
    assert reprocess.status_code == 202 and reprocess.json()["data"][0]["status"] == "queued"
    jobs = client.get("/api/ocr/jobs?status=completed", headers=auth).json()
    assert jobs["meta"]["total"] >= 2
    assert client.post("/api/ocr/process", headers=auth, json={}).status_code == 422
    assert client.post("/api/ocr/process", headers=auth, json={"file_ids": [999999]}).status_code == 404
    assert client.get("/api/ocr/job/999999", headers=auth).status_code == 404
    assert client.get("/api/ocr/result/999999", headers=auth).status_code == 404
    assert client.put("/api/ocr/result/999999", headers=auth, json={}).status_code == 404


def test_ocr_job_failure_is_recorded(client: TestClient, auth: dict, project_id: int, db) -> None:
    from app.models import DrawingFile
    from app.services import ocr_service

    summary = _upload(client, auth, project_id, [("gone.pdf", make_pdf("GONE-ST-0001", bom=[]))], run_ocr="false")
    file = db.get(DrawingFile, summary["items"][0]["file_id"])
    file.storage_path = "TST/xx/missing.pdf"
    db.commit()
    job = ocr_service.create_jobs(db, [file.id])[0]
    db.commit()
    done = ocr_service.run_job(db, job.id)
    assert done.status == "failed" and done.error
    assert db.get(DrawingFile, file.id).upload_status == "failed"
