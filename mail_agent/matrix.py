"""Query matrix ("square mapping").

A search is expanded into a grid:

        rows    = date windows  (e.g. 2024-Q1, 2024-Q2 ... 2025-Q4)
        columns = senders       ("mck kutty", "acme billing", ...)
        layers  = folders       (INBOX, Invoices, ...)

Every cell is a small, cheap server-side search that runs in parallel on its own
IMAP connection. Cells overlap a little on purpose (one day of margin on each
side, to absorb timezone differences), and the engine merges the results as a
set, so nothing is counted twice.
"""

from __future__ import annotations

import re
from datetime import date, timedelta

from .models import SearchCell, SearchRequest

MARGIN = timedelta(days=1)

# File-type aliases users can type instead of extensions
TYPE_GROUPS = {
    "image": ["jpg", "jpeg", "png", "gif", "webp", "heic", "bmp", "tif", "tiff"],
    "images": ["jpg", "jpeg", "png", "gif", "webp", "heic", "bmp", "tif", "tiff"],
    "excel": ["xls", "xlsx", "xlsm", "csv", "ods"],
    "word": ["doc", "docx", "odt", "rtf"],
    "office": ["doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods"],
    "archive": ["zip", "rar", "7z", "gz", "tar"],
    "zip": ["zip"],
    "pdf": ["pdf"],
}


def normalise_types(types: list[str]) -> list[str]:
    out: list[str] = []
    for t in types:
        t = t.strip().lower().lstrip(".*")
        if not t or t in ("any", "all"):
            continue
        for e in TYPE_GROUPS.get(t, [t]):
            if e not in out:
                out.append(e)
    return out


def _add_months(d: date, months: int) -> date:
    m = d.month - 1 + months
    y = d.year + m // 12
    return date(y, m % 12 + 1, 1)


def date_windows(start: date | None, end: date | None, months: int = 3) -> list[tuple[date | None, date | None]]:
    """Split [start, end] (inclusive) into windows of (since, before_exclusive).

    Each window gets MARGIN of overlap on both sides; exact filtering is done
    client-side, so the overlap never produces extra results, only safety.
    """
    if start is None and end is None:
        return [(None, None)]
    if start is None or end is None or months <= 0:
        return [(start - MARGIN if start else None, end + MARGIN + timedelta(days=1) if end else None)]
    if end < start:
        start, end = end, start
    out = []
    cur = start
    while cur <= end:
        nxt = min(_add_months(date(cur.year, cur.month, 1), months), end + timedelta(days=1))
        out.append((cur - MARGIN, nxt + MARGIN))
        cur = nxt
    return out


def build_cells(req: SearchRequest) -> list[SearchCell]:
    senders = _dedupe([s.strip() for s in req.senders if s.strip()]) or [None]
    keywords = _dedupe([k.strip() for k in req.keywords if k.strip()])
    types = normalise_types(req.file_types)
    cells = []
    for since, before in date_windows(req.date_from, req.date_to, req.window_months):
        for sender in senders:
            cells.append(SearchCell(sender=sender, keywords=keywords, file_types=types,
                                    since=since, before=before))
    return cells


def _dedupe(items):
    seen, out = set(), []
    for i in items:
        k = i.lower()
        if k not in seen:
            seen.add(k)
            out.append(i)
    return out


def compact(s: str) -> str:
    return re.sub(r"[^0-9a-z@.]+", "", s.lower())


# --------------------------------------------------------------------------- IMAP

def _or(items: list) -> list:
    """Fold items into nested IMAP OR: OR a (OR b c)."""
    if len(items) == 1:
        return items[0]
    return ["OR", items[0], _or(items[1:])]


def imap_criteria(cell: SearchCell) -> list:
    """Standard RFC 3501 SEARCH criteria for any IMAP server."""
    crit: list = []
    if cell.since:
        crit += ["SINCE", cell.since]
    if cell.before:
        crit += ["BEFORE", cell.before]
    if cell.sender:
        variants = [cell.sender]
        c = compact(cell.sender)
        if c and c != cell.sender.lower():
            variants.append(c)            # "mck kutty" also finds mckkutty@...
        crit.append(_or([["FROM", v] for v in variants]))
    elif cell.keywords:
        # No sender: narrow by keyword server-side. BODY also scans MIME part
        # headers on most servers, which contain the attachment file name.
        alts = []
        for k in cell.keywords:
            alts += [["SUBJECT", k], ["BODY", k]]
        crit.append(_or(alts))
    if not crit:
        crit = ["ALL"]
    return crit


# -------------------------------------------------------------------------- Gmail

def _gq(term: str) -> str:
    term = term.replace('"', "")
    return f'"{term}"' if re.search(r"\s", term) else term


def gmail_raw(cell: SearchCell) -> str:
    """Gmail X-GM-RAW query - searches subject, body AND attachment names server-side."""
    q = ["has:attachment"]
    if cell.sender:
        alts = [f"from:{_gq(cell.sender)}"]
        c = compact(cell.sender)
        if c and c != cell.sender.lower():
            alts.append(f"from:{c}")
        q.append("{" + " ".join(alts) + "}" if len(alts) > 1 else alts[0])
    if cell.keywords:
        alts = []
        for k in cell.keywords:
            alts += [_gq(k), f"filename:{_gq(k)}"]
        q.append("{" + " ".join(alts) + "}")
    if cell.file_types:
        q.append("{" + " ".join(f"filename:{t}" for t in cell.file_types) + "}")
    if cell.since:
        q.append(f"after:{cell.since:%Y/%m/%d}")
    if cell.before:
        q.append(f"before:{cell.before:%Y/%m/%d}")
    return " ".join(q)
