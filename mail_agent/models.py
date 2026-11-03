"""Plain data objects shared by every layer."""

from __future__ import annotations

import base64
from dataclasses import dataclass, field
from datetime import date, datetime

FIELD_NAMES = ("doc_type", "doc_no", "doc_date", "amount", "currency", "gstin")


@dataclass
class SearchRequest:
    senders: list[str] = field(default_factory=list)      # "mck kutty", "billing@acme.com"
    keywords: list[str] = field(default_factory=list)     # "invoice", "bill"
    date_from: date | None = None                         # inclusive
    date_to: date | None = None                           # inclusive
    file_types: list[str] = field(default_factory=list)   # "pdf", "xlsx" ... empty = any
    folders: list[str] = field(default_factory=list)      # empty = provider default
    exclude_keywords: list[str] = field(default_factory=list)
    match_pdf_text: bool = False      # also look for keywords *inside* PDFs (slower)
    hide_duplicates: bool = True
    include_inline: bool = False      # inline images = usually signature logos
    min_score: float = 0.0
    window_months: int = 3            # date-matrix row size
    # --- extra filters
    recipients: list[str] = field(default_factory=list)   # To / Cc
    match_mode: str = "any"           # any | all | phrase  (how keywords combine)
    fuzzy: bool = True                # typo tolerant matching
    filename_pattern: str = ""        # wildcard, e.g. "INV*2025*.pdf"
    size_min: int = 0                 # bytes
    size_max: int = 0                 # bytes, 0 = no limit
    amount_min: float | None = None   # uses auto-detected amount
    amount_max: float | None = None
    doc_types: list[str] = field(default_factory=list)    # invoice, receipt, statement ...
    text_state: str = "any"           # any | has_text | scanned | not_analyzed
    source: str = "live"              # live (mail server) | index (local database)


@dataclass
class SearchCell:
    """One cell of the query matrix: (sender x date-window) inside one folder."""
    sender: str | None
    keywords: list[str]
    file_types: list[str]
    since: date | None            # inclusive
    before: date | None           # exclusive
    recipients: list[str] = field(default_factory=list)


@dataclass
class AttachmentPart:
    part: str            # IMAP body section, e.g. "2" or "1.3"
    filename: str
    mime: str
    size: int
    encoding: str
    charset: str = ""
    inline: bool = False


@dataclass
class MessageMeta:
    folder: str
    uid: int
    message_id: str
    from_name: str
    from_email: str
    subject: str
    date: datetime | None
    snippet: str = ""
    to: str = ""
    attachments: list[AttachmentPart] = field(default_factory=list)


@dataclass
class Attachment:
    id: str
    folder: str
    uid: int
    part: str
    filename: str
    mime: str
    size: int
    encoding: str
    inline: bool
    message_id: str
    from_name: str
    from_email: str
    subject: str
    date: datetime | None
    snippet: str
    to: str = ""
    score: float = 0.0
    facets: dict = field(default_factory=dict)
    duplicates: int = 0          # how many identical copies were folded into this one
    fields: dict = field(default_factory=dict)   # smart fields: doc_type, doc_no, amount ...
    text: str = ""               # extracted / OCR text (from the index)
    text_source: str = ""        # "", pdf, ocr, office, plain, none

    @property
    def ext(self) -> str:
        return self.filename.rsplit(".", 1)[-1].lower() if "." in self.filename else ""

    def to_json(self) -> dict:
        return {
            "id": self.id,
            "folder": self.folder,
            "filename": self.filename,
            "mime": self.mime,
            "ext": self.ext,
            "size": self.size,
            "inline": self.inline,
            "from_name": self.from_name,
            "from_email": self.from_email,
            "to": self.to,
            "subject": self.subject,
            "date": self.date.isoformat() if self.date else None,
            "snippet": self.snippet[:300],
            "score": round(self.score, 3),
            "facets": {k: (None if v is None else round(v, 3)) for k, v in self.facets.items()},
            "duplicates": self.duplicates,
            "fields": {k: self.fields.get(k) for k in FIELD_NAMES},
            "text_source": self.text_source,
            "risky": self.ext in _risky(),
        }


def _risky():
    from .downloader import RISKY_EXTS
    return RISKY_EXTS


def make_attachment_id(folder: str, uid: int, part: str) -> str:
    raw = f"{folder}\x00{uid}\x00{part}".encode()
    return base64.urlsafe_b64encode(raw).decode().rstrip("=")
