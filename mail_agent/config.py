"""Settings from environment variables or a local .env file (never committed)."""

from __future__ import annotations

import os
from pathlib import Path


def load_dotenv(path: str | os.PathLike = ".env") -> None:
    p = Path(path)
    if not p.is_file():
        return
    for line in p.read_text("utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        k, v = line.split("=", 1)
        os.environ.setdefault(k.strip(), v.strip().strip('"').strip("'"))


def settings() -> dict:
    load_dotenv()
    return {
        "host": os.environ.get("MAIL_HOST", ""),
        "port": int(os.environ.get("MAIL_PORT", "993") or 993),
        "user": os.environ.get("MAIL_USER", ""),
        "password": os.environ.get("MAIL_PASSWORD", ""),
        "download_dir": os.path.expanduser(os.environ.get("DOWNLOAD_DIR", "~/MailAttachments")),
        "workers": int(os.environ.get("MAIL_WORKERS", "6") or 6),
    }
