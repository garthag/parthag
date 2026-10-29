/**
 * Google Drive: project folder tree, report files and evidence shortcuts.
 * The script runs as the spreadsheet owner (the document-controller account),
 * so field users never need direct access to Drive or to the spreadsheet.
 */
var PROJECT_FOLDERS = ['01_Contracts', '02_Drawings', '03_Baseline_and_Schedules', '04_DPR', '05_Manpower', '06_Equipment',
  '07_Materials', '08_QA_QC', '09_Safety', '10_Inspection_and_Test_Records', '11_Fabrication_and_Dispatch',
  '12_Billing_and_BOQ', '13_Commissioning', '14_Client_Correspondence', '15_Archive'];
var DPR_FOLDERS = ['Daily Reports', 'Photographs', 'Videos', 'Voice Notes', 'Approved PDFs'];

function ensureRootFolder_() {
  var id = setting_('ROOT_FOLDER_ID', '');
  if (id) {
    try { return DriveApp.getFolderById(id); } catch (e) { /* deleted: recreate */ }
  }
  var parent = parentFolder_();
  var f = parent.createFolder('CONSTRUCT DPR');
  setSetting_('ROOT_FOLDER_ID', f.getId(), 'Root folder for all project folders');
  return f;
}

function parentFolder_() {
  try {
    var it = DriveApp.getFileById(ss_().getId()).getParents();
    if (it.hasNext()) return it.next();
  } catch (e) { /* fall back to My Drive */ }
  return DriveApp.getRootFolder();
}

/** Returns (creating once) the folder at `path` below the project folder. Idempotent. */
function projectFolder_(project, path) {
  var key = 'FOLDER:' + project.ProjectID + ':' + (path || '');
  var known = setting_(key, '');
  if (known) {
    try { return DriveApp.getFolderById(known); } catch (e) { /* recreate below */ }
  }
  var folder;
  if (!path) {
    folder = childFolder_(ensureRootFolder_(), project.Code + ' - ' + project.Name);
    var p = byKey_('Projects', project.ProjectID);
    if (p) update_('Projects', p, { DriveFolderID: folder.getId(), DriveFolderURL: folder.getUrl() });
  } else {
    var parts = path.split('/');
    folder = projectFolder_(project, parts.slice(0, -1).join('/'));
    folder = childFolder_(folder, parts[parts.length - 1]);
  }
  setSetting_(key, folder.getId(), 'Folder cache');
  return folder;
}

function childFolder_(parent, name) {
  var it = parent.getFoldersByName(name);
  return it.hasNext() ? it.next() : parent.createFolder(name);
}

function createProjectFolders_(project, dateIso) {
  PROJECT_FOLDERS.forEach(function (f) { projectFolder_(project, f); });
  var d = dateIso || today_();
  DPR_FOLDERS.forEach(function (f) { projectFolder_(project, '04_DPR/' + d.slice(0, 4) + '/' + d.slice(5, 7) + '/' + f); });
  return projectFolder_(project, '');
}

function dprFolder_(project, dateIso, kind) {
  return projectFolder_(project, '04_DPR/' + dateIso.slice(0, 4) + '/' + dateIso.slice(5, 7) + '/' + kind);
}

/**
 * AppSheet stores Image/File columns as a path relative to the app's data
 * folder (for example "Evidence_Images/abc.Photo.123456.jpg"). This finds the
 * Drive file next to the spreadsheet or in Settings APPSHEET_FOLDER_ID.
 */
function resolveAppFile_(value) {
  if (!value) return null;
  var s = String(value);
  var id = (/[-\w]{25,}/.exec(s) || [])[0];
  if (/^https?:/.test(s) && id) {
    try { return DriveApp.getFileById(id); } catch (e) { return null; }
  }
  var parts = s.split('/').filter(Boolean);
  var bases = [];
  var configured = setting_('APPSHEET_FOLDER_ID', '');
  if (configured) { try { bases.push(DriveApp.getFolderById(configured)); } catch (e) { /* ignore */ } }
  bases.push(parentFolder_());
  for (var b = 0; b < bases.length; b++) {
    var folder = bases[b];
    var ok = true;
    for (var i = 0; i < parts.length - 1 && ok; i++) {
      var it = folder.getFoldersByName(parts[i]);
      if (it.hasNext()) folder = it.next();
      else ok = false;
    }
    if (!ok) continue;
    var files = folder.getFilesByName(parts[parts.length - 1]);
    if (files.hasNext()) return files.next();
  }
  return null;
}
