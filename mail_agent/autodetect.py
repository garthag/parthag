"""Find the IMAP server for any email address (company domains included).

Looks up the domain's MX (mail exchanger) records - first with DNS-over-HTTPS,
then with the system's nslookup - and maps the mail host to its IMAP server.
Falls back to trying imap.<domain> / mail.<domain> on port 993.
"""

from __future__ import annotations

import json
import re
import socket
import subprocess
import sys
import urllib.request

# (pattern in MX host, provider key, label, imap host or None = derive, App Password hint key)
MX_MAP = [
    (r"(aspmx\.l\.google\.com|googlemail\.com|google\.com|smtp\.google\.com)$", "google", "Google Workspace (Gmail)",
     "imap.gmail.com"),
    (r"(protection\.outlook\.com|outlook\.com|hotmail\.com|office365\.us)$", "microsoft", "Microsoft 365 / Outlook",
     "outlook.office365.com"),
    (r"zoho\.in$", "zoho.in", "Zoho Mail (India)", "imap.zoho.in"),
    (r"zoho\.eu$", "zoho.eu", "Zoho Mail (EU)", "imap.zoho.eu"),
    (r"zoho\.com$", "zoho", "Zoho Mail", "imap.zoho.com"),
    (r"titan\.email$", "titan", "Titan Mail", "imap.titan.email"),
    (r"secureserver\.net$", "godaddy", "GoDaddy Workspace Email", "imap.secureserver.net"),
    (r"hostinger\.(com|in)$", "hostinger", "Hostinger Email", "imap.hostinger.com"),
    (r"yandex\.(net|ru|com)$", "yandex", "Yandex Mail", "imap.yandex.com"),
    (r"(icloud\.com|me\.com)$", "icloud", "iCloud Mail", "imap.mail.me.com"),
    (r"(yahoodns\.net|yahoo\.com)$", "yahoo", "Yahoo Mail", "imap.mail.yahoo.com"),
    (r"rediffmail(pro)?\.com$", "rediff", "Rediffmail Pro", "imap.rediffmailpro.com"),
    (r"(mailgun\.org|sendgrid\.net|mimecast\.com|pphosted\.com|barracudanetworks\.com)$", "filter",
     "Spam filter in front of your mail server", None),
]
PUBLIC = {
    "gmail.com": ("google", "Gmail", "imap.gmail.com"), "googlemail.com": ("google", "Gmail", "imap.gmail.com"),
    "outlook.com": ("microsoft", "Outlook.com", "outlook.office365.com"),
    "hotmail.com": ("microsoft", "Outlook.com", "outlook.office365.com"),
    "live.com": ("microsoft", "Outlook.com", "outlook.office365.com"),
    "yahoo.com": ("yahoo", "Yahoo Mail", "imap.mail.yahoo.com"), "yahoo.co.in": ("yahoo", "Yahoo Mail", "imap.mail.yahoo.com"),
    "icloud.com": ("icloud", "iCloud Mail", "imap.mail.me.com"), "me.com": ("icloud", "iCloud Mail", "imap.mail.me.com"),
    "zohomail.in": ("zoho.in", "Zoho Mail (India)", "imap.zoho.in"), "zohomail.com": ("zoho", "Zoho Mail", "imap.zoho.com"),
    "rediffmail.com": ("rediff", "Rediffmail", "imap.rediffmail.com"),
}
HINTS = {
    "google": "Company Gmail (Google Workspace): use an App Password from myaccount.google.com/apppasswords "
              "(2-Step Verification must be on). If that page is missing, ask your Google admin to allow "
              "App Passwords / IMAP.",
    "microsoft": "Microsoft 365: use an App Password (account.microsoft.com/security). Many company accounts have "
                 "IMAP login turned off - ask your IT admin to enable IMAP for your mailbox.",
    "zoho": "Zoho: enable IMAP in Zoho Mail Settings -> Mail Accounts -> IMAP, then use an app-specific password "
            "if 2-factor login is on.",
    "zoho.in": "Zoho: enable IMAP in Zoho Mail Settings -> Mail Accounts -> IMAP, then use an app-specific password "
               "if 2-factor login is on.",
    "titan": "Titan: use your normal email password.",
    "godaddy": "GoDaddy: use your normal email password.",
    "hostinger": "Hostinger: use your normal email password.",
    "cpanel": "Hosting (cPanel) mail: use your normal email password.",
}


def _resolve_doh(domain: str, timeout: float = 6) -> list[str]:
    for url in (f"https://dns.google/resolve?name={domain}&type=MX",
                f"https://cloudflare-dns.com/dns-query?name={domain}&type=MX"):
        try:
            req = urllib.request.Request(url, headers={"accept": "application/dns-json"})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                data = json.loads(r.read().decode())
            ans = [a.get("data", "") for a in data.get("Answer", []) if a.get("type") == 15]
            if ans:
                return [a.split()[-1].rstrip(".").lower() for a in sorted(ans, key=lambda x: int(x.split()[0]))]
        except Exception:
            continue
    return []


def _resolve_nslookup(domain: str, timeout: float = 8) -> list[str]:
    try:
        flags = {"creationflags": 0x08000000} if sys.platform == "win32" else {}   # no console window
        out = subprocess.run(["nslookup", "-type=MX", domain], capture_output=True, text=True,
                             timeout=timeout, **flags).stdout
    except Exception:
        return []
    found = re.findall(r"mail exchanger\s*=\s*(?:\d+\s+)?([\w.-]+)", out, re.I)
    return [h.rstrip(".").lower() for h in found]


def mx_hosts(domain: str) -> list[str]:
    return _resolve_doh(domain) or _resolve_nslookup(domain)


def _port_open(host: str, port: int = 993, timeout: float = 4) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


def detect(email: str, resolver=mx_hosts, probe=_port_open) -> dict:
    email = (email or "").strip().lower()
    if "@" not in email:
        return {"found": False, "reason": "Enter a full email address"}
    domain = email.rsplit("@", 1)[1]
    if domain in PUBLIC:
        key, label, host = PUBLIC[domain]
        return {"found": True, "provider": key, "label": label, "host": host, "port": 993,
                "hint": HINTS.get(key, ""), "domain": domain, "mx": []}
    mx = resolver(domain)
    for m in mx:
        for pat, key, label, host in MX_MAP:
            if re.search(pat, m):
                if host:
                    return {"found": True, "provider": key, "label": label, "host": host, "port": 993,
                            "hint": HINTS.get(key, ""), "domain": domain, "mx": mx}
                break
    # Own hosting (cPanel, Plesk, ...): the mail server is usually mail.<domain> or imap.<domain>
    for cand in [f"mail.{domain}", f"imap.{domain}"] + [m for m in mx if m.endswith(domain)][:1]:
        if probe(cand):
            return {"found": True, "provider": "cpanel", "label": f"Your hosting's mail server ({cand})",
                    "host": cand, "port": 993, "hint": HINTS["cpanel"], "domain": domain, "mx": mx}
    return {"found": False, "domain": domain, "mx": mx,
            "reason": ("Mail for this domain is handled by " + ", ".join(mx[:2]) + " - enter its IMAP server "
                       "under 'Other'." if mx else "Could not look up this domain's mail server. Choose 'Other' "
                       "and enter the IMAP server from your email provider / IT person.")}
