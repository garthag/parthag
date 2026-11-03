"""Entry point of MailAgent.exe (PyInstaller). Double-click = desktop window."""
import os
import sys
import traceback

# A --windowed exe has no console: give print() somewhere harmless to go.
if sys.stdout is None:
    sys.stdout = open(os.devnull, "w")
if sys.stderr is None:
    sys.stderr = open(os.devnull, "w")


def _fatal(text: str) -> None:
    try:
        from mail_agent.config import app_dir
        log = app_dir() / "error.log"
        log.write_text(text, "utf-8")
        where = f"\n\nDetails saved to:\n{log}"
    except Exception:
        where = ""
    try:
        import ctypes
        ctypes.windll.user32.MessageBoxW(None, f"Mail Attachment Agent could not start.{where}",
                                         "Mail Attachment Agent", 0x10)
    except Exception:
        pass


if __name__ == "__main__":
    try:
        from mail_agent.cli import main
        sys.exit(main(sys.argv[1:] or ["desktop"]))
    except SystemExit:
        raise
    except Exception:
        _fatal(traceback.format_exc())
        sys.exit(1)
