"""Local attachment index (SQLite) - the "database" behind instant search.

One small .db file per mail account in the app-data folder. It stores the
*metadata* of every attachment (sender, subject, date, file name, size ...),
the smart fields (doc type, number, amount, GSTIN ...) and, once a file has been
read or scanned, its text. File contents are NOT stored, so it stays small
(roughly 1 KB per attachment, plus extracted text).

Sync is incremental: per folder we remember UIDVALIDITY and the highest UID
seen, so the next sync only asks the server for newer mail.
"""

from __future__ import annotations

import hashlib
import json
import sqlite3
import threading
import time
from datetime import datetime, timedelta

from . import smartfields
from .models import FIELD_NAMES, Attachment, MessageMeta, SearchRequest, make_attachment_id
from .hexagon import MIME_EXT

SCHEMA = """
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
CREATE TABLE IF NOT EXISTS atts(
  id TEXT PRIMARY KEY, folder TEXT, uid INTEGER, part TEXT,
  filename TEXT, mime TEXT, ext TEXT, size INTEGER, encoding TEXT, inline INTEGER,
  message_id TEXT, from_name TEXT, from_email TEXT, to_addr TEXT, subject TEXT,
  date TEXT, ts REAL, snippet TEXT,
  doc_type TEXT, doc_no TEXT, doc_date TEXT, amount REAL, currency TEXT, gstin TEXT,
  user_fields TEXT DEFAULT '',
  text TEXT DEFAULT '', text_source TEXT DEFAULT '', sha256 TEXT, indexed_at REAL);
CREATE INDEX IF NOT EXISTS ix_ts ON atts(ts);
CREATE INDEX IF NOT EXISTS ix_from ON atts(from_email);
CREATE INDEX IF NOT EXISTS ix_ext ON atts(ext);
CREATE INDEX IF NOT EXISTS ix_fu ON atts(folder, uid);
CREATE INDEX IF NOT EXISTS ix_type ON atts(doc_type);
CREATE TABLE IF NOT EXISTS folders(
  name TEXT PRIMARY KEY, uidvalidity INTEGER, last_uid INTEGER, synced_at REAL);
CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT);
"""

COLS = ("id", "folder", "uid", "part", "filename", "mime", "ext", "size", "encoding", "inline", "message_id",
        "from_name", "from_email", "to_addr", "subject", "date", "ts", "snippet", "doc_type", "doc_no",
        "doc_date", "amount", "currency", "gstin", "indexed_at")
META_COLS = [c for c in COLS if c not in ("id", "doc_type", "doc_no", "doc_date", "amount", "currency", "gstin")]
SYNC_BATCH = 250


def db_name(account: str) -> str:
    return "index-" + hashlib.sha1(account.lower().encode()).hexdigest()[:12] + ".db"


def _ts(dt: datetime | None) -> float | None:
    if dt is None:
        return None
    try:
        return dt.timestamp()
    except (OverflowError, OSError, ValueError):
        return None


class Index:
    def __init__(self, path: str = ":memory:"):
        self.path = path
        self.db = sqlite3.connect(path, check_same_thread=False, isolation_level=None)
        self.db.execute("PRAGMA secure_delete=ON")      # deleted rows are overwritten, not left in the file
        self.db.row_factory = sqlite3.Row
        self.lock = threading.RLock()
        with self.lock:
            self.db.executescript(SCHEMA)
        if path != ":memory:":
            from .config import restrict
            for suffix in ("", "-wal", "-shm"):
                restrict(path + suffix)

    # ------------------------------------------------------------- writing
    def _row(self, m: MessageMeta, p) -> tuple:
        ext = (p.filename.rsplit(".", 1)[-1].lower() if "." in p.filename else "") or MIME_EXT.get(p.mime.lower(), "")
        f = smartfields.quick(m.subject, m.snippet, p.filename)
        return (make_attachment_id(m.folder, m.uid, p.part), m.folder, m.uid, p.part, p.filename, p.mime, ext,
                p.size, p.encoding, int(p.inline), m.message_id, m.from_name, m.from_email, m.to, m.subject,
                m.date.isoformat() if m.date else None, _ts(m.date), m.snippet,
                f["doc_type"], f["doc_no"], f["doc_date"], f["amount"], f["currency"], f["gstin"], time.time())

    def upsert(self, metas: list[MessageMeta]) -> int:
        rows = [self._row(m, p) for m in metas for p in m.attachments]
        if not rows:
            return 0
        sets = ", ".join(f"{c}=excluded.{c}" for c in META_COLS)
        # smart fields from metadata only replace old ones when the file hasn't been read yet
        guess = ", ".join(f"{c}=CASE WHEN atts.text_source='' THEN excluded.{c} ELSE atts.{c} END"
                          for c in ("doc_type", "doc_no", "doc_date", "amount", "currency", "gstin"))
        sql = (f"INSERT INTO atts({','.join(COLS)}) VALUES({','.join('?' * len(COLS))}) "
               f"ON CONFLICT(id) DO UPDATE SET {sets}, {guess}")
        with self.lock:
            self.db.execute("BEGIN")
            self.db.executemany(sql, rows)
            self.db.execute("COMMIT")
        return len(rows)

    def set_text(self, att_id: str, text: str, source: str, sha256: str | None, fields: dict):
        with self.lock:
            uf = self._user_fields(att_id)
            f = {k: fields.get(k) for k in ("doc_type", "doc_no", "doc_date", "amount", "currency", "gstin")}
            f.update(uf)                                 # user corrections always win
            self.db.execute(
                "UPDATE atts SET text=?, text_source=?, sha256=COALESCE(?, sha256), doc_type=?, doc_no=?, "
                "doc_date=?, amount=?, currency=?, gstin=? WHERE id=?",
                (text, source, sha256, f["doc_type"], f["doc_no"], f["doc_date"], f["amount"], f["currency"],
                 f["gstin"], att_id))

    def _user_fields(self, att_id: str) -> dict:
        with self.lock:
            r = self.db.execute("SELECT user_fields FROM atts WHERE id=?", (att_id,)).fetchone()
        try:
            return json.loads(r[0]) if r and r[0] else {}
        except ValueError:
            return {}

    def set_user_field(self, att_id: str, field: str, value) -> dict:
        if field not in FIELD_NAMES:
            raise ValueError("unknown field")
        if field == "amount":
            value = None if value in (None, "") else smartfields.to_number(str(value))
        elif value is not None:
            value = str(value).strip() or None
        with self.lock:
            uf = self._user_fields(att_id)
            uf[field] = value
            self.db.execute(f"UPDATE atts SET {field}=?, user_fields=? WHERE id=?",
                            (value, json.dumps(uf), att_id))
        return uf

    def drop_folder(self, folder: str):
        with self.lock:
            self.db.execute("DELETE FROM atts WHERE folder=?", (folder,))
            self.db.execute("DELETE FROM folders WHERE name=?", (folder,))

    def prune(self, folder: str, alive_uids: set[int]) -> int:
        """Remove attachments whose email was deleted on the server."""
        with self.lock:
            have = {r[0] for r in self.db.execute("SELECT DISTINCT uid FROM atts WHERE folder=?", (folder,))}
            gone = list(have - alive_uids)
            for i in range(0, len(gone), 500):
                chunk = gone[i:i + 500]
                self.db.execute(f"DELETE FROM atts WHERE folder=? AND uid IN ({','.join('?' * len(chunk))})",
                                (folder, *chunk))
        return len(gone)

    def folder_state(self, folder: str):
        with self.lock:
            r = self.db.execute("SELECT uidvalidity, last_uid, synced_at FROM folders WHERE name=?",
                                (folder,)).fetchone()
        return dict(r) if r else None

    def set_folder_state(self, folder: str, uidvalidity: int, last_uid: int):
        with self.lock:
            self.db.execute("INSERT OR REPLACE INTO folders VALUES(?,?,?,?)",
                            (folder, uidvalidity, last_uid, time.time()))

    def clear(self):
        with self.lock:
            self.db.executescript("DELETE FROM atts; DELETE FROM folders; DELETE FROM meta;")

    def wipe(self):
        """Delete everything AND compact the file so deleted text can't be recovered from it."""
        with self.lock:
            self.db.executescript("DELETE FROM atts; DELETE FROM folders; DELETE FROM meta;")
            self.db.execute("PRAGMA wal_checkpoint(TRUNCATE)")
            self.db.execute("VACUUM")

    # ------------------------------------------------------------- reading
    @staticmethod
    def to_attachment(r: sqlite3.Row) -> Attachment:
        d = None
        if r["date"]:
            try:
                d = datetime.fromisoformat(r["date"])
            except ValueError:
                d = None
        return Attachment(
            id=r["id"], folder=r["folder"], uid=r["uid"], part=r["part"], filename=r["filename"], mime=r["mime"],
            size=r["size"], encoding=r["encoding"], inline=bool(r["inline"]), message_id=r["message_id"] or "",
            from_name=r["from_name"] or "", from_email=r["from_email"] or "", subject=r["subject"] or "", date=d,
            snippet=r["snippet"] or "", to=r["to_addr"] or "",
            fields={k: r[k] for k in FIELD_NAMES}, text=r["text"] or "", text_source=r["text_source"] or "")

    def get(self, att_id: str) -> Attachment | None:
        with self.lock:
            r = self.db.execute("SELECT * FROM atts WHERE id=?", (att_id,)).fetchone()
        return self.to_attachment(r) if r else None

    def get_many(self, ids: list[str]) -> dict[str, Attachment]:
        out = {}
        for i in range(0, len(ids), 500):
            chunk = ids[i:i + 500]
            with self.lock:
                rows = self.db.execute(f"SELECT * FROM atts WHERE id IN ({','.join('?' * len(chunk))})",
                                       chunk).fetchall()
            for r in rows:
                out[r["id"]] = self.to_attachment(r)
        return out

    def candidates(self, req: SearchRequest, types: list[str], folders: list[str] | None = None,
                   limit: int = 50000) -> list[Attachment]:
        """Cheap SQL pre-filter; exact matching/scoring is done by the hexagon afterwards."""
        where, args = [], []
        if not req.include_inline:
            where.append("inline=0")
        if req.date_from:
            where.append("ts >= ?")
            args.append(datetime.combine(req.date_from - timedelta(days=1), datetime.min.time()).timestamp())
        if req.date_to:
            where.append("ts < ?")
            args.append(datetime.combine(req.date_to + timedelta(days=2), datetime.min.time()).timestamp())
        if types:
            where.append(f"ext IN ({','.join('?' * len(types))})")
            args += types
        if folders:
            where.append(f"folder IN ({','.join('?' * len(folders))})")
            args += folders
        if req.size_min:
            where.append("size >= ?")
            args.append(req.size_min)
        if req.size_max:
            where.append("size <= ?")
            args.append(req.size_max)
        if req.amount_min is not None:
            where.append("amount >= ?")
            args.append(req.amount_min)
        if req.amount_max is not None:
            where.append("amount <= ?")
            args.append(req.amount_max)
        if req.doc_types:
            where.append(f"doc_type IN ({','.join('?' * len(req.doc_types))})")
            args += req.doc_types
        if not req.fuzzy and req.senders:
            likes = []
            for s in req.senders:
                likes.append("(lower(from_name) LIKE ? OR lower(from_email) LIKE ?)")
                args += [f"%{s.lower()}%", f"%{s.lower().replace(' ', '')}%"]
            where.append("(" + " OR ".join(likes) + ")")
        sql = "SELECT * FROM atts" + (" WHERE " + " AND ".join(where) if where else "") + " ORDER BY ts DESC LIMIT ?"
        args.append(limit)
        with self.lock:
            rows = self.db.execute(sql, args).fetchall()
        return [self.to_attachment(r) for r in rows]

    def ids_to_analyze(self, exts: list[str], limit: int = 100000) -> list[str]:
        q = (f"SELECT id FROM atts WHERE text_source='' AND inline=0 AND ext IN ({','.join('?' * len(exts))}) "
             "ORDER BY ts DESC LIMIT ?")
        with self.lock:
            return [r[0] for r in self.db.execute(q, (*exts, limit)).fetchall()]

    def stats(self) -> dict:
        with self.lock:
            r = self.db.execute(
                "SELECT COUNT(*), COUNT(DISTINCT message_id), COALESCE(SUM(size),0), MIN(ts), MAX(ts), "
                "SUM(text_source!=''), SUM(text_source='ocr') FROM atts WHERE inline=0").fetchone()
            folders = [dict(x) for x in self.db.execute("SELECT name, last_uid, synced_at FROM folders ORDER BY name")]
            types = [dict(x) for x in self.db.execute(
                "SELECT doc_type, COUNT(*) n FROM atts WHERE inline=0 GROUP BY doc_type ORDER BY n DESC")]
            exts = [dict(x) for x in self.db.execute(
                "SELECT ext, COUNT(*) n FROM atts WHERE inline=0 GROUP BY ext ORDER BY n DESC LIMIT 8")]
        db_bytes = 0
        if self.path != ":memory:":
            import os
            for suffix in ("", "-wal"):
                try:
                    db_bytes += os.path.getsize(self.path + suffix)
                except OSError:
                    pass
        return {"attachments": r[0], "messages": r[1], "total_size": r[2],
                "oldest": datetime.fromtimestamp(r[3]).date().isoformat() if r[3] else None,
                "newest": datetime.fromtimestamp(r[4]).date().isoformat() if r[4] else None,
                "analyzed": r[5] or 0, "scanned": r[6] or 0, "folders": folders, "doc_types": types,
                "exts": exts, "db_bytes": db_bytes,
                "last_sync": max((f["synced_at"] for f in folders), default=None)}

    def close(self):
        with self.lock:
            self.db.close()
