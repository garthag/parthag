"""Index, filters, smart fields, text/tables, export, share."""
import io
import time
import zipfile
from datetime import date
from email import message_from_bytes

import pytest
from openpyxl import load_workbook

from mail_agent import exporter, smartfields, textract
from mail_agent.demo import DemoSource
from mail_agent.engine import SearchEngine
from mail_agent.index import Index
from mail_agent.models import SearchRequest
from mail_agent.share import parse_recipients, smtp_for

OCR = bool(textract.ocr_engine())


@pytest.fixture(scope="module")
def eng():
    e = SearchEngine(DemoSource(), workers=4)
    e.sync()
    return e


def idx_req(**kw):
    return SearchRequest(source="index", **kw)


# ---------------------------------------------------------------- index
def test_sync_is_incremental_and_persistent(tmp_path):
    path = str(tmp_path / "i.db")
    e = SearchEngine(DemoSource(), workers=2, index=Index(path))
    first = e.sync()
    assert first["added"] > 50
    assert e.sync()["added"] == 0                          # nothing new -> nothing fetched
    e.close()
    again = Index(path)                                    # survives restart
    assert again.stats()["attachments"] == first["stats"]["attachments"]


def test_sync_prunes_deleted_mail(tmp_path):
    src = DemoSource()
    e = SearchEngine(src, workers=2, index=Index(str(tmp_path / "p.db")))
    e.sync()
    before = e.index.stats()["attachments"]
    victim = next(iter(src.folders["INBOX"]))
    del src.folders["INBOX"][victim]
    assert e.sync()["removed"] == 1
    assert e.index.stats()["attachments"] < before


def test_index_search_matches_live_and_is_fast(eng):
    base = dict(senders=["mck kutty"], keywords=["invoice"], date_from=date(2024, 1, 1),
                date_to=date(2025, 12, 25), file_types=["pdf"])
    live = {r["filename"] for r in SearchEngine(DemoSource()).search(SearchRequest(**base))["results"]}
    t = time.perf_counter()
    idx = {r["filename"] for r in eng.search(idx_req(**base))["results"]}
    assert time.perf_counter() - t < 0.5
    assert idx == live and len(idx) == 25


# -------------------------------------------------------------- filters
def test_match_modes(eng):
    anyr = eng.search(idx_req(keywords=["invoice", "price"], senders=["mck kutty"]))["results"]
    allr = eng.search(idx_req(keywords=["price", "list"], match_mode="all", senders=["mck kutty"]))["results"]
    phr = eng.search(idx_req(keywords=["price list"], match_mode="phrase"))["results"]
    assert any(r["filename"] == "PriceList_2025.pdf" for r in anyr)
    assert [r["filename"] for r in allr] == ["PriceList_2025.pdf"]
    assert [r["filename"] for r in phr] == ["PriceList_2025.pdf"]


def test_fuzzy_toggle(eng):
    assert eng.search(idx_req(senders=["mck kuty"]))["results"]                    # typo tolerated
    assert not eng.search(idx_req(senders=["mck kuty"], fuzzy=False))["results"]


def test_recipient_pattern_size_doctype(eng):
    hr = eng.search(idx_req(recipients=["payroll@company.com"]))["results"]
    assert hr and all("payroll" in r["to"] for r in hr)
    pat = eng.search(idx_req(filename_pattern="MCK_Invoice_102?.pdf"))["results"]
    assert pat and all(r["filename"].startswith("MCK_Invoice_102") for r in pat)
    big = eng.search(idx_req(senders=["mck"], size_min=10_000))["results"]
    assert all(r["size"] >= 10_000 for r in big)
    pays = eng.search(idx_req(doc_types=["payslip"]))["results"]
    assert pays and all(r["fields"]["doc_type"] == "payslip" for r in pays)


def test_amount_filter_after_reading(eng):
    req = idx_req(senders=["mck kutty"], date_from=date(2025, 1, 1), date_to=date(2025, 3, 31), file_types=["pdf"])
    ids = [r["id"] for r in eng.search(req)["results"]]
    rep = eng.analyze(ids, ocr=False)
    assert rep["analyzed"] == len(ids) and not rep["failed"]
    rows = eng.search(req)["results"]
    amounts = [r["fields"]["amount"] for r in rows if r["filename"].startswith("MCK_Invoice")]
    assert len(amounts) == 3 and all(a and a > 0 for a in amounts)
    assert [r["fields"]["amount"] for r in rows if r["filename"] == "PriceList_2025.pdf"] == [None]
    cut = sorted(amounts)[len(amounts) // 2]
    hi = eng.search(idx_req(senders=["mck kutty"], amount_min=cut, file_types=["pdf"],
                            date_from=date(2025, 1, 1), date_to=date(2025, 3, 31)))["results"]
    assert hi and all(r["fields"]["amount"] >= cut for r in hi)
    r0 = next(r for r in rows if r["filename"].startswith("MCK_Invoice"))["fields"]
    assert r0["doc_type"] == "invoice" and r0["doc_no"] and r0["gstin"] == "32ABCDE1234F1Z5" and r0["currency"] == "INR"


def test_user_correction_wins(eng):
    a = eng.search(idx_req(senders=["mck kutty"], file_types=["pdf"]))["results"][0]
    eng.set_field(a["id"], "amount", "1,23,456.50")
    eng.analyze([a["id"]], ocr=False, force=True)         # re-reading must not overwrite it
    assert eng.index.get(a["id"]).fields["amount"] == 123456.5


# ---------------------------------------------------- smart fields / text
@pytest.mark.parametrize("text,expect", [
    ("TAX INVOICE\nInvoice No: INV/24-25/0012   Invoice Date: 15/03/2025\nGrand Total   Rs. 12,390.00",
     {"doc_type": "invoice", "doc_no": "INV/24-25/0012", "doc_date": "2025-03-15", "amount": 12390.0, "currency": "INR"}),
    ("Payment received. Total paid $1,234.50 on Nov 3, 2025. Receipt #R-88812",
     {"doc_type": "receipt", "doc_no": "R-88812", "doc_date": "2025-11-03", "amount": 1234.5, "currency": "USD"}),
    ("Net Pay: 45,000.00\nSalary slip for March 2025", {"doc_type": "payslip", "amount": 45000.0}),
])
def test_smartfields(text, expect):
    f = smartfields.extract(text)
    for k, v in expect.items():
        assert f[k] == v, (k, f)


def test_tables_from_layout_text():
    t = textract.find_tables("Item   Qty   Amount\nCement   10   4,500.00\nSteel   5   6,000.00\nSub Total   10,500.00\nThanks")
    assert t[0][0] == ["Item", "Qty", "Amount"] and t[0][3] == ["Sub Total", "", "10,500.00"]


def test_document_reader_and_tables(eng):
    a = eng.search(idx_req(filename_pattern="MCK_Invoice_1020.pdf"))["results"][0]
    d = eng.document(a["id"])
    assert d["source"] == "pdf" and "TAX INVOICE" in d["text"]
    assert d["tables"] and d["tables"][0][0][:2] == ["Item", "Qty"]


@pytest.mark.skipif(not OCR, reason="no OCR engine installed")
def test_ocr_scanned_pdf(eng):
    a = eng.search(idx_req(filename_pattern="IMG_2207.pdf"))["results"][0]
    d = eng.document(a["id"])
    assert d["source"] == "ocr" and d["fields"]["doc_no"] == "MK-5521" and d["fields"]["amount"] == 18750.0


# ------------------------------------------------------- export / share
def test_excel_export(eng):
    rows = eng.search(idx_req(senders=["mck kutty"], file_types=["pdf"], date_from=date(2025, 1, 1),
                              date_to=date(2025, 3, 31)))["results"]
    atts = [eng.get(r["id"]) for r in rows]
    wb = load_workbook(io.BytesIO(exporter.to_xlsx(atts)))
    assert wb.sheetnames == ["Attachments", "By month", "By sender", "About"]
    ws = wb["Attachments"]
    assert ws["A1"].value == "Email date" and ws.max_row == len(rows) + 2
    assert str(ws.cell(len(rows) + 2, 12).value).startswith("=SUBTOTAL(9,L2:")
    csv_text = exporter.to_csv(atts).decode("utf-8-sig")
    assert csv_text.count("\n") == len(rows) + 1
    assert "FILE    :" in exporter.text_bundle(atts).decode()


def test_eml_draft_and_share_helpers():
    raw = exporter.eml_draft([("a.pdf", b"%PDF-1", "application/pdf")], "x@y.in", "Hi", "Body")
    m = message_from_bytes(raw)
    assert m["X-Unsent"] == "1" and m["To"] == "x@y.in"
    assert [p.get_filename() for p in m.walk() if p.get_filename()] == ["a.pdf"]
    assert parse_recipients("A <a@x.in>; b@y.com, nonsense") == ["a@x.in", "b@y.com"]
    assert smtp_for("imap.gmail.com") == ("smtp.gmail.com", 465, "ssl")
    assert smtp_for("imap.example.org")[0] == "smtp.example.org"


def test_tables_xlsx_numbers():
    wb = load_workbook(io.BytesIO(exporter.tables_xlsx([[["Item", "Amount"], ["Rice", "2,400.00"]]])))
    assert wb["Table 1"]["B2"].value == 2400.0


# ------------------------------------------------------------------ web
@pytest.fixture()
def client(tmp_path):
    from mail_agent.web import create_app
    app = create_app({"download_dir": str(tmp_path), "workers": 2, "host": "", "user": "", "password": ""},
                     engine=SearchEngine(DemoSource(), workers=2), demo=True)
    return app.test_client()


def _wait(client, job):
    for _ in range(200):
        j = client.get(f"/api/jobs/{job['id']}").json
        if j["state"] != "running":
            return j
        time.sleep(0.05)
    raise AssertionError("job did not finish")


def test_web_index_jobs_export_share(client, tmp_path):
    assert client.get("/api/status").json["index"]["attachments"] > 50       # demo auto-indexed
    r = client.post("/api/search", json={"source": "index", "senders": "mck kutty", "doc_types": ["invoice"],
                                         "file_types": ["pdf"], "date_from": "2025-01-01", "date_to": "2025-06-30"})
    ids = [x["id"] for x in r.json["results"]]
    assert ids and r.json["stats"]["mode"] == "index"
    j = _wait(client, client.post("/api/analyze", json={"ids": ids, "ocr": False}).json)
    assert j["state"] == "done" and j["result"]["analyzed"] == len(ids)
    j = _wait(client, client.post("/api/index/sync", json={}).json)
    assert j["state"] == "done" and j["result"]["added"] == 0
    doc = client.get(f"/api/doc/{ids[0]}").json
    assert doc["fields"]["amount"] and doc["tables"]
    assert client.post("/api/fields", json={"id": ids[0], "field": "doc_no", "value": "X-1"}).json["fields"]["doc_no"] == "X-1"
    x = client.post("/api/export", json={"ids": ids, "format": "xlsx"})
    assert x.status_code == 200 and x.data[:2] == b"PK"
    saved = client.post("/api/export", json={"ids": ids, "format": "csv", "save_to": str(tmp_path / "list")}).json
    assert saved["path"].endswith("list.csv") and (tmp_path / "list.csv").exists()
    t = client.post(f"/api/tables/{ids[0]}", json={"format": "xlsx"})
    assert t.status_code == 200 and zipfile.is_zipfile(io.BytesIO(t.data))
    eml = client.post("/api/share/eml", json={"ids": ids[:2], "to": "a@b.in"})
    assert b"X-Unsent: 1" in eml.data
    sent = client.post("/api/share/email", json={"ids": ids[:1], "to": "a@b.in"}).json
    assert sent["demo"] is True                                          # demo never really sends
    assert client.post("/api/share/email", json={"ids": ids[:1], "to": "nope"}).status_code == 400
    assert "Total:" in client.post("/api/share/text", json={"ids": ids}).json["text"]
