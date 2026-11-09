# Roles and permissions

Each person has one role per project. The server checks the role on **every** request (`LocalApi`, see `src/ConstructDpr.Shared/Security/Permissions.cs`); the apps only use it to hide buttons.

Letters: **C** create · **V** view · **E** edit · **S** submit · **A** approve · **R** reject · **X** export · **D** delete. `all` = every action.

| Role | project | members | masters (activities) | schedule | BOQ | DPR | reports |
|---|---|---|---|---|---|---|---|
| Project administrator | all | all | all | all | all | all | all |
| Project manager | CVEX | CVED | CVEXD | CVESARX | CVESARX | CVESARX | CVX |
| Construction manager | V | V | CVEX | VEX | VX | CVESARX | CVX |
| Planning engineer | V | V | CVEXD | CVESX | VX | CVESX | CVX |
| Site engineer | V | V | V | V | V | CVESX | VX |
| Supervisor | V | V | V | V | — | CVES | V |
| Storekeeper | V | V | V | — | — | CVS | V |
| QA/QC engineer | V | V | V | V | — | VS | VX |
| Safety engineer | V | V | V | V | — | VS | VX |
| Commercial engineer | V | V | V | V | CVESX | VX | VX |
| Client reviewer | V | — | V | V | V | VARX | VX |
| Integration (API account) | VX | V | CVEX | CVEX | CVEX | VX | VX |
| Read-only auditor | VX | V | VX | VX | VX | VX | VX |

## Special rules

* **No self-approval.** Nobody approves a report they prepared, unless the project setting *Allow self-approval* is on.
* **Reasons are required** for rejections and revisions. Approved reports are never edited; corrections are new revisions.
* **Own drafts.** Roles that may create reports but not edit them (Supervisor, Storekeeper) can change only drafts they prepared themselves.
* **BOQ.** Changing quantities or rates needs BOQ edit (or masters delete) rights. A BOQ quantity can never go below the quantity already approved.
* **Administrators.** Only a project administrator can appoint another one, and a project always keeps at least one.
* **Non-members** cannot see that a project exists: the server answers "not found".
* **Offline Windows app.** The single user of "This PC only" mode is the administrator of every project on that PC.
