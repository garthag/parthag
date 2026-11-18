"""Content-addressed file storage for uploaded drawings."""

from __future__ import annotations

import hashlib
import os
import shutil
import tempfile
from pathlib import Path
from typing import BinaryIO

from app.core.config import settings
from app.core.exceptions import ValidationFailedError

CHUNK = 1024 * 1024
PDF_MAGIC = b"%PDF-"


def storage_root() -> Path:
    root = Path(settings.STORAGE_DIR).resolve()
    root.mkdir(parents=True, exist_ok=True)
    return root


def save_stream(stream: BinaryIO, project_code: str) -> tuple[str, str, int]:
    """Stream ``stream`` to storage. Returns (relative storage path, sha256, size).

    Files are written to a temp file while hashing, then atomically moved to
    ``<root>/<project>/<sha[:2]>/<sha>.pdf`` so identical uploads are stored once.
    """
    root = storage_root()
    max_bytes = settings.MAX_UPLOAD_MB * 1024 * 1024
    digest = hashlib.sha256()
    size = 0
    head = b""
    tmp_dir = root / ".tmp"
    tmp_dir.mkdir(exist_ok=True)
    fd, tmp_name = tempfile.mkstemp(dir=tmp_dir, suffix=".part")
    try:
        with os.fdopen(fd, "wb") as out:
            while chunk := stream.read(CHUNK):
                if not head:
                    head = chunk[:8]
                size += len(chunk)
                if size > max_bytes:
                    raise ValidationFailedError(f"File exceeds {settings.MAX_UPLOAD_MB} MB limit")
                digest.update(chunk)
                out.write(chunk)
        if not head.startswith(PDF_MAGIC):
            raise ValidationFailedError("File is not a valid PDF")
        sha = digest.hexdigest()
        safe_project = "".join(c for c in project_code if c.isalnum() or c in "-_") or "default"
        rel = Path(safe_project) / sha[:2] / f"{sha}.pdf"
        dest = root / rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        if dest.exists():
            os.unlink(tmp_name)
        else:
            shutil.move(tmp_name, dest)
        return rel.as_posix(), sha, size
    except Exception:
        if os.path.exists(tmp_name):
            os.unlink(tmp_name)
        raise


def absolute_path(storage_path: str) -> Path:
    root = storage_root()
    path = (root / storage_path).resolve()
    if root not in path.parents and path != root:
        raise ValidationFailedError("Invalid storage path")
    return path
