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
from fnmatch import fnmatch
from functools import lru_cache

from .models import Attachment, SearchRequest

FACETS = ("sender", "subject", "body", "filename", "filetype", "date")
MATCH = 0.6          # a facet counts as "matched" at or above this
FUZZY = 0.84         # per-token similarity that counts as a typo-match
FUZZY_SCAN = 30000   # only the first 30k chars of long documents are tokenised

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


def term_score(term: str, text: str, fuzzy: bool = True) -> float:
    """How well `term` (one or more words) appears in `text`. 1.0 = exact."""
    if not term or not text:
        return 0.0
    t_low, x_low = term.lower().strip(), text.lower()
    if t_low in x_low:
        return 1.0
    t_c = re.sub(r"[^0-9a-z]", "", t_low)
    if t_c and len(t_c) >= 3 and t_c in re.sub(r"[^0-9a-z]", "", x_low[:FUZZY_SCAN]):
        return 1.0            # "mck kutty" vs "mckkutty@..." / "M.C.K. Kutty"
    want = tokens(term)
    if not want:
        return 0.0
    have = _token_set(x_low[:FUZZY_SCAN])
    if not have:
        return 0.0
    got = 0.0
    for w in want:
        if w in have:
            got += 1
            continue
        if not fuzzy or len(w) < 4:
            continue
        best_r = max((SequenceMatcher(None, w, h).ratio() for h in have
                      if abs(len(h) - len(w)) <= 2 and h[:1] == w[:1] or abs(len(h) - len(w)) <= 1 and h[-2:] == w[-2:]),
                     default=0)
        if best_r >= FUZZY:
            got += best_r      # typo tolerance: "kuty" ~ "kutty"
    return got / len(want)


@lru_cache(maxsize=4096)
def _token_set(text: str) -> frozenset:
    return frozenset(_with_initials(tokens(text)))


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


def best(terms: list[str], text: str, fuzzy: bool = True) -> float:
    return max((term_score(t, text, fuzzy) for t in terms), default=0.0)


def ext_of(att: Attachment) -> str:
    return att.ext or MIME_EXT.get(att.mime.lower(), "")


def score(att: Attachment, req: SearchRequest, types: list[str], pdf_text: str = "") -> tuple[float, dict, bool]:
    f: dict[str, float | None] = dict.fromkeys(FACETS)
    fz = req.fuzzy
    senders = [s for s in req.senders if s.strip()]
    kws = [k for k in req.keywords if k.strip()]
    if kws and req.match_mode == "phrase":
        kws, fz = [" ".join(kws)], False

    if senders:
        f["sender"] = best(senders, f"{att.from_name} {att.from_email}", fz)
    kw_ok = True
    if kws:
        body = att.snippet
        doc = pdf_text or att.text
        texts = {"subject": att.subject, "body": f"{body}\n{doc}" if doc else body, "filename": att.filename}
        per_kw = [{k: term_score(kw, t, fz) for k, t in texts.items()} for kw in kws]
        for k in texts:
            f[k] = max(p[k] for p in per_kw)
        if req.match_mode == "all":
            kw_ok = all(max(p.values()) >= MATCH for p in per_kw)
        else:
            kw_ok = max(f["subject"], f["body"], f["filename"]) >= MATCH
    if types:
        f["filetype"] = 1.0 if ext_of(att) in types else 0.0
    if req.date_from or req.date_to:
        d: date | None = att.date.date() if att.date else None
        ok = d is not None and (not req.date_from or d >= req.date_from) and (not req.date_to or d <= req.date_to)
        f["date"] = 1.0 if ok else 0.0

    gates = kw_ok
    if f["date"] is not None and f["date"] < 1:
        gates = False
    if f["filetype"] is not None and f["filetype"] < 1:
        gates = False
    if f["sender"] is not None and f["sender"] < MATCH:
        gates = False
    excl = [e for e in req.exclude_keywords if e.strip()]
    if excl and any(term_score(e, f"{att.subject} {att.filename}", False) >= 1 for e in excl):
        gates = False

    active = [v for k, v in f.items() if v is not None and k not in ("subject", "body", "filename")]
    if kws:
        # keyword facets are alternatives - the strongest one counts fully,
        # the others add a small bonus when they agree.
        kw = sorted([f["subject"], f["body"], f["filename"]], reverse=True)
        active.append(min(1.0, kw[0] + 0.1 * kw[1] + 0.05 * kw[2]))
    total = sum(active) / len(active) if active else 1.0
    return total, f, gates


def passes_filters(att: Attachment, req: SearchRequest) -> bool:
    """Exact (non-scored) filters: recipient, file-name pattern, size, amount, type, text state."""
    if req.recipients and best([r for r in req.recipients if r.strip()], att.to, req.fuzzy) < MATCH:
        return False
    pat = req.filename_pattern.strip().lower()
    if pat:
        name = att.filename.lower()
        if any(ch in pat for ch in "*?["):
            if not fnmatch(name, pat):
                return False
        elif pat not in name:
            return False
    if req.size_min and att.size < req.size_min:
        return False
    if req.size_max and att.size > req.size_max:
        return False
    amt = att.fields.get("amount")
    if req.amount_min is not None and (amt is None or amt < req.amount_min):
        return False
    if req.amount_max is not None and (amt is None or amt > req.amount_max):
        return False
    if req.doc_types and att.fields.get("doc_type", "other") not in req.doc_types:
        return False
    ts = req.text_state
    if ts == "has_text" and not (att.text_source in ("pdf", "office", "plain", "ocr") and att.text.strip()):
        return False
    if ts == "scanned" and att.text_source != "ocr":
        return False
    if ts == "not_analyzed" and att.text_source:
        return False
    return True
