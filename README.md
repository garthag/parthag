# parthag

## Mail Attachment Agent

![screenshot](docs/screenshot.png)

Finds email attachments fast, **reads them** (including scanned PDFs and photos), pulls out invoice details,
and lets you **preview, select, download, export and share**, with no duplicates.

> Example: *all invoices from MCK Kutty between 1 Jan 2024 and 25 Dec 2025, as PDF, with their amounts in Excel.*

Works with **Gmail, Outlook/Hotmail/Microsoft 365, Yahoo, Zoho, iCloud, Rediffmail** or any IMAP mailbox.
It only **reads** your mail: nothing is deleted, moved or marked as read. Everything runs on your computer.

### What it can do

| | |
|---|---|
| **Instant search (local index)** | Sync once. After that every search runs in milliseconds against a small database on your PC, and later syncs only fetch new mail. |
| **Live search** | Or search the mail server directly, using parallel queries (the "query matrix"). Everything found this way is added to the index too. |
| **Filters** | Sender, To/Cc, keywords (*any / all / exact phrase*), exclude words, typo tolerance, date range (quick FY buttons), file type, **document type** (invoice, receipt, statement, payslip, quotation, order, credit note), file-name pattern (`INV*2025*.pdf`), size range, **amount range**, scanned/read state, folders, minimum match score. |
| **Text reader** | Read the text of any PDF, Word, Excel or CSV file. The keywords you searched for are highlighted, and you can copy the text or save it as `.txt`. |
| **Scanner (OCR)** | Scanned PDFs and photos of bills are turned into text. Windows uses its **built-in OCR**, so nothing extra to install; other systems use Tesseract if it's installed. |
| **Smart fields** | Detects the document type, **number, date, amount, currency and GSTIN** automatically. You can correct any value, and your correction is kept. |
| **Table view** | All results as a spreadsheet: edit cells, group by month, sender or type, with totals. |
| **Tables inside files** | Pulls line-item tables out of PDFs, scans and Excel/CSV files, and saves them as **Excel/CSV** or copies them for pasting into Excel. |
| **Download** | Save to a folder (`sender/year-month`, …) with a `manifest.csv`, or as a ZIP. A file is never saved twice (checked by SHA-256). |
| **Export** | **Excel** (list + monthly totals + per-sender totals), CSV, JSON, or one `.txt` with the text of every selected file. |
| **Share** | Email the selected files from your own account, open them as an **Outlook draft** (`.eml`), or copy a summary list (date, file, number, amount, total) for WhatsApp or a document. |
| **Hexagon score** | Every result is checked on 6 points: sender, subject, body/document text, file name, type and date. The hexagon icon shows *why* it matched. |

### Windows app (recommended)

1. On GitHub open **Actions → "Windows app"**, click the latest green run, and download
   **MailAttachmentAgent-Windows-…** from *Artifacts* (unzip it). For tagged versions (`v1.0.0` …) the files are
   also on the **Releases** page.
2. Run **`MailAttachmentAgent-Setup-….exe`**. No administrator rights are needed. If SmartScreen says
   "Windows protected your PC", click **More info → Run anyway** (the app isn't code-signed).
3. Start **Mail Attachment Agent** from the Start menu. The **"(demo mailbox)"** shortcut lets you practise first.

The desktop app adds native **Browse… / Save as** dialogs, **Open / Show in folder** buttons, opens `.eml` drafts in
Outlook, and has **Remember me**, which keeps your password in the *Windows Credential Manager*.
A **portable ZIP** (no installer) is in the same download. Needs Windows 10/11 64-bit with WebView2
(built into Windows 11 and recent Windows 10).

### First use

1. **Connect**: choose your provider, enter your email and an **App Password** (Gmail: turn on 2-step verification,
   then go to <https://myaccount.google.com/apppasswords>). Your normal password won't work with IMAP.
2. **Index → Sync now**: builds the local index. You can limit it to mail since a date. Later syncs are incremental,
   and happen automatically when you connect.
3. **Index → Read all documents** (optional, runs in the background): reads and scans every PDF and image, so amounts,
   numbers and the text inside files become searchable and filterable.
4. **Search**: fill the filters and press **Search**. Click a row to preview it. Use the **Text**, **Details** and
   **Tables** tabs, **↑/↓** to move between files, **Space** to select, and **Shift-click** to select a range.
5. **Save / ZIP / Share / Export** the selection. The **Table** tab gives a spreadsheet of everything found, with totals.

### Run from source (any OS)

```bash
pip install -r requirements.txt
```

```bash
python -m mail_agent web --demo
```

Drop `--demo` to use your real mailbox. `python -m mail_agent desktop` opens it in a window if pywebview is
installed. For OCR outside Windows, install Tesseract (`apt install tesseract-ocr`, `brew install tesseract`).

Command line:

```bash
python -m mail_agent search --from "mck kutty" -k invoice --since 2024-01-01 --until 2025-12-25 -t pdf --save ~/Invoices
```

### How it's fast and exact

* **Local index (SQLite):** about 1 KB per attachment (metadata, detected fields and text, never the files).
  Syncing remembers the last message number per folder (UIDVALIDITY + UID), so only new mail is fetched. Mail deleted
  on the server is removed from the index, and an interrupted sync resumes where it stopped.
* **Query matrix (live mode):** date windows × senders × folders, run as parallel server-side searches. On Gmail
  it uses Gmail's own search (`X-GM-RAW`), which also matches attachment file names.
* **Structure-only fetch:** only each email's MIME structure, headers and about 2 KB of text are downloaded. Files
  are fetched one MIME part at a time, only when they are previewed, read, saved or shared, and are cached.
* **3-level de-duplication:** the same email in several folders or labels, the same file re-sent, and identical
  file content when saving (SHA-256).
* **Hexagon scoring** with typo and initials tolerance (`M.C.K Kuty` matches `mck kutty`), plus keyword
  matching inside the text read from documents.

### Privacy & safety

* The UI listens on `127.0.0.1` only and rejects other host names and non-JSON POSTs.
* Passwords stay on your PC, in memory or in the Windows Credential Manager. Account details and the index live in
  `%APPDATA%\MailAttachmentAgent` (`~/.mail_agent` on other systems).
* Attachments are previewed safely: HTML is shown as plain text and never runs scripts.
* Saved file names are sanitised, so nothing is ever written outside the folder you chose.
* "Send email" goes through your own provider's SMTP server. The demo mailbox never sends anything.

### Tests

```bash
python -m pytest -q
```
