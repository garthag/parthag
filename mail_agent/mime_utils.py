"""MIME helpers: BODYSTRUCTURE walking, filename decoding, transfer decoding."""

from __future__ import annotations

import base64
import binascii
import html
import quopri
import re
from email.header import decode_header, make_header
from urllib.parse import unquote_to_bytes

from .models import AttachmentPart

GUESS_EXT = {
    "application/pdf": ".pdf", "image/jpeg": ".jpg", "image/png": ".png", "image/gif": ".gif",
    "application/zip": ".zip", "text/csv": ".csv", "message/rfc822": ".eml",
    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet": ".xlsx",
    "application/vnd.openxmlformats-officedocument.wordprocessingml.document": ".docx",
}


def _s(v) -> str:
    if v is None:
        return ""
    if isinstance(v, bytes):
        return v.decode("utf-8", "replace")
    return str(v)


def decode_words(value: str) -> str:
    """Decode RFC 2047 encoded words (=?utf-8?B?...?=)."""
    if not value:
        return ""
    try:
        return str(make_header(decode_header(value)))
    except Exception:
        return value


def _params(raw) -> dict[str, str]:
    out: dict[str, str] = {}
    if isinstance(raw, (list, tuple)):
        items = list(raw)
        for i in range(0, len(items) - 1, 2):
            out[_s(items[i]).lower()] = _s(items[i + 1])
    return out


def _rfc2231(value: str) -> str:
    """charset'lang'percent-encoded  ->  text"""
    charset, value = "utf-8", value.strip('"')
    if value.count("'") >= 2:
        charset, _lang, value = value.split("'", 2)
    try:
        return unquote_to_bytes(value).decode(charset or "utf-8", "replace")
    except LookupError:
        return unquote_to_bytes(value).decode("utf-8", "replace")


def param_filename(params: dict[str, str], key: str) -> str:
    """Read `key` from MIME params, handling RFC 2231 (key*, key*0*, key*1...) and RFC 2047."""
    if key in params:
        return decode_words(params[key])
    if key + "*" in params:
        return _rfc2231(params[key + "*"])
    pieces = []
    n = 0
    while True:
        if f"{key}*{n}*" in params:
            pieces.append((params[f"{key}*{n}*"], True))
        elif f"{key}*{n}" in params:
            pieces.append((params[f"{key}*{n}"], False))
        else:
            break
        n += 1
    if not pieces:
        return ""
    if any(enc for _, enc in pieces):
        return _rfc2231("".join(p for p, _ in pieces))
    return decode_words("".join(p for p, _ in pieces))


def _disposition(body, mtype: str):
    """Locate the disposition in a single-part BODYSTRUCTURE."""
    idx = {"text": 9, "message": 11}.get(mtype, 8)
    if mtype == "message" and len(body) > 8 and not isinstance(body[7], (list, tuple)):
        idx = 8  # message/* that isn't rfc822 has no envelope
    if len(body) > idx and isinstance(body[idx], (list, tuple)) and body[idx]:
        d = body[idx]
        return _s(d[0]).lower(), _params(d[1] if len(d) > 1 else None)
    return "", {}


def walk_bodystructure(bs, prefix: str = ""):
    """Yield (part_number, single_part_body) for every leaf part."""
    is_multi = getattr(bs, "is_multipart", None)
    if is_multi is None:
        is_multi = bool(bs) and isinstance(bs[0], list)
    if is_multi:
        for i, child in enumerate(bs[0], 1):
            yield from walk_bodystructure(child, f"{prefix}.{i}" if prefix else str(i))
    else:
        yield (prefix or "1"), bs


def parts_from_bodystructure(bs) -> tuple[list[AttachmentPart], tuple[str, str, str] | None]:
    """Return (attachments, first_text_part) where first_text_part=(part, encoding, charset)."""
    atts: list[AttachmentPart] = []
    text_part = None
    html_part = None
    for part, body in walk_bodystructure(bs):
        try:
            mtype, sub = _s(body[0]).lower(), _s(body[1]).lower()
            ctype_params = _params(body[2])
            encoding = _s(body[5]).lower() if len(body) > 5 else "7bit"
            size = int(body[6]) if len(body) > 6 and body[6] is not None else 0
        except (IndexError, TypeError, ValueError):
            continue
        disp, disp_params = _disposition(body, mtype)
        mime = f"{mtype}/{sub}"
        name = param_filename(disp_params, "filename") or param_filename(ctype_params, "name")

        is_body_text = mtype == "text" and sub in ("plain", "html") and disp != "attachment" and not name
        if is_body_text:
            ref = (part, encoding, ctype_params.get("charset", ""))
            if sub == "plain" and text_part is None:
                text_part = ref
            elif sub == "html" and html_part is None:
                html_part = ref
            continue
        if mtype == "multipart":
            continue
        if not name:
            name = f"attachment-{part}{GUESS_EXT.get(mime, '')}"
        atts.append(AttachmentPart(
            part=part, filename=name.strip().replace("\r", "").replace("\n", ""), mime=mime,
            size=_estimate_size(size, encoding), encoding=encoding,
            charset=ctype_params.get("charset", ""),
            inline=(disp == "inline" or (disp == "" and mtype == "image")),
        ))
    return atts, text_part or html_part


def _estimate_size(transfer_size: int, encoding: str) -> int:
    return int(transfer_size * 0.74) if encoding == "base64" else transfer_size


def decode_transfer(data: bytes, encoding: str, partial: bool = False) -> bytes:
    enc = (encoding or "").lower()
    try:
        if enc == "base64":
            data = re.sub(rb"[^A-Za-z0-9+/=]", b"", data)
            if partial:
                data = data[: len(data) - len(data) % 4]
            return base64.b64decode(data + b"=" * (-len(data) % 4))
        if enc == "quoted-printable":
            return quopri.decodestring(data)
    except (binascii.Error, ValueError):
        pass
    return data


_TAG = re.compile(r"<(script|style)[^>]*>.*?</\1>|<[^>]+>", re.S | re.I)


def to_snippet(data: bytes, charset: str, limit: int = 1500) -> str:
    try:
        text = data.decode(charset or "utf-8", "replace")
    except LookupError:
        text = data.decode("utf-8", "replace")
    if "<" in text and ">" in text:
        text = html.unescape(_TAG.sub(" ", text))
    return re.sub(r"\s+", " ", text).strip()[:limit]
