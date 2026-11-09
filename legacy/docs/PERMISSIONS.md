# Roles, modules and permissions

Permissions are rows in `role_permissions` (defaults below) and can be overridden **per project** in `project_role_permissions` by a project administrator. They are enforced by PostgreSQL Row Level Security and inside every workflow function; the app only uses them to hide buttons.

Letters: **C** create · **V** view · **E** edit · **S** submit · **A** approve · **R** reject · **X** export · **D** delete

| Role | project | members | masters | schedule | boq | commercial | dpr | resources | safety | quality | documents | reports |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Organisation administrator | all | all | all | all | all | all | all | all | all | all | all | all |
| Project administrator | all | all | all | all | all | all | all | all | all | all | all | all |
| Project manager | CVEX | CVED | CVEXD | CVESARX | CVESARX | CVESARX | CVESARX | CVEX | CVESARX | CVESARX | CVEXD | CVX |
| Construction manager | V | V | CVEX | VEX | VX | VX | CVESARX | CVEX | CVESARX | CVESARX | CVEX | CVX |
| Planning engineer | V | V | CVEXD | CVESX | VX | V | CVESX | VX | V | V | CVX | CVX |
| Site engineer | V | V | V | V | V | — | CVESX | CVE | CVES | CVES | CV | VX |
| Supervisor | V | V | V | V | — | — | CVES | CVE | CVES | V | CV | V |
| Storekeeper | V | V | V | — | — | — | CVS | CVEX | — | — | CV | V |
| QA/QC engineer | V | V | V | V | — | — | VS | V | V | CVESARX | CVX | VX |
| Safety engineer | V | V | V | V | — | — | VS | V | CVESARX | V | CVX | VX |
| Commercial engineer | V | V | V | V | CVESX | CVESX | VX | V | — | — | VX | VX |
| Client reviewer | V | — | V | V | V | — | VARX | V | V | V | V | VX |
| Integration (API account) | VX | V | CVEX | CVEX | CVEX | VX | VX | VX | VX | VX | VX | VX |
| Read-only auditor | VX | V | VX | VX | VX | VX | VX | VX | VX | VX | VX | VX |

### Special rules

* **Integration** accounts are for other software (ERP, Python/Java services). They sync masters, schedule and billing fields and read the event feed, but can never submit, approve or reject reports or authorise BOQ revisions. See [INTEGRATIONS.md](INTEGRATIONS.md).

* **No self-approval**: a user cannot approve a DPR they prepared or submitted unless the project setting *Allow self-approval* is on (recorded in the audit log). Default: off.
* **Rejection requires a reason.** Revisions require a reason.
* **Project administrators** can only be appointed by project/organisation administrators; `org_admin` is never a project role.
* **Workflow tables** (DPRs, approvals, snapshots, baselines, memberships, folder registry) have **no direct write policy** — only the validated functions can change them.
* **Drive**: database roles do not grant or revoke Google Drive sharing. In-app file viewing goes through the `drive` function, which checks the caller's database permission for that attachment. Direct Drive sharing is a separate, explicit action (Documents → Share).

### Module → screens

| Module | Screens / data |
|---|---|
| project | Project details, settings, export |
| members | Members & roles |
| masters | Areas, WBS, activities, equipment & material registers, industrial work packages |
| schedule | Calendar, dependencies, CPM, baselines |
| boq / commercial | BOQ, measurement / certification / billing, revisions, change orders |
| dpr | DPR wizard, approvals, constraints, action items |
| resources | Manpower, equipment and material views |
| safety / quality | Safety register, inspections, NCRs, commissioning |
| documents | Attachments, Drive folders and sharing |
| reports | Report PDFs, audit log |
