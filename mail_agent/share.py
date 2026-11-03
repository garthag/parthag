"""Share by email: send selected attachments from your own account over SMTP."""

from __future__ import annotations

import smtplib
import ssl
from email.message import EmailMessage
from email.utils import formatdate, getaddresses, make_msgid

SMTP = {
    "imap.gmail.com": ("smtp.gmail.com", 465, "ssl"),
    "outlook.office365.com": ("smtp.office365.com", 587, "starttls"),
    "imap-mail.outlook.com": ("smtp-mail.outlook.com", 587, "starttls"),
    "imap.mail.yahoo.com": ("smtp.mail.yahoo.com", 465, "ssl"),
    "imap.zoho.com": ("smtp.zoho.com", 465, "ssl"),
    "imap.zoho.in": ("smtp.zoho.in", 465, "ssl"),
    "imap.mail.me.com": ("smtp.mail.me.com", 587, "starttls"),
    "imap.rediffmail.com": ("smtp.rediffmail.com", 465, "ssl"),
}
MAX_TOTAL = 24 * 1024 * 1024   # Gmail/Outlook limit is 25 MB including encoding overhead


def smtp_for(imap_host: str) -> tuple[str, int, str]:
    if imap_host in SMTP:
        return SMTP[imap_host]
    h = imap_host[5:] if imap_host.startswith("imap.") else imap_host
    return f"smtp.{h}", 465, "ssl"


def parse_recipients(text: str) -> list[str]:
    addrs = [a for _, a in getaddresses([text.replace(";", ",")]) if "@" in a]
    return list(dict.fromkeys(addrs))


def send(host: str, user: str, password: str, to: list[str], subject: str, body: str,
         files: list[tuple[str, bytes, str]], timeout: float = 60) -> dict:
    total = sum(len(d) for _, d, _ in files) * 4 // 3      # base64 growth
    if total > MAX_TOTAL:
        raise ValueError(f"Too big to email ({total / 1048576:.1f} MB after encoding, limit ~24 MB). "
                         "Select fewer files or use Download ZIP.")
    m = EmailMessage()
    m["From"], m["To"], m["Subject"] = user, ", ".join(to), subject or "Attachments"
    m["Date"], m["Message-ID"] = formatdate(localtime=True), make_msgid()
    m.set_content(body or "Please find the attached files.")
    for name, data, mime in files:
        maintype, _, subtype = (mime or "application/octet-stream").partition("/")
        m.add_attachment(data, maintype=maintype or "application", subtype=subtype or "octet-stream", filename=name)
    s_host, port, mode = smtp_for(host)
    ctx = ssl.create_default_context()
    if mode == "ssl":
        server = smtplib.SMTP_SSL(s_host, port, context=ctx, timeout=timeout)
    else:
        server = smtplib.SMTP(s_host, port, timeout=timeout)
        server.starttls(context=ctx)
    try:
        server.login(user, password)
        server.send_message(m)
    finally:
        try:
            server.quit()
        except Exception:
            pass
    return {"sent_to": to, "files": len(files), "server": s_host}
