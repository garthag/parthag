# CONSTRUCT DPR — Google Workspace edition

Google Sheets (data) + AppSheet (phone app) + Apps Script (rules, PDFs, scheduling) + Drive (files) + Looker Studio (dashboards).

* **Users:** follow [docs/GOOGLE_WORKSPACE_EDITION.md](../docs/GOOGLE_WORKSPACE_EDITION.md). You only need `dist/Code.gs` and `dist/appsscript.json`.
* **AppSheet column settings:** [docs/APPSHEET_COLUMNS.md](../docs/APPSHEET_COLUMNS.md) (generated).
* **Developers:** edit `src/gas/*.js`, then `npm test` (rebuilds `dist/` and the AppSheet guide, runs the end-to-end tests against in-memory Google service fakes in `test/fakes.mjs`). The CPM engine, S-curve and voice parser are bundled from `web/src/lib`, so both editions share one tested implementation.
