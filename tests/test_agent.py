import json
import zipfile
from datetime import date
from io import BytesIO

import pytest
from imapclient.response_types import BodyData

from mail_agent import hexagon
from mail_agent.demo import DemoSource
from mail_agent.downloader import INDEX, MANIFEST, build_zip, save_all, target_path
from mail_agent.engine import SearchEngine
from mail_agent.matrix import build_cells, date_windows, gmail_raw, imap_criteria, normalise_types
from mail_agent.mime_utils import decode_transfer, param_filename, parts_from_bodystructure
from mail_agent.models import Attachment, SearchCell, SearchRequest

MCK = dict(senders=["mck kutty"], keywords=["invoice"], date_from=date(2024, 1, 1),
           date_to=date(2025, 12, 25), file_types=["pdf"])


@pytest.fixture(scope="module")
def engine():
    return SearchEngine(DemoSource(), workers=4)


# ------------------------------------------------------------------ matrix
def test_date_windows_cover_range_without_gaps():
    w = date_windows(date(2024, 1, 1), date(2025, 12, 25), 3)
    assert len(w) == 8
    assert w[0][0] <= date(2024, 1, 1) and w[-1][1] > date(2025, 12, 25)
    for (_, b1), (a2, _) in zip(w, w[1:]):
        assert a2 < b1            # overlapping -> nothing can fall between cells


def test_cells_are_senders_times_windows_and_deduped():
    req = SearchRequest(senders=["mck kutty", "MCK Kutty", "acme"], date_from=date(2024, 1, 1),
                        date_to=date(2024, 12, 31), window_months=6)
    assert len(build_cells(req)) == 2 * 2


def test_gmail_query():
    q = gmail_raw(SearchCell("mck kutty", ["invoice"], ["pdf"], date(2024, 1, 1), date(2025, 12, 26)))
    assert '{from:"mck kutty" from:mckkutty}' in q
    assert "{invoice filename:invoice}" in q and "filename:pdf" in q
    assert "after:2024/01/01" in q and "before:2025/12/26" in q


def test_imap_criteria():
    c = imap_criteria(SearchCell("mck kutty", [], [], date(2024, 1, 1), date(2024, 4, 1)))
    assert c[:4] == ["SINCE", date(2024, 1, 1), "BEFORE", date(2024, 4, 1)]
    assert c[4] == ["OR", ["FROM", "mck kutty"], ["FROM", "mckkutty"]]


def test_type_groups():
    assert normalise_types(["PDF", ".xlsx", "image"])[:2] == ["pdf", "xlsx"]
    assert "png" in normalise_types(["image"])


# ----------------------------------------------------------------- hexagon
@pytest.mark.parametrize("term,text,ok", [
    ("mck kutty", "MCK Kutty Traders accounts@mckkutty.in", True),
    ("mck kutty", "M.C.K Kuty billing@x.in", True),        # punctuation + typo
    ("mck kutty", "Ravi Kumar ravi@gmail.com", False),
    ("invoice", "TAX INVOICE #1001", True),
])
def test_term_score(term, text, ok):
    assert (hexagon.term_score(term, text) >= hexagon.MATCH) is ok


# -------------------------------------------------------------- MIME / BS
def test_bodystructure_parsing_real_shape():
    # multipart/mixed( multipart/alternative(text/plain, text/html), application/pdf, image inline )
    raw = (
        [
            ([(b"text", b"plain", (b"charset", b"utf-8"), None, None, b"quoted-printable", 120, 4, None, None, None, None),
              (b"text", b"html", (b"charset", b"utf-8"), None, None, b"base64", 300, 5, None, None, None, None)],
             b"alternative", (b"boundary", b"x"), None, None, None),
            (b"application", b"pdf", (b"name", b"=?utf-8?B?SW52b2ljZV/gpIUucGRm?="), None, None, b"base64", 4000, None,
             (b"attachment", (b"filename*", b"utf-8''Invoice_%E0%A4%85.pdf")), None, None),
            (b"image", b"png", None, b"<logo>", None, b"base64", 400, None, (b"inline", (b"filename", b"logo.png")), None, None),
        ],
        b"mixed", (b"boundary", b"y"), None, None, None,
    )
    bs = BodyData.create(raw)
    atts, text = parts_from_bodystructure(bs)
    assert text == ("1.1", "quoted-printable", "utf-8")
    assert [a.part for a in atts] == ["2", "3"]
    assert atts[0].filename == "Invoice_अ.pdf" and atts[0].mime == "application/pdf" and not atts[0].inline
    assert atts[1].inline


def test_single_part_pdf_message():
    bs = BodyData.create((b"application", b"pdf", None, None, None, b"base64", 900, None, None, None, None))
    atts, text = parts_from_bodystructure(bs)
    assert text is None and atts[0].part == "1" and atts[0].filename.endswith(".pdf")


def test_rfc2231_continuations():
    p = {"filename*0*": "utf-8''Rechnung%20", "filename*1*": "M%C3%A4rz.pdf"}
    assert param_filename(p, "filename") == "Rechnung März.pdf"


def test_partial_base64():
    assert decode_transfer(b"aGVsbG8gd29y", "base64", partial=True) == b"hello wor"


# ------------------------------------------------------------------ engine
def test_mck_kutty_invoices_exact():
    engine = SearchEngine(DemoSource(), workers=4)
    res = engine.search(SearchRequest(**MCK))
    names = [r["filename"] for r in res["results"]]
    # 24 monthly invoices Jan-2024..Dec-2025 + "M.C.K Kuty" special invoice
    assert len(names) == 25
    assert len(set(names)) == len(names), "no duplicates in results"
    assert "Invoice_Special.pdf" in names
    assert "PriceList_2025.pdf" not in names          # keyword filter
    assert "delivery_photo.jpg" not in names          # type filter
    assert not any(n.startswith("doc_") for n in names)   # other senders
    assert all("2024-01-01" <= r["date"][:10] <= "2025-12-25" for r in res["results"])
    s = res["stats"]
    assert s["duplicate_messages"] > 0 and s["duplicate_attachments"] > 0


def test_deep_pdf_text_finds_hidden_invoice():
    from mail_agent import textract
    eng = SearchEngine(DemoSource(), workers=4)
    res = eng.search(SearchRequest(**MCK, match_pdf_text=True))
    names = [r["filename"] for r in res["results"]]
    assert "scan_0620.pdf" in names                       # found via PDF text
    if textract.ocr_engine():
        assert "IMG_2207.pdf" in names and len(names) == 27   # image-only PDF found via OCR
    else:
        assert len(names) == 26


def test_inline_logos_skipped_by_default(engine):
    res = engine.search(SearchRequest(senders=["mck kutty"], date_from=date(2025, 1, 1), date_to=date(2025, 1, 31)))
    assert all(r["filename"] != "logo.png" for r in res["results"])
    res = engine.search(SearchRequest(senders=["mck kutty"], date_from=date(2025, 1, 1), date_to=date(2025, 1, 31),
                                      include_inline=True))
    assert any(r["filename"] == "logo.png" for r in res["results"])


# -------------------------------------------------------------- downloader
def test_save_organises_and_never_duplicates(tmp_path):
    engine = SearchEngine(DemoSource(), workers=4)
    res = engine.search(SearchRequest(**MCK))
    ids = [r["id"] for r in res["results"]][:5]
    rep = save_all(engine, ids, str(tmp_path))
    assert len(rep["saved"]) == 5 and not rep["failed"]
    first = tmp_path / rep["saved"][0]["path"]
    assert first.read_bytes().startswith(b"%PDF")
    assert first.parent.parent.name == "MCK Kutty Traders"
    rep2 = save_all(engine, ids, str(tmp_path))         # again -> all skipped
    assert len(rep2["saved"]) == 0 and len(rep2["skipped"]) == 5
    assert (tmp_path / MANIFEST).read_text().count("\n") == 6
    assert len(json.loads((tmp_path / INDEX).read_text())) == 5


def test_zip():
    engine = SearchEngine(DemoSource(), workers=4)
    res = engine.search(SearchRequest(**MCK))
    data, rep = build_zip(engine, [r["id"] for r in res["results"]])
    assert rep["added"] == 25
    assert len(zipfile.ZipFile(BytesIO(data)).namelist()) == 25


def test_path_traversal_blocked(tmp_path):
    a = Attachment(id="x", folder="I", uid=1, part="2", filename="../../evil.pdf", mime="application/pdf",
                   size=1, encoding="base64", inline=False, message_id="", from_name="../../..",
                   from_email="", subject="", date=None, snippet="")
    p = target_path(tmp_path, a, "{sender}/{subject}", "{filename}")
    assert tmp_path.resolve() in p.resolve().parents


# --------------------------------------------------------------------- web
@pytest.fixture()
def client(tmp_path):
    from mail_agent.web import create_app
    app = create_app({"download_dir": str(tmp_path), "workers": 2, "host": "", "user": "", "password": ""},
                     engine=SearchEngine(DemoSource(), workers=2), demo=True)
    return app.test_client()


def test_web_flow(client, tmp_path):
    assert client.get("/api/status").json["connected"]
    r = client.post("/api/search", json={"senders": "mck kutty", "keywords": "invoice", "date_from": "2024-01-01",
                                         "date_to": "2025-12-25", "file_types": ["pdf"]})
    assert r.status_code == 200 and len(r.json["results"]) == 25
    first = r.json["results"][0]["id"]
    pv = client.get(f"/api/preview/{first}")
    assert pv.status_code == 200 and pv.mimetype == "application/pdf" and pv.data.startswith(b"%PDF")
    d = client.post("/api/download", json={"ids": [first], "dest": str(tmp_path / "out")})
    assert len(d.json["saved"]) == 1
    z = client.post("/api/zip", json={"ids": [first]})
    assert z.mimetype == "application/zip"


def test_web_rejects_foreign_host_and_form_posts(client):
    assert client.get("/api/status", headers={"Host": "evil.example"}).status_code == 403
    assert client.post("/api/download", data="ids=x").status_code == 415
