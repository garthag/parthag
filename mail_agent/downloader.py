"""Save attachments into an organised folder tree, never storing the same file twice."""

from __future__ import annotations

import csv
import hashlib
import io
import json
import os
import re
import threading
import zipfile
from datetime import datetime
from pathlib import Path

from .models import Attachment

INDEX = ".mail_agent_index.json"
MANIFEST = "manifest.csv"

FOLDER_TEMPLATES = {
    "sender/year-month": "{sender}/{year}-{month}",
    "sender/year": "{sender}/{year}",
    "year/month": "{year}/{month}",
    "sender": "{sender}",
    "flat": "",
}
NAME_TEMPLATES = {
    "original": "{filename}",
    "date_original": "{date}_{filename}",
    "date_sender_original": "{date}_{sender}_{filename}",
}

_bad = re.compile(r'[<>:"/\\|?*\x00-\x1f]+')
_lock = threading.Lock()


def safe(segment: str, fallback: str = "unknown", limit: int = 120) -> str:
    s = _bad.sub("_", segment or "").strip(" .")
    s = re.sub(r"\s+", " ", s)
    if s in ("", ".", ".."):
        s = fallback
    if len(s) > limit:
        stem, dot, ext = s.rpartition(".")
        s = (stem[: limit - len(ext) - 1] + "." + ext) if dot and len(ext) <= 8 else s[:limit]
    return s


def _fields(a: Attachment) -> dict:
    d = a.date or datetime(1970, 1, 1)
    return {
        "sender": a.from_name or a.from_email or "unknown",
        "sender_email": a.from_email or "unknown",
        "year": f"{d:%Y}", "month": f"{d:%m}", "day": f"{d:%d}", "date": f"{d:%Y-%m-%d}",
        "subject": a.subject or "no subject", "filename": a.filename, "ext": a.ext,
    }


def target_path(root: Path, a: Attachment, folder_tpl: str, name_tpl: str) -> Path:
    f = _fields(a)
    folder_tpl = FOLDER_TEMPLATES.get(folder_tpl, folder_tpl)
    name_tpl = NAME_TEMPLATES.get(name_tpl, name_tpl)
    try:
        rel_dir = folder_tpl.format(**f) if folder_tpl else ""
        name = name_tpl.format(**f) if name_tpl else a.filename
    except (KeyError, IndexError, ValueError):
        rel_dir, name = "", a.filename
    parts = [safe(p) for p in rel_dir.replace("\\", "/").split("/") if p.strip()]
    path = root.joinpath(*parts, safe(name, "attachment"))
    # never escape the chosen folder
    if root.resolve() not in path.resolve().parents:
        path = root / safe(a.filename, "attachment")
    return path


def _unique(path: Path) -> Path:
    if not path.exists():
        return path
    stem, suf = path.stem, path.suffix
    n = 2
    while True:
        p = path.with_name(f"{stem} ({n}){suf}")
        if not p.exists():
            return p
        n += 1


def _load_index(root: Path) -> dict:
    try:
        return json.loads((root / INDEX).read_text("utf-8"))
    except (OSError, ValueError):
        return {}


def save_all(engine, ids: list[str], dest: str, folder_tpl: str = "sender/year-month",
             name_tpl: str = "original") -> dict:
    root = Path(os.path.expanduser(dest or "~/MailAttachments")).resolve()
    root.mkdir(parents=True, exist_ok=True)
    report = {"folder": str(root), "saved": [], "skipped": [], "failed": []}
    with _lock:
        index = _load_index(root)
        rows = []
        for a, data in engine.fetch_many(list(dict.fromkeys(ids))):
            if isinstance(data, Exception):
                report["failed"].append({"filename": a.filename, "error": str(data)})
                continue
            digest = hashlib.sha256(data).hexdigest()
            existing = index.get(digest)
            if existing and (root / existing).exists():
                report["skipped"].append({"filename": a.filename, "reason": "identical file already saved",
                                          "path": existing})
                continue
            path = _unique(target_path(root, a, folder_tpl, name_tpl))
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            if a.date:
                ts = a.date.timestamp()
                os.utime(path, (ts, ts))     # file date = email date
            rel = path.relative_to(root).as_posix()
            index[digest] = rel
            report["saved"].append({"filename": a.filename, "path": rel, "size": len(data)})
            rows.append([datetime.now().isoformat(timespec="seconds"), rel, digest, len(data),
                         a.from_name, a.from_email, a.subject,
                         a.date.isoformat() if a.date else "", a.message_id])
        (root / INDEX).write_text(json.dumps(index, indent=1), "utf-8")
        if rows:
            new = not (root / MANIFEST).exists()
            with open(root / MANIFEST, "a", newline="", encoding="utf-8") as fh:
                w = csv.writer(fh)
                if new:
                    w.writerow(["saved_at", "path", "sha256", "bytes", "sender", "sender_email",
                                "subject", "email_date", "message_id"])
                w.writerows(rows)
    return report


def build_zip(engine, ids: list[str], folder_tpl: str = "sender/year-month",
              name_tpl: str = "original") -> tuple[bytes, dict]:
    buf = io.BytesIO()
    seen_hash: set[str] = set()
    names: set[str] = set()
    report = {"added": 0, "skipped": 0, "failed": 0}
    fake_root = Path("/zip-root")
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for a, data in engine.fetch_many(list(dict.fromkeys(ids))):
            if isinstance(data, Exception):
                report["failed"] += 1
                continue
            h = hashlib.sha256(data).hexdigest()
            if h in seen_hash:
                report["skipped"] += 1
                continue
            seen_hash.add(h)
            rel = target_path(fake_root, a, folder_tpl, name_tpl).relative_to(fake_root).as_posix()
            base, n = rel, 2
            while rel.lower() in names:
                stem, dot, ext = base.rpartition(".")
                rel = f"{stem} ({n}).{ext}" if dot else f"{base} ({n})"
                n += 1
            names.add(rel.lower())
            d = a.date or datetime(1980, 1, 1)
            info = zipfile.ZipInfo(rel, date_time=(max(d.year, 1980), d.month, d.day, d.hour, d.minute, d.second))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)
            report["added"] += 1
    return buf.getvalue(), report
