"""IMAPSource against a fake IMAPClient that returns imapclient-shaped responses."""
import base64
from datetime import date, datetime

from imapclient.response_types import BodyData

import mail_agent.imap_source as mod
from mail_agent.engine import SearchEngine
from mail_agent.models import SearchRequest

PDF = b"%PDF-1.4 fake invoice"
BS = BodyData.create((
    [(b"text", b"plain", (b"charset", b"utf-8"), None, None, b"7bit", 40, 1, None, None, None, None),
     (b"application", b"pdf", (b"name", b"Invoice_7.pdf"), None, None, b"base64", 40, None,
      (b"attachment", (b"filename", b"Invoice_7.pdf")), None, None)],
    b"mixed", (b"boundary", b"b"), None, None, None))
HDR = (b"From: MCK Kutty <accounts@mckkutty.in>\r\nSubject: Invoice 7\r\n"
       b"Date: Tue, 04 Mar 2025 10:00:00 +0530\r\nMessage-ID: <m7@x>\r\n\r\n")


class FakeClient:
    instances = 0

    def __init__(self, host, port, ssl, ssl_context, timeout):
        FakeClient.instances += 1
        self.gm = host == "imap.gmail.com"
        self.queries = []

    def login(self, u, p): pass
    def logout(self): pass
    def capabilities(self): return (b"IMAP4REV1", b"X-GM-EXT-1") if self.gm else (b"IMAP4REV1",)
    def select_folder(self, f, readonly): assert readonly is True
    def find_special_folder(self, _): return "[Gmail]/All Mail"

    def gmail_search(self, q):
        self.queries.append(q)
        return [7]

    def search(self, crit, charset=None):
        self.queries.append(crit)
        return [7]

    def fetch(self, uids, items):
        assert all("PEEK" in i or i in ("BODYSTRUCTURE", "INTERNALDATE") for i in items), "must never mark as read"
        if "BODYSTRUCTURE" in items:
            return {7: {b"BODYSTRUCTURE": BS, b"INTERNALDATE": datetime(2025, 3, 4),
                        b"BODY[HEADER.FIELDS (FROM SUBJECT DATE MESSAGE-ID)]": HDR, b"SEQ": 1}}
        if items[0].startswith("BODY.PEEK[1]<"):
            return {7: {b"BODY[1]<0>": b"Please find the invoice attached"}}
        if items[0] == "BODY.PEEK[2]":
            return {7: {b"BODY[2]": base64.encodebytes(PDF)}}
        raise AssertionError(items)


def _run(monkeypatch, host):
    FakeClient.instances = 0
    monkeypatch.setattr(mod, "IMAPClient", FakeClient)
    src = mod.IMAPSource(host, "u", "p", pool_size=3)
    eng = SearchEngine(src, workers=3)
    res = eng.search(SearchRequest(senders=["mck kutty"], keywords=["invoice"], file_types=["pdf"],
                                   date_from=date(2025, 1, 1), date_to=date(2025, 6, 30)))
    return src, eng, res


def test_generic_imap(monkeypatch):
    src, eng, res = _run(monkeypatch, "imap.example.com")
    assert not src.is_gmail
    assert len(res["results"]) == 1          # 2 windows x same UID -> one result
    r = res["results"][0]
    assert r["filename"] == "Invoice_7.pdf" and r["from_email"] == "accounts@mckkutty.in"
    assert "invoice attached" in r["snippet"]
    assert eng.fetch_bytes(r["id"]) == PDF
    assert FakeClient.instances <= 3         # pool never exceeds its size


def test_gmail_uses_raw_search(monkeypatch):
    src, eng, res = _run(monkeypatch, "imap.gmail.com")
    assert src.is_gmail and res["stats"]["folders"] == ["[Gmail]/All Mail"]
    assert len(res["results"]) == 1
