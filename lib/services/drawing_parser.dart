// Reads plant, unit, drawing type, drawing number, revision and status out of
// a file's path inside a drawing ZIP.
//
// Works with the usual plant numbering styles, for example:
//
//   RF-100-ST-GA-0001_R2.pdf                       → Unit 100 · Structural · Rev 2
//   Plant-2/Unit 300/Piping/ISO-300-1023 Rev B.pdf → Plant 2 · Unit 300 · Piping · Rev B
//   U200-EL-SLD-001-R0 Main SLD (IFC).pdf          → Unit 200 · Electrical · Rev 0 · IFC
//   02_Structural/Foundation Plan.dwg              → Structural (from folder)

import '../models/drawing.dart';

class ParsedDrawing {
  ParsedDrawing({
    required this.number,
    required this.title,
    required this.revision,
    required this.plant,
    required this.unit,
    required this.type,
    required this.kind,
    required this.status,
    required this.fileName,
    required this.folder,
  });

  final String number;
  final String title;
  final String revision;
  final String plant;
  final String unit;
  final DrawingType type;
  final String kind;
  final DrawingStatus status;
  final String fileName;
  final String folder;
}

const _sep = r'[\s_\-.()\[\]]';

final _revWord = RegExp('(?:^|$_sep)REV(?:ISION)?[\\s.\\-_#:]*([0-9]{1,2}|[A-Z]{1,2})(?=\$|$_sep)',
    caseSensitive: false);
final _revShort = RegExp('(?:^|$_sep)R([0-9]{1,2})(?=\$|$_sep)', caseSensitive: false);

final _statusWords = <RegExp, DrawingStatus>{
  RegExp('(?:^|$_sep)(?:IFC|AFC|GFC)(?=\$|$_sep)', caseSensitive: false): DrawingStatus.forConstruction,
  RegExp('(?:^|$_sep)(?:IFA|AFA)(?=\$|$_sep)', caseSensitive: false): DrawingStatus.forApproval,
  RegExp('(?:^|$_sep)(?:IFR)(?=\$|$_sep)', caseSensitive: false): DrawingStatus.forReview,
  RegExp('(?:^|$_sep)(?:AS[\\s_\\-]?BUILT|ABD)(?=\$|$_sep)', caseSensitive: false): DrawingStatus.asBuilt,
  RegExp('(?:^|$_sep)(?:FOR[\\s_\\-]?INFO(?:RMATION)?)(?=\$|$_sep)', caseSensitive: false):
      DrawingStatus.forInformation,
};

/// Leading code of a file name: groups of letters/digits joined by - _ . or /.
final _numberRe = RegExp(r'^[A-Za-z0-9&]+(?:[-_.][A-Za-z0-9&]+)*');

final _plantFolder = RegExp(r'^(?:PLANT|PLT)[\s_\-#.]*([A-Z0-9][A-Z0-9 \-&]*)$', caseSensitive: false);
final _unitFolder = RegExp(r'^(UNIT|UNT|U|AREA|BLOCK|BLK)[\s_\-#.]*([A-Z0-9]+)$');
final _unitInText = RegExp(r'(?:^|[\s_\-])(UNIT|AREA)[\s_\-#.]*(\d{2,4}[A-Z]?)(?=$|[\s_\-])');
final _orderPrefix = RegExp(r'^\d{1,2}[\s._\-]+(?=\D)');

String _stripPrefix(String f) => f.replaceFirst(_orderPrefix, '').trim();
String _cleanFolder(String f) => _stripPrefix(f).toUpperCase();

String _unitLabel(String word, String id) {
  final w = word.toUpperCase();
  if (w.startsWith('AREA')) return 'Area $id';
  if (w.startsWith('B')) return 'Block $id';
  return 'Unit $id';
}

bool _isJunk(String path) {
  final lower = path.toLowerCase();
  final name = lower.split('/').last;
  return lower.startsWith('__macosx/') ||
      lower.contains('/__macosx/') ||
      name.startsWith('._') ||
      name == '.ds_store' ||
      name == 'thumbs.db' ||
      name == 'desktop.ini' ||
      name.endsWith('.bak') ||
      name.endsWith('.tmp') ||
      name.endsWith('.dwl') ||
      name.endsWith('.dwl2') ||
      name.isEmpty;
}

/// True when the archive entry is a real drawing / document file.
bool isDrawingFile(String path) => !_isJunk(path);

DrawingType? _typeFromWord(String word, {bool allowSingleLetter = true}) {
  final w = word.toUpperCase();
  if (w.isEmpty) return null;
  if (!allowSingleLetter && w.length == 1) return null;
  for (final t in DrawingType.all) {
    if (t.aliases.contains(w)) return t;
  }
  return null;
}

DrawingType? _typeFromFolder(String folder) {
  final f = _cleanFolder(folder);
  final direct = _typeFromWord(f, allowSingleLetter: false);
  if (direct != null) return direct;
  for (final t in DrawingType.all) {
    if (t == DrawingType.other) continue;
    final mainWord = t.name.split(RegExp(r'[\s/&]+')).first.toUpperCase();
    if (f.contains(mainWord)) return t;
  }
  return _typeFromHints(f);
}

DrawingType? _typeFromHints(String text) {
  final padded = ' ${text.toUpperCase().replaceAll(RegExp(r'[_\-.]+'), ' ')} ';
  for (final t in DrawingType.all) {
    for (final h in t.kindHints) {
      if (padded.contains(' $h ') || (h.contains('&') && padded.contains(h))) return t;
    }
  }
  return null;
}

String _tidy(String s) {
  var t = s
      .replaceAll('_', ' ')
      .replaceAll(RegExp(r'\(\s*\)|\[\s*\]'), ' ')
      .replaceAll(RegExp(r'\s+'), ' ');
  // Brackets left unbalanced after taking out "(IFC)" or "[Rev 2]".
  if (!t.contains('(')) t = t.replaceAll(')', ' ');
  if (!t.contains(')')) t = t.replaceAll('(', ' ');
  if (!t.contains('[')) t = t.replaceAll(']', ' ');
  if (!t.contains(']')) t = t.replaceAll('[', ' ');
  return t.replaceAll(RegExp(r'\s+'), ' ').replaceAll(RegExp(r'^[\s\-.,]+|[\s\-.,]+$'), '').trim();
}

/// Parses one archive path. [defaultPlant] is used when neither the folders
/// nor the drawing number name a plant.
ParsedDrawing parseDrawingPath(String path, {String defaultPlant = ''}) {
  final parts = path.replaceAll('\\', '/').split('/').where((s) => s.isNotEmpty).toList();
  final fileName = parts.isEmpty ? path : parts.last;
  final folders = parts.length > 1 ? parts.sublist(0, parts.length - 1) : <String>[];
  final dot = fileName.lastIndexOf('.');
  var stem = dot > 0 ? fileName.substring(0, dot) : fileName;

  // Revision.
  var revision = '';
  var m = _revWord.firstMatch(stem);
  m ??= _revShort.firstMatch(stem);
  if (m != null) {
    revision = m.group(1)!.toUpperCase();
    stem = stem.replaceRange(m.start, m.end, ' ');
  }

  // Status keywords (IFC, IFA, As-built …).
  DrawingStatus? status;
  for (final e in _statusWords.entries) {
    final sm = e.key.firstMatch(stem);
    if (sm != null) {
      status ??= e.value;
      stem = stem.replaceRange(sm.start, sm.end, ' ');
    }
  }
  status ??= revision.isNotEmpty && int.tryParse(revision) == null
      ? DrawingStatus.forApproval
      : DrawingStatus.forConstruction;
  if (revision.isEmpty) revision = '0';

  stem = stem.trim();

  // Drawing number = leading code (must contain a digit); the rest is title.
  var number = '';
  var title = '';
  final nm = _numberRe.firstMatch(stem);
  if (nm != null && RegExp(r'\d').hasMatch(nm.group(0)!)) {
    number = nm.group(0)!.replaceAll(RegExp(r'[-_.]+$'), '').toUpperCase();
    title = _tidy(stem.substring(nm.end));
  } else {
    number = _tidy(stem);
    title = '';
  }
  if (number.isEmpty) number = _tidy(fileName);

  final tokens = number.split(RegExp(r'[-_.]')).where((t) => t.isNotEmpty).toList();

  // Plant.
  var plant = '';
  for (final f in folders) {
    final pm = _plantFolder.firstMatch(_stripPrefix(f));
    if (pm != null) {
      plant = 'Plant ${pm.group(1)!.trim()}';
      break;
    }
  }
  if (plant.isEmpty) {
    for (final t in tokens) {
      final pm = RegExp(r'^(?:PLANT|PLT)(\d+[A-Z]?)$').firstMatch(t);
      if (pm != null) {
        plant = 'Plant ${pm.group(1)}';
        break;
      }
    }
  }
  // Folders that name neither a plant, a unit pattern nor a discipline are
  // treated as names: "Refinery/CDU/…" → plant Refinery, unit CDU, and a
  // single one such as "External Works/…" → unit External Works.
  final named = [
    for (final f in folders)
      if (!_plantFolder.hasMatch(_stripPrefix(f)) &&
          !_unitFolder.hasMatch(_cleanFolder(f)) &&
          _typeFromFolder(f) == null)
        _tidy(_stripPrefix(f)),
  ].where((f) => f.isNotEmpty).toList();
  final plantFromFolder = plant.isNotEmpty;
  if (plant.isEmpty && named.length >= 2) plant = named.removeAt(0);
  if (plant.isEmpty) plant = defaultPlant;

  // Drawing type (discipline). Folder names win, then the discipline code in
  // the drawing number, then words such as "P&ID" or "SLD".
  DrawingType? type;
  for (final f in folders.reversed) {
    if (_unitFolder.hasMatch(_cleanFolder(f)) || _plantFolder.hasMatch(_cleanFolder(f))) continue;
    type = _typeFromFolder(f);
    if (type != null) break;
  }
  var typeIdx = -1;
  if (type == null) {
    // Multi-letter codes first, skipping the project code in position 0.
    final order = [for (var i = 1; i < tokens.length; i++) i, if (tokens.isNotEmpty) 0];
    for (final single in [false, true]) {
      for (final i in order) {
        final t = _typeFromWord(tokens[i], allowSingleLetter: single);
        if (t != null && (single ? tokens[i].length == 1 : true)) {
          type = t;
          typeIdx = i;
          break;
        }
      }
      if (type != null) break;
    }
  } else {
    typeIdx = tokens.indexWhere((t) => _typeFromWord(t, allowSingleLetter: false) == type);
  }
  type ??= _typeFromHints('$number $title');
  type ??= DrawingType.other;

  // Unit / area.
  var unit = '';
  for (final f in folders) {
    final um = _unitFolder.firstMatch(_cleanFolder(f));
    if (um != null) {
      unit = _unitLabel(um.group(1)!, um.group(2)!);
      break;
    }
  }
  if (unit.isEmpty) {
    for (var i = 0; i < tokens.length; i++) {
      final um = RegExp(r'^U(\d{2,4}[A-Z]?)$').firstMatch(tokens[i]);
      if (um != null) {
        unit = 'Unit ${um.group(1)}';
        break;
      }
      if ((tokens[i] == 'UNIT' || tokens[i] == 'AREA') && i + 1 < tokens.length) {
        unit = _unitLabel(tokens[i], tokens[i + 1]);
        break;
      }
    }
  }
  if (unit.isEmpty && typeIdx > 0) {
    // A 2–4 digit area code just before the discipline code, as in
    // PROJECT-100-ST-GA-0001.
    for (var i = typeIdx - 1; i >= 1; i--) {
      if (RegExp(r'^\d{2,4}[A-Z]?$').hasMatch(tokens[i])) {
        unit = 'Unit ${tokens[i]}';
        break;
      }
    }
  }
  if (unit.isEmpty && named.isNotEmpty && (plantFromFolder || named.length == 1 || plant != defaultPlant)) {
    unit = named.first;
  }
  if (unit.isEmpty) {
    final um = _unitInText.firstMatch(' ${title.toUpperCase()} ');
    if (um != null) unit = _unitLabel(um.group(1)!, um.group(2)!);
  }

  // Sheet kind.
  var kind = '';
  final words = [...tokens, ...title.toUpperCase().split(RegExp(r'[\s\-_]+'))];
  for (final w in words) {
    final k = drawingKinds[w.toUpperCase()];
    if (k != null) {
      kind = k;
      break;
    }
  }
  if (kind.isEmpty) {
    final upper = ' ${title.toUpperCase()} ';
    for (final (word, k) in const [
      ('ISOMETRIC', 'Isometric'),
      ('LAYOUT', 'Layout'),
      ('SECTION', 'Section'),
      ('ELEVATION', 'Elevation'),
      ('DETAIL', 'Detail'),
      ('SCHEMATIC', 'Schematic'),
      ('PLAN', 'Plan'),
    ]) {
      if (upper.contains(word)) {
        kind = k;
        break;
      }
    }
  }

  return ParsedDrawing(
    number: number,
    title: title,
    revision: revision,
    plant: plant,
    unit: unit,
    type: type,
    kind: kind,
    status: status,
    fileName: fileName,
    folder: folders.join('/'),
  );
}

/// When every path in a ZIP sits under one wrapper folder (e.g. the ZIP name),
/// returns that folder so it can be ignored for plant / unit detection.
String commonRootFolder(Iterable<String> paths) {
  String? root;
  for (final p in paths) {
    final parts = p.replaceAll('\\', '/').split('/');
    if (parts.length < 2) return '';
    root ??= parts.first;
    if (parts.first != root) return '';
  }
  return root ?? '';
}
