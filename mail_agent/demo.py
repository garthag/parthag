"""In-memory demo mailbox (no login needed). Also used by the test-suite.

Contains real MIME messages with real PDFs so preview, PDF-text search,
de-duplication and downloading can all be tried end-to-end.
"""

from __future__ import annotations

import random
from datetime import datetime, timedelta, timezone
from email import message_from_bytes, policy
from email.message import EmailMessage
from email.utils import format_datetime, getaddresses, make_msgid, parsedate_to_datetime

from .hexagon import term_score
from .matrix import compact
from .mime_utils import to_snippet
from .models import AttachmentPart, MessageMeta, SearchCell

IST = timezone(timedelta(hours=5, minutes=30))


def make_pdf(lines: list[str]) -> bytes:
    """Minimal valid one-page PDF with text (no dependencies)."""
    def esc(s):
        return s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
    content = "BT /F1 14 Tf 60 780 Td 18 TL " + " ".join(f"({esc(l)}) '" for l in lines) + " ET"
    objs = [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R "
        "/Resources << /Font << /F1 5 0 R >> >> >>",
        f"<< /Length {len(content)} >>\nstream\n{content}\nendstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    out = bytearray(b"%PDF-1.4\n")
    offsets = []
    for i, o in enumerate(objs, 1):
        offsets.append(len(out))
        out += f"{i} 0 obj\n{o}\nendobj\n".encode("latin-1")
    xref = len(out)
    out += f"xref\n0 {len(objs) + 1}\n0000000000 65535 f \n".encode()
    for off in offsets:
        out += f"{off:010d} 00000 n \n".encode()
    out += f"trailer\n<< /Size {len(objs) + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
    return bytes(out)


# 1x1 PNG
PNG = bytes.fromhex("89504e470d0a1a0a0000000d4948445200000001000000010806000000"
                    "1f15c4890000000d49444154789c6360f8cf000000030101005fa1e6c90000000049454e44ae426082")


def _msg(frm, to, subject, when, body, attachments=(), inline_logo=False, msgid=None):
    m = EmailMessage()
    m["From"], m["To"], m["Subject"] = frm, to, subject
    m["Date"] = format_datetime(when)
    m["Message-ID"] = msgid or make_msgid(domain="demo.local")
    m.set_content(body)
    m.add_alternative(f"<html><body><p>{body}</p></body></html>", subtype="html")
    if inline_logo:
        m.get_payload()[1].add_related(PNG, "image", "png", cid="<logo>", filename="logo.png",
                                          disposition="inline")
    for name, data, maintype, subtype in attachments:
        m.add_attachment(data, maintype=maintype, subtype=subtype, filename=name)
    return m.as_bytes()


def build_mailbox(seed: int = 7) -> dict[str, list[bytes]]:
    rnd = random.Random(seed)
    inbox: list[bytes] = []
    invoices_folder: list[bytes] = []
    mck = "MCK Kutty Traders <accounts@mckkutty.in>"
    me = "you@example.com"

    # Monthly invoices from MCK Kutty, Oct 2023 .. Mar 2026
    d = datetime(2023, 10, 5, 10, 30, tzinfo=IST)
    n = 1001
    while d < datetime(2026, 4, 1, tzinfo=IST):
        amt = rnd.randint(8, 95) * 1000
        pdf = make_pdf([f"MCK KUTTY TRADERS - TAX INVOICE #{n}", f"Date: {d:%d %b %Y}",
                        f"Amount: INR {amt:,}", "GSTIN 32ABCDE1234F1Z5"])
        raw = _msg(mck, me, f"Invoice #{n} for {d:%B %Y}", d,
                   f"Dear customer, please find attached invoice {n} for {d:%B %Y}. Regards, MCK Kutty",
                   [(f"MCK_Invoice_{n}.pdf", pdf, "application", "pdf")], inline_logo=True)
        inbox.append(raw)
        if n % 4 == 0:
            invoices_folder.append(raw)            # same email filed in 2 folders -> dedupe
        if n % 7 == 0:                              # re-sent reminder with the same PDF -> dedupe
            inbox.append(_msg(mck, me, f"Reminder: payment due for invoice {n}", d + timedelta(days=10),
                              "Gentle reminder - invoice attached again.",
                              [(f"MCK_Invoice_{n}.pdf", pdf, "application", "pdf")]))
        n += 1
        d = (d.replace(day=1) + timedelta(days=32)).replace(day=rnd.randint(2, 9))

    # Non-invoice mails from MCK Kutty (should be filtered by keyword)
    inbox.append(_msg(mck, me, "Price list 2025", datetime(2025, 2, 3, tzinfo=IST), "Our new price list.",
                      [("PriceList_2025.pdf", make_pdf(["Price list 2025"]), "application", "pdf")]))
    # A bill whose subject/filename say nothing - only the PDF text says "invoice"
    inbox.append(_msg(mck, me, "Documents", datetime(2025, 6, 20, tzinfo=IST), "See attached.",
                      [("scan_0620.pdf", make_pdf(["TAX INVOICE", "MCK Kutty Traders", "INR 12,500"]),
                        "application", "pdf")]))
    # Typo in display name
    inbox.append(_msg("M.C.K Kuty <billing@mckkutty.in>", me, "Invoice 2024-Special",
                      datetime(2024, 8, 14, tzinfo=IST), "Special order invoice.",
                      [("Invoice_Special.pdf", make_pdf(["Invoice special order"]), "application", "pdf"),
                       ("delivery_photo.jpg", PNG, "image", "jpeg")]))
    # Other senders
    for i in range(25):
        when = datetime(2024, 1, 1, tzinfo=IST) + timedelta(days=rnd.randint(0, 700))
        who = rnd.choice(["Amazon <auto@amazon.in>", "KSEB <bills@kseb.in>", "HR <hr@company.com>",
                          "Ravi Kumar <ravi@gmail.com>"])
        inbox.append(_msg(who, me, rnd.choice(["Your invoice", "Monthly statement", "Photos", "Payslip"]), when,
                          "Attached.", [(f"doc_{i}.pdf", make_pdf([f"Document {i}", who]), "application", "pdf"),
                                        (f"sheet_{i}.xlsx", b"PK\x03\x04demo", "application",
                                         "vnd.openxmlformats-officedocument.spreadsheetml.sheet")]))
    return {"INBOX": inbox, "Invoices": invoices_folder}


class DemoSource:
    name = "demo"
    is_gmail = False

    def __init__(self):
        box = build_mailbox()
        self.folders = {f: {i + 1: message_from_bytes(raw, policy=policy.default) for i, raw in enumerate(msgs)}
                        for f, msgs in box.items()}

    def list_folders(self):
        return list(self.folders)

    def default_folders(self):
        return list(self.folders)

    def _date(self, m):
        return parsedate_to_datetime(str(m["Date"]))

    def search(self, folder, cell: SearchCell) -> list[int]:
        out = []
        for uid, m in self.folders.get(folder, {}).items():
            d = self._date(m).date()
            if cell.since and d < cell.since:
                continue
            if cell.before and d >= cell.before:
                continue
            frm = str(m["From"]).lower()
            if cell.sender and cell.sender.lower() not in frm and compact(cell.sender) not in compact(frm):
                # emulate server substring search (no fuzzy) - fuzzy happens client side
                if term_score(cell.sender, frm) < 0.6:
                    continue
            out.append(uid)
        return out

    def _parts(self, m):
        """Number leaf parts exactly like IMAP does."""
        def walk(p, prefix):
            if p.is_multipart():
                for i, c in enumerate(p.iter_parts(), 1):
                    yield from walk(c, f"{prefix}.{i}" if prefix else str(i))
            else:
                yield prefix or "1", p
        return list(walk(m, ""))

    def fetch_meta(self, folder, uids):
        out = []
        for uid in uids:
            m = self.folders[folder][uid]
            name, email = getaddresses([str(m["From"])])[0]
            meta = MessageMeta(folder=folder, uid=uid, message_id=str(m["Message-ID"]),
                               from_name=name or email, from_email=email.lower(),
                               subject=str(m["Subject"]), date=self._date(m))
            for part, p in self._parts(m):
                fn = p.get_filename()
                disp = p.get_content_disposition()
                if p.get_content_maintype() == "text" and not fn and disp != "attachment":
                    if not meta.snippet:
                        meta.snippet = to_snippet(p.get_payload(decode=True) or b"", p.get_content_charset() or "")
                    continue
                data = p.get_payload(decode=True) or b""
                meta.attachments.append(AttachmentPart(
                    part=part, filename=fn or f"attachment-{part}", mime=p.get_content_type(),
                    size=len(data), encoding="binary", inline=(disp == "inline" or (disp is None and
                                                               p.get_content_maintype() == "image"))))
            if meta.attachments:
                out.append(meta)
        return out

    def fetch_part(self, folder, uid, part, encoding):
        for num, p in self._parts(self.folders[folder][uid]):
            if num == part:
                return p.get_payload(decode=True) or b""
        raise KeyError(part)

    def close(self):
        pass
