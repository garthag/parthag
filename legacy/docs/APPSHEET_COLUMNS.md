# AppSheet column settings (generated from the schema — do not edit)

For each table: in AppSheet open **Data → Tables → <table> → View columns** and set the columns as below.
**script** columns are calculated by Apps Script: untick *Editable* (or set *Editable_If* = FALSE) and *Show* where noted.

## Users

Who may use the app, their role and projects ("ALL" or comma-separated ProjectIDs).

| Column | Type | Filled by | Settings |
|---|---|---|---|
| Email | Email | user | **Key** |
| Name | Name | user |  |
| Role | Enum | user | Values: Admin, Project Manager, Planning Engineer, Site Engineer, Supervisor, Storekeeper, QA/QC Engineer, Safety Engineer, Commercial Engineer, Client Reviewer, Auditor |
| Projects | Text | user | ALL or ProjectID,ProjectID |
| Active | Yes/No | user |  |

## Projects

Project master.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| ProjectID | Text | app | **Key** · Initial value: UNIQUEID() |
| Code | Text | user |  |
| Name | Text | user |  |
| Client | Text | user |  |
| MainContractor | Text | user |  |
| WorkOrderNo | Text | user |  |
| SiteAddress | Address | user |  |
| StartDate | Date | user |  |
| CompletionDate | Date | user |  |
| Currency | Text | user | Initial value: "INR" |
| Timezone | Text | user | Initial value: "Asia/Kolkata" |
| AllowSelfApproval | Yes/No | user |  |
| Status | Enum | user | Values: Active, On hold, Closed |
| DriveFolderID | Text | script | Editable: off · Project folder tree in Drive |
| DriveFolderURL | Url | script | Editable: off · Link to the project folder |

## Areas

Sites, areas, units and systems.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| AreaID | Text | app | **Key** · Initial value: UNIQUEID() |
| ProjectID | Ref | user | Source table: Projects |
| Code | Text | user |  |
| Name | Text | user |  |
| Kind | Enum | user | Values: Area, Unit, System |

## WBS

Work breakdown structure.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| WBSID | Text | app | **Key** · Initial value: UNIQUEID() |
| ProjectID | Ref | user | Source table: Projects |
| Code | Text | user |  |
| Name | Text | user |  |
| Weight | Decimal | user |  |

## Contractors

Contractors and agencies.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| ContractorID | Text | app | **Key** · Initial value: UNIQUEID() |
| Name | Text | user |  |
| Kind | Enum | user | Values: Main contractor, Subcontractor, Supplier, Labour agency |

## BOQ

Bill of quantities. Quantity/rate changes go through BOQRevisions.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| BOQID | Text | app | **Key** · Initial value: UNIQUEID() |
| ProjectID | Ref | user | Source table: Projects |
| ItemNo | Text | user |  |
| Description | LongText | user |  |
| Unit | Text | user |  |
| TenderQty | Decimal | user |  |
| RevisedQty | Decimal | user | Set only by approved BOQ revisions |
| Rate | Price | user |  |
| MeasuredQty | Decimal | user |  |
| CertifiedQty | Decimal | user |  |
| BilledQty | Decimal | user | Valid_If: [_THIS] <= [CertifiedQty] |
| ExecutedQty | Decimal | script | Editable: off · Accepted qty from approved DPRs |
| ContractAmount | Price | script | Editable: off · Approved qty × rate |
| ExecutedAmount | Price | script | Editable: off |
| BilledAmount | Price | script | Editable: off |

## BOQRevisions

Authorised BOQ quantity/rate revisions (append-only).

| Column | Type | Filled by | Settings |
|---|---|---|---|
| RevisionID | Text | app | **Key** · Initial value: UNIQUEID() |
| BOQID | Ref | user | Source table: BOQ |
| NewQty | Decimal | user |  |
| NewRate | Price | user |  |
| Reason | LongText | user |  |
| RequestedBy | Email | app | Initial value: USEREMAIL() |
| Status | Enum | user | Values: Requested, Approved, Rejected |
| ApprovedBy | Email | app |  |
| OldQty | Decimal | script | Editable: off |
| OldRate | Price | script | Editable: off |
| AppliedAt | DateTime | script | Editable: off |

## Activities

Activity register with plan, baseline, actuals and CPM results.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| ActivityID | Text | app | **Key** · Initial value: UNIQUEID() |
| ProjectID | Ref | user | Source table: Projects |
| WBSID | Ref | user | Source table: WBS |
| AreaID | Ref | user | Source table: Areas |
| BOQID | Ref | user | Source table: BOQ |
| ContractorID | Ref | user | Source table: Contractors |
| Code | Text | user |  |
| Name | Text | user |  |
| Location | Text | user |  |
| Unit | Text | user |  |
| TotalQty | Decimal | user | Approved total quantity |
| Weight | Decimal | user |  |
| Duration | Number | user | Working days |
| IsMilestone | Yes/No | user |  |
| PlannedStart | Date | user |  |
| PlannedFinish | Date | user |  |
| BaselineStart | Date | script | Editable: off · Frozen by "Freeze baseline" |
| BaselineFinish | Date | script | Editable: off |
| ActualStart | Date | script | Editable: off · From approved DPRs |
| ActualFinish | Date | script | Editable: off |
| AcceptedCum | Decimal | script | Editable: off |
| ProgressPct | Percent | script | Editable: off |
| EarlyStart | Date | script | Editable: off · CPM |
| EarlyFinish | Date | script | Editable: off · CPM |
| TotalFloat | Number | script | Editable: off · CPM, working days |
| Critical | Yes/No | script | Editable: off · CPM |
| Status | Enum | user | Values: Not started, In progress, Completed, Cancelled |

## Dependencies

Schedule logic (FS/SS/FF/SF with lag; negative lag = lead).

| Column | Type | Filled by | Settings |
|---|---|---|---|
| DepID | Text | app | **Key** · Initial value: UNIQUEID() |
| ProjectID | Ref | user | Source table: Projects |
| PredecessorID | Ref | user | Source table: Activities |
| SuccessorID | Ref | user | Source table: Activities |
| Type | Enum | user | Values: FS, SS, FF, SF |
| Lag | Number | user |  |

## Holidays

Non-working dates per project (Sundays are off by default; see Settings WORK_DAYS).

| Column | Type | Filled by | Settings |
|---|---|---|---|
| HolidayID | Text | app | **Key** · Initial value: UNIQUEID() |
| ProjectID | Ref | user | Source table: Projects |
| Date | Date | user |  |
| Name | Text | user |  |

## Equipment

Plant and equipment register.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| EquipmentID | Text | app | **Key** · Initial value: UNIQUEID() |
| ProjectID | Ref | user | Source table: Projects |
| Code | Text | user |  |
| Type | Text | user |  |
| Capacity | Text | user |  |
| CertExpiry | Date | user |  |

## Materials

Material master with current stock (from approved DPRs).

| Column | Type | Filled by | Settings |
|---|---|---|---|
| MaterialID | Text | app | **Key** · Initial value: UNIQUEID() |
| ProjectID | Ref | user | Source table: Projects |
| Code | Text | user |  |
| Description | Text | user |  |
| Grade | Text | user |  |
| Unit | Text | user |  |
| ReorderLevel | Decimal | user |  |
| Stock | Decimal | script | Editable: off · From approved DPRs |
| BelowReorder | Yes/No | script | Editable: off |

## DPR

Daily progress report header and narrative.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| DPRID | Text | app | **Key** · Initial value: UNIQUEID() |
| ProjectID | Ref | user | Source table: Projects |
| DPRNo | Text | script | Editable: off · Generated on first submit |
| Revision | Number | app | Initial value: 0 |
| PreviousDPRID | Ref | script | Source table: DPR · Editable: off |
| RevisionReason | LongText | user |  |
| ReportDate | Date | user | Initial value: TODAY() |
| Shift | Enum | user | Values: Day, Night, General |
| AreaID | Ref | user | Source table: Areas |
| Weather | Text | user |  |
| SiteConditions | Text | user |  |
| PreparedBy | Email | app | Initial value: USEREMAIL() |
| Status | Enum | app | Values: Draft, Submitted, Approved, Rejected, Revision requested · Initial value: "Draft"; changed only by actions |
| ReviewedBy | Email | app |  |
| ReviewComment | LongText | user |  |
| ToolboxTalks | Number | user |  |
| PermitsIssued | Number | user |  |
| WorkCompleted | LongText | user |  |
| Achievements | LongText | user |  |
| WorkNotCompleted | LongText | user |  |
| DelayReasons | LongText | user |  |
| ConstraintsText | LongText | user |  |
| DecisionsRequired | LongText | user |  |
| TomorrowPlan | LongText | user |  |
| ResourcesRequired | LongText | user |  |
| SubmittedAt | DateTime | script | Editable: off |
| ApprovedAt | DateTime | script | Editable: off |
| ProcessedStatus | Text | script | Editable: off · Internal: last status handled by the script |
| Issues | LongText | script | Editable: off · Validation messages from the script |
| PdfURL | Url | script | Editable: off · Approved PDF in Drive |
| SnapshotHash | Text | script | Editable: off · SHA-256 of the approved snapshot |

## DPR_Activities

Activity progress lines of a DPR.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| LineID | Text | app | **Key** · Initial value: UNIQUEID() |
| DPRID | Ref | user | Source table: DPR · **Is a part of** ✓ |
| ProjectID | Ref | app | Source table: Projects · Initial value: [DPRID].[ProjectID] |
| ActivityID | Ref | user | Source table: Activities |
| QtyToday | Decimal | user |  |
| QtyAccepted | Decimal | user | Set by approver; blank = QtyToday |
| Location | Text | user |  |
| DelayReason | Text | user |  |
| OverrunAuthorised | Yes/No | user |  |
| OverrunReason | Text | user |  |
| NextDayTarget | Decimal | user |  |
| Remarks | LongText | user |  |
| Photo | Image | user |  |
| PrevCum | Decimal | script | Editable: off |
| CumQty | Decimal | script | Editable: off |
| Balance | Decimal | script | Editable: off |
| ActualPct | Percent | script | Editable: off |
| PlannedPct | Percent | script | Editable: off |
| Variance | Decimal | script | Editable: off · Percentage points |

## Manpower

Manpower lines of a DPR.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| LineID | Text | app | **Key** · Initial value: UNIQUEID() |
| DPRID | Ref | user | Source table: DPR · **Is a part of** ✓ |
| ProjectID | Ref | app | Source table: Projects · Initial value: [DPRID].[ProjectID] |
| ContractorID | Ref | user | Source table: Contractors |
| Trade | Text | user |  |
| Skilled | Number | user |  |
| SemiSkilled | Number | user |  |
| Unskilled | Number | user |  |
| Supervisors | Number | user |  |
| Engineers | Number | user |  |
| SafetyStaff | Number | user |  |
| Others | Number | user |  |
| Absent | Number | user |  |
| HoursPerPerson | Decimal | user | Initial value: 8 |
| OvertimeHours | Decimal | user |  |
| Headcount | Number | script | Editable: off |
| ManHours | Decimal | script | Editable: off |

## EquipmentUse

Equipment deployment lines of a DPR.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| LineID | Text | app | **Key** · Initial value: UNIQUEID() |
| DPRID | Ref | user | Source table: DPR · **Is a part of** ✓ |
| ProjectID | Ref | app | Source table: Projects · Initial value: [DPRID].[ProjectID] |
| EquipmentID | Ref | user | Source table: Equipment |
| Operator | Text | user |  |
| WorkingHours | Decimal | user |  |
| IdleHours | Decimal | user |  |
| BreakdownHours | Decimal | user |  |
| BreakdownReason | Text | user | Required_If: [BreakdownHours] > 0 |
| Fuel | Decimal | user |  |
| UtilisationPct | Percent | script | Editable: off |

## MaterialMoves

Material receipts, issues and consumption in a DPR.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| LineID | Text | app | **Key** · Initial value: UNIQUEID() |
| DPRID | Ref | user | Source table: DPR · **Is a part of** ✓ |
| ProjectID | Ref | app | Source table: Projects · Initial value: [DPRID].[ProjectID] |
| MaterialID | Ref | user | Source table: Materials |
| Received | Decimal | user |  |
| Rejected | Decimal | user |  |
| Issued | Decimal | user |  |
| Consumed | Decimal | user |  |
| Returned | Decimal | user |  |
| ChallanNo | Text | user |  |
| HeatBatchNo | Text | user |  |
| Inspection | Enum | user | Values: Pending, Accepted, Rejected, Not required |
| Opening | Decimal | script | Editable: off |
| Closing | Decimal | script | Editable: off |

## Safety

Safety observations, permits and incidents.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| ObsID | Text | app | **Key** · Initial value: UNIQUEID() |
| DPRID | Ref | user | Source table: DPR |
| ProjectID | Ref | user | Source table: Projects |
| Category | Enum | user | Values: Permit, Lifting plan, Work at height, Unsafe condition, Unsafe act, Near miss, Incident, First aid, Good practice |
| PermitType | Enum | user | Values: Hot work, Work at height, Lifting, Confined space, Excavation, Electrical isolation, General |
| Description | LongText | user |  |
| Severity | Enum | user | Values: Low, Medium, High, Critical |
| Action | LongText | user |  |
| Responsible | Text | user |  |
| TargetDate | Date | user |  |
| Status | Enum | user | Values: Open, In progress, Closed |
| Photo | Image | user |  |

## Quality

Inspections, hold points, NCRs and observations.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| QID | Text | app | **Key** · Initial value: UNIQUEID() |
| DPRID | Ref | user | Source table: DPR |
| ProjectID | Ref | user | Source table: Projects |
| ActivityID | Ref | user | Source table: Activities |
| Kind | Enum | user | Values: Inspection, NCR, Observation |
| RefNo | Text | user |  |
| Description | LongText | user |  |
| HoldPoint | Yes/No | user |  |
| Outcome | Enum | user | Values: Pending, Accepted, Accepted with comments, Rejected |
| Responsible | Text | user |  |
| TargetDate | Date | user |  |
| Status | Enum | user | Values: Open, In progress, Closed |
| Photo | Image | user |  |

## Constraints

Constraints and required actions.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| ConstraintID | Text | app | **Key** · Initial value: UNIQUEID() |
| DPRID | Ref | user | Source table: DPR |
| ProjectID | Ref | user | Source table: Projects |
| ActivityID | Ref | user | Source table: Activities |
| Category | Enum | user | Values: Material, Equipment, Labour, Design, Client, Weather, Permit, Other |
| Description | LongText | user |  |
| Responsible | Text | user |  |
| TargetDate | Date | user |  |
| Status | Enum | user | Values: Open, In progress, Closed |

## Evidence

Photos, videos, voice notes and documents (files are stored in Google Drive by AppSheet).

| Column | Type | Filled by | Settings |
|---|---|---|---|
| EvidenceID | Text | app | **Key** · Initial value: UNIQUEID() |
| DPRID | Ref | user | Source table: DPR · **Is a part of** ✓ |
| ProjectID | Ref | app | Source table: Projects · Initial value: [DPRID].[ProjectID] |
| ActivityID | Ref | user | Source table: Activities |
| Type | Enum | user | Values: Photo, Video, Voice note, Document, Drawing |
| Photo | Image | user | Show_If: [Type] = "Photo" |
| File | File | user | Show_If: [Type] <> "Photo" |
| Caption | Text | user |  |
| Location | LatLong | user | Optional; user taps to fill |
| CapturedAt | DateTime | app | Initial value: NOW() |
| CapturedBy | Email | app | Initial value: USEREMAIL() |
| DriveFileID | Text | script | Editable: off · Resolved Drive file |
| SHA256 | Text | script | Editable: off · Checksum at approval |

## QuickEntry

Voice / typed site statements converted into a proposed entry for confirmation.

| Column | Type | Filled by | Settings |
|---|---|---|---|
| EntryID | Text | app | **Key** · Initial value: UNIQUEID() |
| DPRID | Ref | user | Source table: DPR · **Is a part of** ✓ |
| ProjectID | Ref | app | Source table: Projects · Initial value: [DPRID].[ProjectID] |
| Statement | LongText | user | Use the phone keyboard microphone to dictate |
| Status | Enum | app | Values: New, Proposed, Confirmed, Applied, Discarded · Initial value: "New" |
| ProposedActivityID | Ref | script | Source table: Activities · Editable: off · Editable by the user before confirming |
| ProposedQty | Decimal | script | Editable: off |
| ProposedUnit | Text | script | Editable: off |
| ProposedWorkers | Number | script | Editable: off |
| ProposedTrade | Text | user |  |
| ProposedEquipment | Text | script | Editable: off |
| Uncertain | Text | script | Editable: off · Fields to double-check |

## Approvals

Approval history (append-only). **Add as read-only** (Are updates allowed? → Read-only).

| Column | Type | Filled by | Settings |
|---|---|---|---|
| LogID | Text | app | **Key** · Initial value: UNIQUEID() |
| DPRID | Ref | script | Source table: DPR · Editable: off |
| Action | Text | script | Editable: off |
| By | Email | script | Editable: off |
| At | DateTime | script | Editable: off |
| FromStatus | Text | script | Editable: off |
| ToStatus | Text | script | Editable: off |
| Comment | LongText | script | Editable: off |

## Snapshots

Immutable record of each approved DPR revision (JSON + PDF in Drive, SHA-256). **Add as read-only** (Are updates allowed? → Read-only).

| Column | Type | Filled by | Settings |
|---|---|---|---|
| SnapshotID | Text | app | **Key** · Initial value: UNIQUEID() |
| DPRID | Ref | script | Source table: DPR · Editable: off |
| DPRNo | Text | script | Editable: off |
| Revision | Number | script | Editable: off |
| ApprovedAt | DateTime | script | Editable: off |
| ApprovedBy | Email | script | Editable: off |
| JsonFileID | Text | script | Editable: off |
| PdfFileID | Text | script | Editable: off |
| SHA256 | Text | script | Editable: off |
| IntegrityOK | Yes/No | script | Editable: off · Re-checked nightly |

## Baselines

Frozen baseline revisions (append-only). **Add as read-only** (Are updates allowed? → Read-only).

| Column | Type | Filled by | Settings |
|---|---|---|---|
| BaselineRowID | Text | app | **Key** · Initial value: UNIQUEID() |
| ProjectID | Ref | user | Source table: Projects |
| Revision | Number | script | Editable: off |
| FrozenAt | DateTime | script | Editable: off |
| ActivityID | Ref | script | Source table: Activities · Editable: off |
| Start | Date | script | Editable: off |
| Finish | Date | script | Editable: off |
| TotalQty | Decimal | script | Editable: off |

## Audit

Script audit trail (append-only). **Add as read-only** (Are updates allowed? → Read-only).

| Column | Type | Filled by | Settings |
|---|---|---|---|
| At | DateTime | script | Editable: off |
| User | Email | script | Editable: off |
| Table | Text | script | Editable: off |
| RowID | Text | script | Editable: off |
| Action | Text | script | Editable: off |
| Details | LongText | script | Editable: off |

