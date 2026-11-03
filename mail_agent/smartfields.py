"""Smart fields: pull document type, number, date, amount, currency and GSTIN out of text.

Pure regex/heuristics - no AI service, no network, works offline and fast.
Tuned for Indian & international invoices ("Rs. 1,23,456.00", "INR 82,000",
"Grand Total: $1,234.50", "Invoice No: INV/24-25/0012", "Date: 09 Nov 2025").
"""

from __future__ import annotations

import re
from datetime import date, datetime

DOC_TYPES = {
    "credit_note": [r"credit\s*note"],
    "payslip": [r"pay\s*slip", r"salary\s*slip", r"payslip"],
    "quotation": [r"quotation", r"\bquote\b", r"estimate", r"pro[\s-]?forma"],
    "order": [r"purchase\s*order", r"order\s*confirmation", r"\bp\.?o\.?\s*(?:no|number|#)"],
    "receipt": [r"receipt", r"payment\s*(?:received|confirmation|successful)"],
    "statement": [r"statement", r"account\s*summary", r"e-?statement"],
    "invoice": [r"tax\s*invoice", r"invoice", r"\bbill\b", r"\binv\b"],
}
DOC_LABELS = {"invoice": "Invoice", "receipt": "Receipt", "statement": "Statement", "payslip": "Payslip",
              "quotation": "Quotation", "order": "Order", "credit_note": "Credit note", "other": "Other"}

GSTIN = re.compile(r"\b\d{2}[A-Z]{5}\d{4}[A-Z][1-9A-Z]Z[0-9A-Z]\b")

CUR = r"(?P<cur>₹|rs\.?|inr|\$|usd|us\$|€|eur|£|gbp|aed|sgd)"
NUM = r"(?P<num>\d{1,3}(?:,\d{2,3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)"
MONEY = re.compile(rf"{CUR}?\s*{NUM}\s*(?P<cur2>inr|usd|eur|gbp|/-)?", re.I)
AMOUNT_LABELS = [
    (r"grand\s*total", 10), (r"total\s*amount\s*(?:payable|due)?", 9), (r"amount\s*payable", 9),
    (r"net\s*(?:payable|amount|pay)", 9), (r"balance\s*due", 8), (r"amount\s*due", 8), (r"invoice\s*total", 8),
    (r"total\s*\(?(?:inr|rs|₹)?\)?", 6), (r"\bamount\b", 4), (r"\bpaid\b", 3), (r"\bsum\b", 2),
]
NO_LABEL = re.compile(
    r"(?:invoice|inv|bill|receipt|order|quotation|quote|credit\s*note|document|doc|ref(?:erence)?)\s*"
    r"(?:no\.?|number|num|#|id)?\s*[:#.\-]?\s*(?P<no>(?=[A-Z0-9\-/_.]*\d)[A-Z0-9][A-Z0-9\-/_.]{1,30})", re.I)
HASH_NO = re.compile(r"#\s*(?P<no>[A-Z0-9][A-Z0-9\-/_]{1,30})", re.I)

MONTHS = {m: i for i, m in enumerate(
    ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"], 1)}
DATE_PATTERNS = [
    (re.compile(r"\b(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})\b"), "ymd"),
    (re.compile(r"\b(\d{1,2})[-/.](\d{1,2})[-/.](\d{4})\b"), "dmy"),          # India / UK default
    (re.compile(r"\b(\d{1,2})[-\s.]*([A-Za-z]{3,9})[-\s.,]*(\d{4})\b"), "d_mon_y"),
    (re.compile(r"\b([A-Za-z]{3,9})[\s.]+(\d{1,2})(?:st|nd|rd|th)?,?\s+(\d{4})\b"), "mon_d_y"),
]
DATE_LABEL = re.compile(r"(?:invoice|bill|document|doc|receipt|statement|order)?\s*date[d]?\s*[:\-]?\s*", re.I)


def to_number(s: str) -> float | None:
    try:
        return float(s.replace(",", ""))
    except ValueError:
        return None


def _mk_date(y, m, d) -> date | None:
    try:
        dt = date(int(y), int(m), int(d))
    except (ValueError, TypeError):
        return None
    return dt if 1990 <= dt.year <= 2100 else None


def parse_dates(text: str) -> list[tuple[int, date]]:
    """Return (position, date) for every date-looking string."""
    out = []
    for rx, kind in DATE_PATTERNS:
        for m in rx.finditer(text):
            a, b, c = m.groups()
            if kind == "ymd":
                d = _mk_date(a, b, c)
            elif kind == "dmy":
                d = _mk_date(c, b, a) or _mk_date(c, a, b)        # fall back to m/d/y
            elif kind == "d_mon_y":
                mon = MONTHS.get(b[:3].lower())
                d = _mk_date(c, mon, a) if mon else None
            else:
                mon = MONTHS.get(a[:3].lower())
                d = _mk_date(c, mon, b) if mon else None
            if d:
                out.append((m.start(), d))
    return sorted(out)


def detect_type(text: str) -> str:
    low = text.lower()
    head = low[:400]
    best, best_score = "other", 0
    for t, pats in DOC_TYPES.items():
        s = 0
        for p in pats:
            s += 3 * len(re.findall(p, head)) + len(re.findall(p, low[:20000]))
        if s > best_score:
            best, best_score = t, s
    return best


def detect_number(text: str) -> str | None:
    for rx in (NO_LABEL, HASH_NO):
        for m in rx.finditer(text[:20000]):
            no = m.group("no").strip("._-/")
            if any(ch.isdigit() for ch in no) and not re.fullmatch(r"\d{1,2}", no) \
                    and not GSTIN.fullmatch(no) and not re.fullmatch(r"\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4}", no):
                return no
    return None


def detect_amount(text: str) -> tuple[float | None, str | None]:
    low = text[:40000]
    best: tuple[int, float, str | None] | None = None
    for lab, weight in AMOUNT_LABELS:
        for lm in re.finditer(lab, low, re.I):
            window = low[lm.end(): lm.end() + 60]
            for mm in MONEY.finditer(window):
                v = to_number(mm.group("num"))
                if v is None or v == 0:
                    continue
                if re.fullmatch(r"(19|20)\d\d", mm.group("num")) and not mm.group("cur"):
                    continue                          # a year, not money
                cur = _currency(mm.group("cur") or mm.group("cur2"))
                # earlier in window + stronger label wins; ties -> bigger (totals > line items)
                score = weight * 1000 - mm.start()
                if best is None or score > best[0] or (score == best[0] and v > best[1]):
                    best = (score, v, cur)
                break
    if best:
        return best[1], best[2]
    # no label: biggest amount that carries a currency marker
    tagged = [(to_number(m.group("num")), _currency(m.group("cur") or m.group("cur2")))
              for m in MONEY.finditer(low) if (m.group("cur") or (m.group("cur2") or "").strip("/-"))]
    tagged = [t for t in tagged if t[0]]
    if tagged:
        v, c = max(tagged, key=lambda t: t[0])
        return v, c
    return None, None


def _currency(tok: str | None) -> str | None:
    if not tok:
        return None
    t = tok.lower().strip(". ")
    return {"₹": "INR", "rs": "INR", "inr": "INR", "/-": "INR", "$": "USD", "usd": "USD", "us$": "USD",
            "€": "EUR", "eur": "EUR", "£": "GBP", "gbp": "GBP", "aed": "AED", "sgd": "SGD"}.get(t)


def detect_date(text: str) -> date | None:
    low = text[:20000]
    for m in DATE_LABEL.finditer(low):
        for pos, d in parse_dates(low[m.end(): m.end() + 40]):
            if pos < 6:
                return d
    found = parse_dates(low[:3000])
    return found[0][1] if found else None


def extract(text: str = "", subject: str = "", filename: str = "") -> dict:
    """Fields from a document's text (plus subject / file name as hints)."""
    name = re.sub(r"[_\-.]+", " ", filename.rsplit(".", 1)[0])
    head = f"{subject}\n{name}\n"
    full = head + (text or "")
    f: dict = {"doc_type": detect_type(full)}
    f["doc_no"] = detect_number(text or "") or detect_number(head)
    d = detect_date(text or "")
    f["doc_date"] = d.isoformat() if d else None
    amt, cur = detect_amount(text or "")
    if amt is None:
        amt, cur = detect_amount(head)
    f["amount"], f["currency"] = amt, cur
    g = GSTIN.search((text or "").upper())
    f["gstin"] = g.group(0) if g else None
    return f


def quick(subject: str, snippet: str, filename: str) -> dict:
    """Cheap first guess before the attachment itself has been read."""
    return extract(snippet, subject, filename)


def merge(base: dict, better: dict) -> dict:
    out = dict(base or {})
    for k, v in (better or {}).items():
        if v not in (None, "", "other") or k not in out:
            out[k] = v
    return out


def now_iso() -> str:
    return datetime.now().isoformat(timespec="seconds")
