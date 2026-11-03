# Mail Attachment Agent – Google edition (Apps Script)

The same app, running **inside your Google account** as a private web app:

* **No password, no App Password.** Google asks you to click *Allow* once. That's all.
* Nothing to install. Works in any browser, on PC and phone. No Windows SmartScreen warning.
* Your mail and files **never leave Google**.

| Feature | How it works here |
|---|---|
| Search | Gmail's own search engine, split into a **query matrix** (date windows × senders) run in parallel. Gmail also finds words **inside** PDFs and documents. |
| Filters | Sender, To/Cc, keywords (any / all / exact phrase), exclude, dates (FY buttons), file type, document type, file-name pattern, size, amount, read/scanned, Spam & Bin |
| Accuracy | Hexagon scoring (sender, subject, body/text, file name, type, date) with typo tolerance; duplicate emails and duplicate files removed |
| Preview | PDFs (pdf.js), images and text right in the page, with ↑/↓ to move and Space to select |
| Text reader / scanner | Google Drive **OCR** reads scanned PDFs and photos. Word and Excel are converted. Search words are highlighted. |
| Smart fields | Document type, number, date, amount, currency, **GSTIN**, editable. Your corrections are kept. |
| Table | All results as an editable spreadsheet, grouped by month, sender or type, with totals |
| Tables inside files | Line items → Google Sheet / CSV / copy |
| Save | **Google Drive → "Mail Attachments/sender/year-month"**. Identical files are never saved twice (SHA-256). |
| Download | ZIP to your PC (big ZIPs go to Drive) |
| Export | Google Sheet (list + totals by month and by sender), CSV, all text (.txt) |
| Share | Email the files, **share a Drive folder link**, or copy a summary list for WhatsApp |
| Database | A private Google Sheet in your Drive ("Mail Attachment Agent - data") |

---

## Set it up (about 10 minutes, once)

**Easiest:** use the 3 ready-made files in [`easy/`](easy/): `appsscript.json`, `Code.gs` and `Index.html` (create it as an
HTML file named `Index`). They contain exactly the same app as the 6 files below, merged by `node build-easy.js`.

Using the 6 separate files instead:

You need the 6 files from this folder: `appsscript.json`, `Code.gs`, `SmartFields.gs`, `Index.html`,
`Styles.html`, `App.html`. On GitHub, open each file → **Raw** → select all → copy.

1. **Create the project.** Sign in as **parthag@mckkutty.com**, open **https://script.google.com** →
   **New project**. Click "Untitled project" at the top and name it **Mail Attachment Agent**.

2. **Show the settings file.** Click ⚙ **Project Settings** (left) → tick
   **"Show 'appsscript.json' manifest file in editor"**. Go back to **‹› Editor**.

3. **Paste the files.**
   * Click **appsscript.json** → select all → paste the contents of `appsscript.json`.
   * Click **Code.gs** → select all → paste `Code.gs`.
   * **＋ → Script** → name it `SmartFields` → paste `SmartFields.gs`.
   * **＋ → HTML** → name it `Index` → paste `Index.html`.
   * **＋ → HTML** → name it `Styles` → paste `Styles.html`.
   * **＋ → HTML** → name it `App` → paste `App.html`.
   * Press **Ctrl+S** to save.
   (File names must be exactly `SmartFields`, `Index`, `Styles`, `App`. The editor adds `.gs` / `.html` itself.)

4. **Publish it as your private web app.** **Deploy → New deployment** → ⚙ → **Web app**:
   * *Execute as:* **Me**
   * *Who has access:* **Only myself**
   * **Deploy**.

5. **Allow access (one time).** Click **Authorize access** and choose your account.
   * Google shows **"Google hasn't verified this app"**. That's normal for your own private script:
     click **Advanced → Go to Mail Attachment Agent (unsafe)**. It's your own code and runs only for you.
   * Review the list and click **Allow**.

6. **Open the Web app URL** shown after deploying (`https://script.google.com/macros/s/…/exec`).
   **Bookmark it**, or on a phone use *Add to Home screen*. That's your app.

**Updating later:** paste the new file contents, save, then **Deploy → Manage deployments → ✏️ Edit → Version: New version →
Deploy**. The URL stays the same.

### If something blocks you

* **"This app is blocked" / "admin has restricted…"** – your Google Workspace admin must allow Apps Script and
  "unverified" internal apps: **admin.google.com → Security → Access and data control → API controls →
  Manage Third-Party App Access → *Trust internal, domain-owned apps*** ✅.
* **Gmail / Drive "service not enabled"** – step 3: make sure `appsscript.json` was pasted completely. It switches on
  the Gmail and Drive services.
* **ZIP doesn't download** – some browsers block downloads from Google's script sandbox. Use **Save to Drive**
  instead, or open the ZIP from the Drive link the app shows for big ZIPs.

### Optional: set up with the command line (clasp)

```bash
npm install -g @google/clasp
```

```bash
clasp login
```

```bash
cd google-apps-script && clasp create --type webapp --title "Mail Attachment Agent" && clasp push
```

Then do steps 4–6 from the script editor (`clasp open`).

---

## Security

* **Runs as you, inside Google.** No password is typed, sent or stored. Access can be removed any time at
  **myaccount.google.com → Security → Third-party apps → Mail Attachment Agent → Remove access**.
* **Least privilege.** The script asks only for:
  * `gmail.readonly`: read mail. **It cannot delete, move, label or change any email.**
  * `drive.file`: **only the files and folders this app creates**. It cannot see your other Drive files.
  * `script.send_mail`: send only, and only when you press *Email the files*.
  * `spreadsheets` / `documents`: its own data sheet, exports, and the temporary OCR document (deleted right away).
* **Private web app** ("Only myself"). Nobody else can open your URL. Pages are served by Google and can't be
  framed by other sites.
* Attachments are shown safely: HTML as plain text, executable and macro files marked **⚠ risky file**, and
  nothing is ever run.
* **Data → Erase app data** moves the data sheet to the Drive Bin. Your email and saved files are untouched.

Want colleagues to use it with **their own** mailboxes? Deploy a second time with *Execute as: User accessing the web app*
and *Who has access: Anyone within mckkutty.com*. Each person then sees only their own Gmail.

## Developing

* `npm test` runs `Code.gs` + `SmartFields.gs` against fake Gmail / Drive / Sheets (`test/mocks.js`).
* `npm run preview` gives a local preview at http://127.0.0.1:8790 with a demo mailbox (`test/devserver.js`).
* `npm run test:ui` is an end-to-end browser test (needs Playwright; set `PDFJS_DIR` to a local
  `pdfjs-dist@3.11.174/build` to test PDF rendering offline).
