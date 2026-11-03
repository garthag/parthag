"""Hexagon scoring - six independent checks per attachment.

            sender
       date ‹‹  ›› subject
     filetype ‹‹  ›› body / PDF text
           filename

Every facet is scored 0..1 (or None when the user didn't ask for it). Hard gates
decide whether an attachment is a match at all; the mean of the active facets
ranks the matches. The UI draws the six values as a hexagon so you can see at a
glance *why* something matched.
"""

from __future__ import annotations

import re
from datetime import date
from difflib import SequenceMatcher

from .models import Attachment, SearchRequest

FACETS = ("sender", "subject", "body", "filename", "filetype", "date")
MATCH = 0.6          # a facet counts as "matched" at or above this
FUZZY = 0.84         # per-token similarity that counts as a typo-match

MIME_EXT = {
    "application/pdf": "pdf",
    "image/jpeg": "jpg", "image/png": "png", "image/gif": "gif", "image/webp": "webp",
    "application/zip": "zip", "text/csv": "csv",
    "application/vnd.ms-excel": "xls",
    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet": "xlsx",
    "application/msword": "doc",
    "application/vnd.openxmlformats-officedocument.wordprocessingml.document": "docx",
}

_tok = re.compile(r"[0-9a-z]+")


def tokens(s: str) -> list[str]:
    return _tok.findall(s.lower())


def term_score(term: str, text: str) -> float:
    """How well `term` (one or more words) appears in `text`. 1.0 = exact."""
    if not term or not text:
        return 0.0
    t_low, x_low = term.lower().strip(), text.lower()
    if t_low in x_low:
        return 1.0
    t_c, x_c = re.sub(r"[^0-9a-z]", "", t_low), re.sub(r"[^0-9a-z]", "", x_low)
    if t_c and t_c in x_c:
        return 1.0            # "mck kutty" vs "mckkutty@..." / "M.C.K. Kutty"
    want, have = tokens(term), _with_initials(tokens(text))
    if not want or not have:
        return 0.0
    got = 0.0
    for w in want:
        if w in have:
            got += 1
            continue
        best = max((SequenceMatcher(None, w, h).ratio() for h in have if abs(len(h) - len(w)) <= 2), default=0)
        if best >= FUZZY:
            got += best        # typo tolerance: "kuty" ~ "kutty"
    return got / len(want)


def _with_initials(toks: list[str]) -> set[str]:
    """Add runs of single letters joined up: "m c k kutty" -> also "mck"."""
    out, run = set(toks), ""
    for t in toks + [""]:
        if len(t) == 1:
            run += t
        else:
            if len(run) > 1:
                out.add(run)
            run = ""
    return out


def best(terms: list[str], text: str) -> float:
    return max((term_score(t, text) for t in terms), default=0.0)


def ext_of(att: Attachment) -> str:
    return att.ext or MIME_EXT.get(att.mime.lower(), "")


def score(att: Attachment, req: SearchRequest, types: list[str], pdf_text: str = "") -> tuple[float, dict, bool]:
    f: dict[str, float | None] = dict.fromkeys(FACETS)
    senders = [s for s in req.senders if s.strip()]
    kws = [k for k in req.keywords if k.strip()]

    if senders:
        f["sender"] = best(senders, f"{att.from_name} {att.from_email}")
    if kws:
        f["subject"] = best(kws, att.subject)
        f["body"] = max(best(kws, att.snippet), best(kws, pdf_text) if pdf_text else 0.0)
        f["filename"] = best(kws, att.filename)
    if types:
        f["filetype"] = 1.0 if ext_of(att) in types else 0.0
    if req.date_from or req.date_to:
        d: date | None = att.date.date() if att.date else None
        ok = d is not None and (not req.date_from or d >= req.date_from) and (not req.date_to or d <= req.date_to)
        f["date"] = 1.0 if ok else 0.0

    gates = True
    if f["date"] is not None and f["date"] < 1:
        gates = False
    if f["filetype"] is not None and f["filetype"] < 1:
        gates = False
    if f["sender"] is not None and f["sender"] < MATCH:
        gates = False
    if kws and max(f["subject"], f["body"], f["filename"]) < MATCH:
        gates = False
    excl = [e for e in req.exclude_keywords if e.strip()]
    if excl and any(term_score(e, f"{att.subject} {att.filename}") >= 1 for e in excl):
        gates = False

    active = [v for k, v in f.items() if v is not None and k not in ("subject", "body", "filename")]
    if kws:
        # keyword facets are alternatives - the strongest one counts fully,
        # the others add a small bonus when they agree.
        kw = sorted([f["subject"], f["body"], f["filename"]], reverse=True)
        active.append(min(1.0, kw[0] + 0.1 * kw[1] + 0.05 * kw[2]))
    total = sum(active) / len(active) if active else 1.0
    return total, f, gates
