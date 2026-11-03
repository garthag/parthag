"""Settings from environment variables or a local .env file (never committed)."""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path

APP_NAME = "MailAttachmentAgent"


def app_dir() -> Path:
    """Per-user data folder: %APPDATA%\\MailAttachmentAgent on Windows, ~/.mail_agent elsewhere."""
    if sys.platform == "win32" and os.environ.get("APPDATA"):
        p = Path(os.environ["APPDATA"]) / APP_NAME
    else:
        p = Path.home() / ".mail_agent"
    p.mkdir(parents=True, exist_ok=True)
    return p


def default_download_dir() -> str:
    docs = Path.home() / "Documents"
    return str((docs if docs.is_dir() else Path.home()) / "MailAttachments")


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
    load_dotenv(app_dir() / ".env")
    return {
        "host": os.environ.get("MAIL_HOST", ""),
        "port": int(os.environ.get("MAIL_PORT", "993") or 993),
        "user": os.environ.get("MAIL_USER", ""),
        "password": os.environ.get("MAIL_PASSWORD", ""),
        "download_dir": os.path.expanduser(os.environ.get("DOWNLOAD_DIR", "") or default_download_dir()),
        "workers": int(os.environ.get("MAIL_WORKERS", "6") or 6),
    }


# ------------------------------------------------------------ saved account
# Account details (no password) go to account.json; the password goes to the
# OS credential store (Windows Credential Manager / macOS Keychain) via keyring.

def _keyring():
    try:
        import keyring
        from keyring.backends.fail import Keyring as Fail
        if isinstance(keyring.get_keyring(), Fail) and sys.platform == "win32":
            # packaged .exe can lose keyring's plugin discovery - pick Credential Manager directly
            from keyring.backends.Windows import WinVaultKeyring
            keyring.set_keyring(WinVaultKeyring())
        if isinstance(keyring.get_keyring(), Fail):
            return None
        return keyring
    except Exception:
        return None


def can_remember() -> bool:
    return _keyring() is not None


def save_account(host: str, port: int, user: str, password: str) -> bool:
    kr = _keyring()
    if kr is None:
        return False
    kr.set_password(APP_NAME, user, password)
    (app_dir() / "account.json").write_text(json.dumps({"host": host, "port": port, "user": user}), "utf-8")
    return True


def saved_account() -> dict | None:
    try:
        acc = json.loads((app_dir() / "account.json").read_text("utf-8"))
    except (OSError, ValueError):
        return None
    return acc if acc.get("user") else None


def saved_password(user: str) -> str | None:
    kr = _keyring()
    return kr.get_password(APP_NAME, user) if kr else None


def forget_account() -> None:
    acc = saved_account()
    kr = _keyring()
    if acc and kr:
        try:
            kr.delete_password(APP_NAME, acc["user"])
        except Exception:
            pass
    try:
        (app_dir() / "account.json").unlink()
    except OSError:
        pass
