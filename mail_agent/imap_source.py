"""IMAP mail source with a thread-safe connection pool.

Speed tricks:
  * N parallel connections, each running a different matrix cell.
  * Search runs on the server (Gmail X-GM-RAW when available).
  * We fetch only BODYSTRUCTURE + 4 headers + the first ~2 KB of body text.
    The actual attachment bytes are fetched only on preview / download, and only
    the one MIME part that is needed (BODY.PEEK[2]), never the whole email.
  * Everything uses PEEK / read-only mode, so nothing is marked as read.
"""

from __future__ import annotations

import queue
import ssl
import threading
from contextlib import contextmanager
from email import message_from_bytes, policy
from email.utils import getaddresses, parsedate_to_datetime

from imapclient import IMAPClient

from .matrix import gmail_raw, imap_criteria
from .mime_utils import decode_transfer, parts_from_bodystructure, to_snippet
from .models import MessageMeta, SearchCell

PROVIDERS = {
    "gmail": ("imap.gmail.com", 993),
    "outlook": ("outlook.office365.com", 993),
    "yahoo": ("imap.mail.yahoo.com", 993),
    "zoho": ("imap.zoho.com", 993),
    "zoho.in": ("imap.zoho.in", 993),
    "icloud": ("imap.mail.me.com", 993),
    "rediff": ("imap.rediffmail.com", 993),
}

HEADER_KEY = "BODY.PEEK[HEADER.FIELDS (FROM TO CC SUBJECT DATE MESSAGE-ID)]"


class _Conn:
    def __init__(self, client: IMAPClient):
        self.client = client
        self.folder: str | None = None


class IMAPSource:
    name = "imap"

    def __init__(self, host: str, user: str, password: str, port: int = 993,
                 use_ssl: bool = True, pool_size: int = 6, timeout: float = 60):
        self.host, self.user, self.password, self.port = host, user, password, port
        self.use_ssl, self.timeout = use_ssl, timeout
        self.pool_size = max(1, pool_size)
        self._idle: queue.LifoQueue[_Conn] = queue.LifoQueue()
        self._slots = threading.BoundedSemaphore(self.pool_size)
        self._all: list[_Conn] = []
        self._lock = threading.Lock()
        # Connect once up-front: validates credentials and detects Gmail.
        first = self._connect()
        caps = {c.upper() for c in first.client.capabilities()}
        self.is_gmail = b"X-GM-EXT-1" in caps
        self._idle.put(first)

    # ------------------------------------------------------------------ pool
    def _connect(self) -> _Conn:
        ctx = ssl.create_default_context() if self.use_ssl else None
        c = IMAPClient(self.host, port=self.port, ssl=self.use_ssl, ssl_context=ctx, timeout=self.timeout)
        c.login(self.user, self.password)
        conn = _Conn(c)
        with self._lock:
            self._all.append(conn)
        return conn

    @contextmanager
    def _conn(self, folder: str | None = None):
        self._slots.acquire()
        conn = None
        try:
            try:
                conn = self._idle.get_nowait()
            except queue.Empty:
                conn = self._connect()
            if folder and conn.folder != folder:
                conn.client.select_folder(folder, readonly=True)
                conn.folder = folder
            yield conn.client
            self._idle.put(conn)
            conn = None
        finally:
            if conn is not None:          # broken connection - drop it
                self._drop(conn)
            self._slots.release()

    def _drop(self, conn: _Conn):
        with self._lock:
            if conn in self._all:
                self._all.remove(conn)
        try:
            conn.client.logout()
        except Exception:
            pass

    def close(self):
        with self._lock:
            conns, self._all = self._all[:], []
        for c in conns:
            try:
                c.client.logout()
            except Exception:
                pass

    # --------------------------------------------------------------- folders
    def list_folders(self) -> list[str]:
        with self._conn() as c:
            out = []
            for flags, _delim, name in c.list_folders():
                if b"\\Noselect" in flags or b"\\NonExistent" in flags:
                    continue
                out.append(name)
            return out

    def default_folders(self) -> list[str]:
        if self.is_gmail:
            with self._conn() as c:
                try:
                    from imapclient import ALL
                    f = c.find_special_folder(ALL)
                    if f:
                        return [f]
                except Exception:
                    pass
            return ["[Gmail]/All Mail"]
        return ["INBOX"]

    # ---------------------------------------------------------------- search
    def search(self, folder: str, cell: SearchCell) -> list[int]:
        with self._conn(folder) as c:
            if self.is_gmail:
                return list(c.gmail_search(gmail_raw(cell)))
            crit = imap_criteria(cell)
            charset = "UTF-8" if any(ord(ch) > 127 for ch in repr(crit)) else None
            return list(c.search(crit, charset=charset))

    # ---------------------------------------------------- incremental index
    def folder_info(self, folder: str) -> dict:
        with self._conn() as c:
            st = c.folder_status(folder, ["UIDVALIDITY", "UIDNEXT", "MESSAGES"])
        return {"uidvalidity": int(st.get(b"UIDVALIDITY", 0)), "uidnext": int(st.get(b"UIDNEXT", 0)),
                "messages": int(st.get(b"MESSAGES", 0))}

    def uids_since(self, folder: str, start_uid: int, since=None) -> list[int]:
        """UIDs >= start_uid (only mails with attachments on Gmail)."""
        crit: list = ["UID", f"{max(1, start_uid)}:*"]
        if since:
            crit += ["SINCE", since]
        if self.is_gmail:
            crit += ["X-GM-RAW", "has:attachment"]
        with self._conn(folder) as c:
            return [u for u in c.search(crit) if u >= start_uid]   # "n:*" always returns the last UID

    def all_uids(self, folder: str) -> set[int]:
        with self._conn(folder) as c:
            return set(c.search("ALL"))

    # ----------------------------------------------------------------- fetch
    def fetch_meta(self, folder: str, uids: list[int]) -> list[MessageMeta]:
        if not uids:
            return []
        with self._conn(folder) as c:
            resp = c.fetch(uids, ["BODYSTRUCTURE", "INTERNALDATE", HEADER_KEY])
            metas: dict[int, MessageMeta] = {}
            text_parts: dict[str, list[tuple[int, str, str]]] = {}
            for uid, data in resp.items():
                bs = data.get(b"BODYSTRUCTURE")
                if bs is None:
                    continue
                hdr_raw = next((v for k, v in data.items() if k.startswith(b"BODY[HEADER")), b"") or b""
                meta = _meta_from_headers(folder, uid, hdr_raw, data.get(b"INTERNALDATE"))
                meta.attachments, text = parts_from_bodystructure(bs)
                if not meta.attachments:
                    continue
                metas[uid] = meta
                if text:
                    text_parts.setdefault(text[0], []).append((uid, text[1], text[2]))
            # One extra round-trip per distinct text-part number (usually 1 or 2)
            for part, items in text_parts.items():
                key = f"BODY.PEEK[{part}]<0.2400>"
                got = c.fetch([u for u, _, _ in items], [key])
                for uid, enc, cs in items:
                    d = got.get(uid, {})
                    raw = next((v for k, v in d.items() if k.startswith(b"BODY[")), b"") or b""
                    metas[uid].snippet = to_snippet(decode_transfer(raw, enc, partial=True), cs)
            return list(metas.values())

    def fetch_part(self, folder: str, uid: int, part: str, encoding: str) -> bytes:
        with self._conn(folder) as c:
            got = c.fetch([uid], [f"BODY.PEEK[{part}]"]).get(uid, {})
            raw = next((v for k, v in got.items() if k.startswith(b"BODY[")), None)
            if raw is None:
                raise KeyError(f"part {part} of message {uid} not found")
            return decode_transfer(raw, encoding)


def _meta_from_headers(folder: str, uid: int, raw: bytes, internal) -> MessageMeta:
    try:
        msg = message_from_bytes(raw, policy=policy.default)
        frm = str(msg.get("From", "") or "")
        to = ", ".join(str(msg.get(h, "") or "") for h in ("To", "Cc") if msg.get(h))
        subject = str(msg.get("Subject", "") or "")
        mid = str(msg.get("Message-ID", "") or "").strip()
        date_hdr = msg.get("Date")
    except Exception:
        frm = subject = mid = to = ""
        date_hdr = None
    name, email = (getaddresses([frm]) or [("", "")])[0]
    dt = None
    if date_hdr:
        try:
            dt = parsedate_to_datetime(str(date_hdr))
        except Exception:
            dt = None
    if dt is None and internal is not None:
        dt = internal
    return MessageMeta(folder=folder, uid=uid, message_id=mid, from_name=name or email,
                       from_email=email.lower(), subject=subject, date=dt, to=to)
