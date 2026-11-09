/**
 * CONSTRUCT DPR — Google Workspace edition.
 * Single source of truth for every sheet (table). Used by setup() to create
 * the spreadsheet and by build.mjs to generate the AppSheet column guide.
 *
 * Column fields:
 *   n  header name          t  AppSheet type          by  who fills it
 *   ref  referenced table   enum  allowed values      note  guidance
 * by: 'user' (entered in the app), 'app' (AppSheet initial value / action),
 *     'script' (calculated by Apps Script — make read-only in AppSheet).
 */
var DPR_STATUSES = ['Draft', 'Submitted', 'Approved', 'Rejected', 'Revision requested'];
var ROLES = ['Admin', 'Project Manager', 'Planning Engineer', 'Site Engineer', 'Supervisor', 'Storekeeper',
  'QA/QC Engineer', 'Safety Engineer', 'Commercial Engineer', 'Client Reviewer', 'Auditor'];
var APPROVER_ROLES = ['Admin', 'Project Manager', 'Client Reviewer'];

function c_(n, t, by, extra) {
  var col = { n: n, t: t, by: by || 'user' };
  for (var k in extra || {}) col[k] = extra[k];
  return col;
}
var KEY = function (n) { return c_(n, 'Text', 'app', { key: true, note: 'Initial value: UNIQUEID()' }); };
var REF = function (n, table, extra) { return c_(n, 'Ref', 'user', Object.assign({ ref: table }, extra || {})); };
var NUM = function (n, extra) { return c_(n, 'Decimal', 'user', extra); };
var INT = function (n, extra) { return c_(n, 'Number', 'user', extra); };
var SCRIPT = function (n, t, note) { return c_(n, t, 'script', { note: note }); };

var SCHEMA = {
  Settings: {
    purpose: 'Key/value settings (folder IDs, options). Managed by the script.',
    appsheet: false,
    cols: [c_('Key', 'Text', 'script', { key: true }), c_('Value', 'Text', 'script'), c_('Notes', 'LongText', 'script')],
  },
  Users: {
    purpose: 'Who may use the app, their role and projects ("ALL" or comma-separated ProjectIDs).',
    cols: [c_('Email', 'Email', 'user', { key: true }), c_('Name', 'Name'), c_('Role', 'Enum', 'user', { enum: ROLES }),
      c_('Projects', 'Text', 'user', { note: 'ALL or ProjectID,ProjectID' }), c_('Active', 'Yes/No')],
  },
  Projects: {
    purpose: 'Project master.',
    cols: [KEY('ProjectID'), c_('Code', 'Text'), c_('Name', 'Text'), c_('Client', 'Text'), c_('MainContractor', 'Text'),
      c_('WorkOrderNo', 'Text'), c_('SiteAddress', 'Address'), c_('StartDate', 'Date'), c_('CompletionDate', 'Date'),
      c_('Currency', 'Text', 'user', { note: 'Initial value: "INR"' }), c_('Timezone', 'Text', 'user', { note: 'Initial value: "Asia/Kolkata"' }),
      c_('AllowSelfApproval', 'Yes/No'), c_('Status', 'Enum', 'user', { enum: ['Active', 'On hold', 'Closed'] }),
      SCRIPT('DriveFolderID', 'Text', 'Project folder tree in Drive'), SCRIPT('DriveFolderURL', 'Url', 'Link to the project folder')],
  },
  Areas: {
    purpose: 'Sites, areas, units and systems.',
    cols: [KEY('AreaID'), REF('ProjectID', 'Projects'), c_('Code', 'Text'), c_('Name', 'Text'), c_('Kind', 'Enum', 'user', { enum: ['Area', 'Unit', 'System'] })],
  },
  WBS: {
    purpose: 'Work breakdown structure.',
    cols: [KEY('WBSID'), REF('ProjectID', 'Projects'), c_('Code', 'Text'), c_('Name', 'Text'), NUM('Weight')],
  },
  Contractors: {
    purpose: 'Contractors and agencies.',
    cols: [KEY('ContractorID'), c_('Name', 'Text'), c_('Kind', 'Enum', 'user', { enum: ['Main contractor', 'Subcontractor', 'Supplier', 'Labour agency'] })],
  },
  BOQ: {
    purpose: 'Bill of quantities. Quantity/rate changes go through BOQRevisions.',
    cols: [KEY('BOQID'), REF('ProjectID', 'Projects'), c_('ItemNo', 'Text'), c_('Description', 'LongText'), c_('Unit', 'Text'),
      NUM('TenderQty'), NUM('RevisedQty', { note: 'Set only by approved BOQ revisions' }), c_('Rate', 'Price'),
      NUM('MeasuredQty'), NUM('CertifiedQty'), NUM('BilledQty', { note: 'Valid_If: [_THIS] <= [CertifiedQty]' }),
      SCRIPT('ExecutedQty', 'Decimal', 'Accepted qty from approved DPRs'), SCRIPT('ContractAmount', 'Price', 'Approved qty × rate'),
      SCRIPT('ExecutedAmount', 'Price', ''), SCRIPT('BilledAmount', 'Price', '')],
  },
  BOQRevisions: {
    purpose: 'Authorised BOQ quantity/rate revisions (append-only).',
    cols: [KEY('RevisionID'), REF('BOQID', 'BOQ'), c_('NewQty', 'Decimal'), c_('NewRate', 'Price'), c_('Reason', 'LongText'),
      c_('RequestedBy', 'Email', 'app', { note: 'Initial value: USEREMAIL()' }), c_('Status', 'Enum', 'user', { enum: ['Requested', 'Approved', 'Rejected'] }),
      c_('ApprovedBy', 'Email', 'app'), SCRIPT('OldQty', 'Decimal', ''), SCRIPT('OldRate', 'Price', ''), SCRIPT('AppliedAt', 'DateTime', '')],
  },
  Activities: {
    purpose: 'Activity register with plan, baseline, actuals and CPM results.',
    cols: [KEY('ActivityID'), REF('ProjectID', 'Projects'), REF('WBSID', 'WBS'), REF('AreaID', 'Areas'), REF('BOQID', 'BOQ'),
      REF('ContractorID', 'Contractors'), c_('Code', 'Text'), c_('Name', 'Text'), c_('Location', 'Text'), c_('Unit', 'Text'),
      NUM('TotalQty', { note: 'Approved total quantity' }), NUM('Weight'), INT('Duration', { note: 'Working days' }), c_('IsMilestone', 'Yes/No'),
      c_('PlannedStart', 'Date'), c_('PlannedFinish', 'Date'), SCRIPT('BaselineStart', 'Date', 'Frozen by "Freeze baseline"'), SCRIPT('BaselineFinish', 'Date', ''),
      SCRIPT('ActualStart', 'Date', 'From approved DPRs'), SCRIPT('ActualFinish', 'Date', ''), SCRIPT('AcceptedCum', 'Decimal', ''),
      SCRIPT('ProgressPct', 'Percent', ''), SCRIPT('EarlyStart', 'Date', 'CPM'), SCRIPT('EarlyFinish', 'Date', 'CPM'),
      SCRIPT('TotalFloat', 'Number', 'CPM, working days'), SCRIPT('Critical', 'Yes/No', 'CPM'),
      c_('Status', 'Enum', 'user', { enum: ['Not started', 'In progress', 'Completed', 'Cancelled'] })],
  },
  Dependencies: {
    purpose: 'Schedule logic (FS/SS/FF/SF with lag; negative lag = lead).',
    cols: [KEY('DepID'), REF('ProjectID', 'Projects'), REF('PredecessorID', 'Activities'), REF('SuccessorID', 'Activities'),
      c_('Type', 'Enum', 'user', { enum: ['FS', 'SS', 'FF', 'SF'] }), INT('Lag')],
  },
  Holidays: {
    purpose: 'Non-working dates per project (Sundays are off by default; see Settings WORK_DAYS).',
    cols: [KEY('HolidayID'), REF('ProjectID', 'Projects'), c_('Date', 'Date'), c_('Name', 'Text')],
  },
  Equipment: {
    purpose: 'Plant and equipment register.',
    cols: [KEY('EquipmentID'), REF('ProjectID', 'Projects'), c_('Code', 'Text'), c_('Type', 'Text'), c_('Capacity', 'Text'), c_('CertExpiry', 'Date')],
  },
  Materials: {
    purpose: 'Material master with current stock (from approved DPRs).',
    cols: [KEY('MaterialID'), REF('ProjectID', 'Projects'), c_('Code', 'Text'), c_('Description', 'Text'), c_('Grade', 'Text'),
      c_('Unit', 'Text'), NUM('ReorderLevel'), SCRIPT('Stock', 'Decimal', 'From approved DPRs'), SCRIPT('BelowReorder', 'Yes/No', '')],
  },
  DPR: {
    purpose: 'Daily progress report header and narrative.',
    cols: [KEY('DPRID'), REF('ProjectID', 'Projects'), SCRIPT('DPRNo', 'Text', 'Generated on first submit'),
      c_('Revision', 'Number', 'app', { note: 'Initial value: 0' }), c_('PreviousDPRID', 'Ref', 'script', { ref: 'DPR' }),
      c_('RevisionReason', 'LongText'), c_('ReportDate', 'Date', 'user', { note: 'Initial value: TODAY()' }),
      c_('Shift', 'Enum', 'user', { enum: ['Day', 'Night', 'General'] }), REF('AreaID', 'Areas'),
      c_('Weather', 'Text'), c_('SiteConditions', 'Text'), c_('PreparedBy', 'Email', 'app', { note: 'Initial value: USEREMAIL()' }),
      c_('Status', 'Enum', 'app', { enum: DPR_STATUSES, note: 'Initial value: "Draft"; changed only by actions' }),
      c_('ReviewedBy', 'Email', 'app'), c_('ReviewComment', 'LongText'),
      INT('ToolboxTalks'), INT('PermitsIssued'), c_('WorkCompleted', 'LongText'), c_('Achievements', 'LongText'),
      c_('WorkNotCompleted', 'LongText'), c_('DelayReasons', 'LongText'), c_('ConstraintsText', 'LongText'),
      c_('DecisionsRequired', 'LongText'), c_('TomorrowPlan', 'LongText'), c_('ResourcesRequired', 'LongText'),
      SCRIPT('SubmittedAt', 'DateTime', ''), SCRIPT('ApprovedAt', 'DateTime', ''), SCRIPT('ProcessedStatus', 'Text', 'Internal: last status handled by the script'),
      SCRIPT('Issues', 'LongText', 'Validation messages from the script'), SCRIPT('PdfURL', 'Url', 'Approved PDF in Drive'),
      SCRIPT('SnapshotHash', 'Text', 'SHA-256 of the approved snapshot')],
  },
  DPR_Activities: {
    purpose: 'Activity progress lines of a DPR.',
    cols: [KEY('LineID'), c_('DPRID', 'Ref', 'user', { ref: 'DPR', isPartOf: true }), c_('ProjectID', 'Ref', 'app', { ref: 'Projects', note: 'Initial value: [DPRID].[ProjectID]' }),
      REF('ActivityID', 'Activities'), NUM('QtyToday'), NUM('QtyAccepted', { note: 'Set by approver; blank = QtyToday' }),
      c_('Location', 'Text'), c_('DelayReason', 'Text'), c_('OverrunAuthorised', 'Yes/No'), c_('OverrunReason', 'Text'),
      NUM('NextDayTarget'), c_('Remarks', 'LongText'), c_('Photo', 'Image'),
      SCRIPT('PrevCum', 'Decimal', ''), SCRIPT('CumQty', 'Decimal', ''), SCRIPT('Balance', 'Decimal', ''),
      SCRIPT('ActualPct', 'Percent', ''), SCRIPT('PlannedPct', 'Percent', ''), SCRIPT('Variance', 'Decimal', 'Percentage points')],
  },
  Manpower: {
    purpose: 'Manpower lines of a DPR.',
    cols: [KEY('LineID'), c_('DPRID', 'Ref', 'user', { ref: 'DPR', isPartOf: true }), c_('ProjectID', 'Ref', 'app', { ref: 'Projects', note: 'Initial value: [DPRID].[ProjectID]' }),
      REF('ContractorID', 'Contractors'), c_('Trade', 'Text'), INT('Skilled'), INT('SemiSkilled'), INT('Unskilled'),
      INT('Supervisors'), INT('Engineers'), INT('SafetyStaff'), INT('Others'), INT('Absent'), NUM('HoursPerPerson', { note: 'Initial value: 8' }),
      NUM('OvertimeHours'), SCRIPT('Headcount', 'Number', ''), SCRIPT('ManHours', 'Decimal', '')],
  },
  EquipmentUse: {
    purpose: 'Equipment deployment lines of a DPR.',
    cols: [KEY('LineID'), c_('DPRID', 'Ref', 'user', { ref: 'DPR', isPartOf: true }), c_('ProjectID', 'Ref', 'app', { ref: 'Projects', note: 'Initial value: [DPRID].[ProjectID]' }),
      REF('EquipmentID', 'Equipment'), c_('Operator', 'Text'), NUM('WorkingHours'), NUM('IdleHours'), NUM('BreakdownHours'),
      c_('BreakdownReason', 'Text', 'user', { note: 'Required_If: [BreakdownHours] > 0' }), NUM('Fuel'), SCRIPT('UtilisationPct', 'Percent', '')],
  },
  MaterialMoves: {
    purpose: 'Material receipts, issues and consumption in a DPR.',
    cols: [KEY('LineID'), c_('DPRID', 'Ref', 'user', { ref: 'DPR', isPartOf: true }), c_('ProjectID', 'Ref', 'app', { ref: 'Projects', note: 'Initial value: [DPRID].[ProjectID]' }),
      REF('MaterialID', 'Materials'), NUM('Received'), NUM('Rejected'), NUM('Issued'), NUM('Consumed'), NUM('Returned'),
      c_('ChallanNo', 'Text'), c_('HeatBatchNo', 'Text'), c_('Inspection', 'Enum', 'user', { enum: ['Pending', 'Accepted', 'Rejected', 'Not required'] }),
      SCRIPT('Opening', 'Decimal', ''), SCRIPT('Closing', 'Decimal', '')],
  },
  Safety: {
    purpose: 'Safety observations, permits and incidents.',
    cols: [KEY('ObsID'), c_('DPRID', 'Ref', 'user', { ref: 'DPR' }), REF('ProjectID', 'Projects'),
      c_('Category', 'Enum', 'user', { enum: ['Permit', 'Lifting plan', 'Work at height', 'Unsafe condition', 'Unsafe act', 'Near miss', 'Incident', 'First aid', 'Good practice'] }),
      c_('PermitType', 'Enum', 'user', { enum: ['Hot work', 'Work at height', 'Lifting', 'Confined space', 'Excavation', 'Electrical isolation', 'General'] }),
      c_('Description', 'LongText'), c_('Severity', 'Enum', 'user', { enum: ['Low', 'Medium', 'High', 'Critical'] }), c_('Action', 'LongText'),
      c_('Responsible', 'Text'), c_('TargetDate', 'Date'), c_('Status', 'Enum', 'user', { enum: ['Open', 'In progress', 'Closed'] }), c_('Photo', 'Image')],
  },
  Quality: {
    purpose: 'Inspections, hold points, NCRs and observations.',
    cols: [KEY('QID'), c_('DPRID', 'Ref', 'user', { ref: 'DPR' }), REF('ProjectID', 'Projects'), REF('ActivityID', 'Activities'),
      c_('Kind', 'Enum', 'user', { enum: ['Inspection', 'NCR', 'Observation'] }), c_('RefNo', 'Text'), c_('Description', 'LongText'),
      c_('HoldPoint', 'Yes/No'), c_('Outcome', 'Enum', 'user', { enum: ['Pending', 'Accepted', 'Accepted with comments', 'Rejected'] }),
      c_('Responsible', 'Text'), c_('TargetDate', 'Date'), c_('Status', 'Enum', 'user', { enum: ['Open', 'In progress', 'Closed'] }), c_('Photo', 'Image')],
  },
  Constraints: {
    purpose: 'Constraints and required actions.',
    cols: [KEY('ConstraintID'), c_('DPRID', 'Ref', 'user', { ref: 'DPR' }), REF('ProjectID', 'Projects'), REF('ActivityID', 'Activities'),
      c_('Category', 'Enum', 'user', { enum: ['Material', 'Equipment', 'Labour', 'Design', 'Client', 'Weather', 'Permit', 'Other'] }),
      c_('Description', 'LongText'), c_('Responsible', 'Text'), c_('TargetDate', 'Date'), c_('Status', 'Enum', 'user', { enum: ['Open', 'In progress', 'Closed'] })],
  },
  Evidence: {
    purpose: 'Photos, videos, voice notes and documents (files are stored in Google Drive by AppSheet).',
    cols: [KEY('EvidenceID'), c_('DPRID', 'Ref', 'user', { ref: 'DPR', isPartOf: true }), c_('ProjectID', 'Ref', 'app', { ref: 'Projects', note: 'Initial value: [DPRID].[ProjectID]' }),
      REF('ActivityID', 'Activities'), c_('Type', 'Enum', 'user', { enum: ['Photo', 'Video', 'Voice note', 'Document', 'Drawing'] }),
      c_('Photo', 'Image', 'user', { note: 'Show_If: [Type] = "Photo"' }), c_('File', 'File', 'user', { note: 'Show_If: [Type] <> "Photo"' }),
      c_('Caption', 'Text'), c_('Location', 'LatLong', 'user', { note: 'Optional; user taps to fill' }),
      c_('CapturedAt', 'DateTime', 'app', { note: 'Initial value: NOW()' }), c_('CapturedBy', 'Email', 'app', { note: 'Initial value: USEREMAIL()' }),
      SCRIPT('DriveFileID', 'Text', 'Resolved Drive file'), SCRIPT('SHA256', 'Text', 'Checksum at approval')],
  },
  QuickEntry: {
    purpose: 'Voice / typed site statements converted into a proposed entry for confirmation.',
    cols: [KEY('EntryID'), c_('DPRID', 'Ref', 'user', { ref: 'DPR', isPartOf: true }), c_('ProjectID', 'Ref', 'app', { ref: 'Projects', note: 'Initial value: [DPRID].[ProjectID]' }),
      c_('Statement', 'LongText', 'user', { note: 'Use the phone keyboard microphone to dictate' }),
      c_('Status', 'Enum', 'app', { enum: ['New', 'Proposed', 'Confirmed', 'Applied', 'Discarded'], note: 'Initial value: "New"' }),
      c_('ProposedActivityID', 'Ref', 'script', { ref: 'Activities', note: 'Editable by the user before confirming' }),
      c_('ProposedQty', 'Decimal', 'script'), SCRIPT('ProposedUnit', 'Text', ''), c_('ProposedWorkers', 'Number', 'script'),
      c_('ProposedTrade', 'Text', 'user'), SCRIPT('ProposedEquipment', 'Text', ''), SCRIPT('Uncertain', 'Text', 'Fields to double-check')],
  },
  Approvals: {
    purpose: 'Approval history (append-only).',
    appsheetReadOnly: true,
    cols: [KEY('LogID'), c_('DPRID', 'Ref', 'script', { ref: 'DPR' }), SCRIPT('Action', 'Text', ''), SCRIPT('By', 'Email', ''),
      SCRIPT('At', 'DateTime', ''), SCRIPT('FromStatus', 'Text', ''), SCRIPT('ToStatus', 'Text', ''), SCRIPT('Comment', 'LongText', '')],
  },
  Snapshots: {
    purpose: 'Immutable record of each approved DPR revision (JSON + PDF in Drive, SHA-256).',
    appsheetReadOnly: true,
    cols: [KEY('SnapshotID'), c_('DPRID', 'Ref', 'script', { ref: 'DPR' }), SCRIPT('DPRNo', 'Text', ''), SCRIPT('Revision', 'Number', ''),
      SCRIPT('ApprovedAt', 'DateTime', ''), SCRIPT('ApprovedBy', 'Email', ''), SCRIPT('JsonFileID', 'Text', ''),
      SCRIPT('PdfFileID', 'Text', ''), SCRIPT('SHA256', 'Text', ''), SCRIPT('IntegrityOK', 'Yes/No', 'Re-checked nightly')],
  },
  Baselines: {
    purpose: 'Frozen baseline revisions (append-only).',
    appsheetReadOnly: true,
    cols: [KEY('BaselineRowID'), REF('ProjectID', 'Projects'), SCRIPT('Revision', 'Number', ''), SCRIPT('FrozenAt', 'DateTime', ''),
      c_('ActivityID', 'Ref', 'script', { ref: 'Activities' }), SCRIPT('Start', 'Date', ''), SCRIPT('Finish', 'Date', ''), SCRIPT('TotalQty', 'Decimal', '')],
  },
  Audit: {
    purpose: 'Script audit trail (append-only).',
    appsheetReadOnly: true,
    cols: [SCRIPT('At', 'DateTime', ''), SCRIPT('User', 'Email', ''), SCRIPT('Table', 'Text', ''), SCRIPT('RowID', 'Text', ''),
      SCRIPT('Action', 'Text', ''), SCRIPT('Details', 'LongText', '')],
  },
  Report_Daily: {
    purpose: 'Generated for Looker Studio: daily accepted quantities, manpower and equipment per project.',
    appsheet: false,
    cols: [SCRIPT('ProjectCode', 'Text', ''), SCRIPT('Date', 'Date', ''), SCRIPT('ActivityCode', 'Text', ''), SCRIPT('Activity', 'Text', ''),
      SCRIPT('Unit', 'Text', ''), SCRIPT('AcceptedQty', 'Decimal', ''), SCRIPT('Headcount', 'Number', ''), SCRIPT('ManHours', 'Decimal', ''),
      SCRIPT('EquipmentHours', 'Decimal', ''), SCRIPT('DelayReason', 'Text', '')],
  },
  Report_SCurve: {
    purpose: 'Generated for Looker Studio: planned vs actual cumulative progress.',
    appsheet: false,
    cols: [SCRIPT('ProjectCode', 'Text', ''), SCRIPT('Date', 'Date', ''), SCRIPT('PlannedPct', 'Decimal', ''), SCRIPT('ActualPct', 'Decimal', '')],
  },
};

function keyOf_(table) {
  var cols = SCHEMA[table].cols;
  for (var i = 0; i < cols.length; i++) if (cols[i].key) return cols[i].n;
  return null;
}
