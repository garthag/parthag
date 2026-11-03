"""Command line.

  python -m mail_agent web              # browser UI at http://127.0.0.1:8765
  python -m mail_agent web --demo       # try it with a built-in demo mailbox
  python -m mail_agent search --from "mck kutty" --keyword invoice \\
        --since 2024-01-01 --until 2025-12-25 --type pdf --save ~/Invoices
"""

from __future__ import annotations

import argparse
import sys
import webbrowser
from datetime import datetime

from .config import settings
from .downloader import FOLDER_TEMPLATES, NAME_TEMPLATES, save_all
from .engine import SearchEngine
from .models import SearchRequest


def _d(s):
    return datetime.strptime(s, "%Y-%m-%d").date() if s else None


def _engine(args, cfg):
    if args.demo:
        from .demo import DemoSource
        return SearchEngine(DemoSource(), workers=cfg["workers"])
    from .imap_source import PROVIDERS, IMAPSource
    host, port = cfg["host"], cfg["port"]
    if args.provider:
        host, port = PROVIDERS[args.provider]
    if not (host and cfg["user"] and cfg["password"]):
        sys.exit("Set MAIL_HOST / MAIL_USER / MAIL_PASSWORD in .env (see .env.example) or use --demo")
    return SearchEngine(IMAPSource(host, cfg["user"], cfg["password"], port=port, pool_size=cfg["workers"]),
                        workers=cfg["workers"])


def parse_selection(text: str, n: int) -> list[int]:
    text = text.strip().lower()
    if text in ("a", "all", "*"):
        return list(range(n))
    out: list[int] = []
    for chunk in text.replace(" ", ",").split(","):
        if not chunk:
            continue
        if "-" in chunk:
            a, b = chunk.split("-", 1)
            rng = range(int(a), int(b) + 1)
        else:
            rng = [int(chunk)]
        for i in rng:
            if 1 <= i <= n and i - 1 not in out:
                out.append(i - 1)
    return out


def _size(n: int) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return f"{n:.0f} {unit}" if unit == "B" else f"{n:.1f} {unit}"
        n /= 1024
    return str(n)


def cmd_search(args, cfg) -> int:
    eng = _engine(args, cfg)
    req = SearchRequest(senders=args.sender, keywords=args.keyword, exclude_keywords=args.exclude,
                        date_from=_d(args.since), date_to=_d(args.until), file_types=args.type,
                        folders=args.folder, match_pdf_text=args.deep, hide_duplicates=not args.keep_duplicates,
                        include_inline=args.inline, min_score=args.min_score)
    res = eng.search(req, progress=lambda m: print(f"... {m}", file=sys.stderr))
    rows, st = res["results"], res["stats"]
    print(f"\n{st['results']} attachments ({_size(st['total_size'])}) from {st['messages']} emails "
          f"in {st['seconds']}s | {st['cells']} matrix cells | removed {st['duplicate_messages']} duplicate "
          f"emails + {st['duplicate_attachments']} duplicate files\n")
    for i, r in enumerate(rows, 1):
        dup = f" (+{r['duplicates']} dup)" if r["duplicates"] else ""
        print(f"{i:>4}. {r['date'][:10] if r['date'] else '----------'}  {r['score']:.2f}  "
              f"{_size(r['size']):>9}  {r['filename']}{dup}\n      {r['from_name']} | {r['subject'][:70]}")
    if not rows or not args.save:
        eng.close()
        return 0
    pick = list(range(len(rows))) if args.yes else parse_selection(
        input("\nDownload which? (e.g. 1,3,5-9 / all / blank = none): "), len(rows))
    if pick:
        rep = save_all(eng, [rows[i]["id"] for i in pick], args.save, args.layout, args.naming)
        print(f"\nSaved {len(rep['saved'])} to {rep['folder']}  |  skipped {len(rep['skipped'])} identical  |  "
              f"failed {len(rep['failed'])}")
        for f in rep["failed"]:
            print("  FAILED", f["filename"], f["error"])
    eng.close()
    return 0


def cmd_web(args, cfg) -> int:
    from .web import create_app
    engine = None
    if args.demo:
        engine = _engine(args, cfg)
    app = create_app(cfg, engine=engine, demo=args.demo)
    url = f"http://127.0.0.1:{args.port}"
    print(f"Mail Attachment Agent running at {url}  (Ctrl+C to stop)")
    if not args.no_browser:
        webbrowser.open(url)
    app.run(host="127.0.0.1", port=args.port, threaded=True, debug=False)
    return 0


def main(argv=None) -> int:
    cfg = settings()
    p = argparse.ArgumentParser(prog="mail_agent", description="Find, preview & download email attachments fast.")
    sub = p.add_subparsers(dest="cmd", required=True)

    w = sub.add_parser("web", help="open the browser UI")
    w.add_argument("--port", type=int, default=8765)
    w.add_argument("--demo", action="store_true", help="use the built-in demo mailbox")
    w.add_argument("--no-browser", action="store_true")

    s = sub.add_parser("search", help="search from the terminal")
    s.add_argument("--from", dest="sender", action="append", default=[], help="sender name/email (repeatable)")
    s.add_argument("--keyword", "-k", action="append", default=[], help="word in subject/body/filename (repeatable)")
    s.add_argument("--exclude", action="append", default=[], help="skip if subject/filename contains this")
    s.add_argument("--since", help="YYYY-MM-DD (inclusive)")
    s.add_argument("--until", help="YYYY-MM-DD (inclusive)")
    s.add_argument("--type", "-t", action="append", default=[], help="pdf, xlsx, image, excel, word, ...")
    s.add_argument("--folder", action="append", default=[], help="mailbox folder (default: INBOX / All Mail)")
    s.add_argument("--deep", action="store_true", help="also search inside PDF text")
    s.add_argument("--keep-duplicates", action="store_true")
    s.add_argument("--inline", action="store_true", help="include inline images (logos)")
    s.add_argument("--min-score", type=float, default=0.0)
    s.add_argument("--save", help="folder to download into")
    s.add_argument("--layout", default="sender/year-month", help=f"one of {list(FOLDER_TEMPLATES)} or a template")
    s.add_argument("--naming", default="original", help=f"one of {list(NAME_TEMPLATES)} or a template")
    s.add_argument("--yes", "-y", action="store_true", help="download all results without asking")
    s.add_argument("--provider", choices=["gmail", "outlook", "yahoo", "zoho", "zoho.in", "icloud", "rediff"])
    s.add_argument("--demo", action="store_true")

    args = p.parse_args(argv)
    return cmd_web(args, cfg) if args.cmd == "web" else cmd_search(args, cfg)
