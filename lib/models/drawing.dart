// Drawing register models.
//
// The register is organised the way plant projects (refinery, power, water
// and process plants) usually file drawings:
//
//   Plant  →  Unit / Area  →  Drawing type (discipline)  →  Drawing no.  →  Revisions
//
// Each [Drawing] is one revision of one drawing file. Revisions of the same
// drawing share a [Drawing.registerKey]; the highest revision is "current" and
// older ones are shown as superseded.

import 'models.dart';

String _s(dynamic v) => v == null ? '' : '$v';
DateTime? _dt(dynamic v) => v == null ? null : DateTime.tryParse('$v');

/// Drawing type (engineering discipline). The list order is the order the
/// register is printed and displayed in.
class DrawingType {
  const DrawingType(this.code, this.name, this.aliases, this.kindHints);

  /// Short code printed in the register, e.g. "ST".
  final String code;
  final String name;

  /// Discipline codes that may appear in a drawing number or folder name.
  final List<String> aliases;

  /// Words in a file name / folder that identify this discipline, e.g.
  /// "P&ID" → Process, "SLD" → Electrical.
  final List<String> kindHints;

  String get label => '$code – $name';

  static const general = DrawingType('GN', 'General / Plot Plan', ['G', 'GN', 'GEN', 'GENERAL'],
      ['PLOT PLAN', 'KEY PLAN', 'OVERALL', 'LEGEND', 'COVER', 'INDEX', 'SITE PLAN', 'MASTER PLAN']);
  static const process = DrawingType('PR', 'Process', ['PR', 'PRO', 'PROC', 'PROCESS', 'PRS'],
      ['P&ID', 'PID', 'PFD', 'UFD', 'HMB', 'PROCESS FLOW', 'FLOW DIAGRAM']);
  static const civil = DrawingType('CV', 'Civil', ['C', 'CV', 'CIV', 'CIVIL'],
      ['GRADING', 'ROAD', 'DRAINAGE', 'PAVING', 'BOUNDARY', 'CULVERT', 'SEWER', 'EARTHWORK', 'TRENCH']);
  static const structural = DrawingType('ST', 'Structural', ['S', 'ST', 'STR', 'STRUC', 'STRUCT', 'STRUCTURAL'],
      ['FOUNDATION', 'FDN', 'PILE', 'RCC', 'BBS', 'REINFORCEMENT', 'STEEL STRUCTURE', 'PIPE RACK', 'PIPERACK', 'BEAM', 'COLUMN', 'SLAB']);
  static const architectural = DrawingType('AR', 'Architectural', ['A', 'AR', 'ARC', 'ARCH', 'ARCHITECTURAL'],
      ['FLOOR PLAN', 'ELEVATION', 'DOOR', 'WINDOW', 'FINISH', 'FACADE', 'TOILET']);
  static const mechanical = DrawingType('ME', 'Mechanical / Equipment', ['M', 'ME', 'MEC', 'MECH', 'MECHANICAL', 'EQ', 'EQP', 'EQUIP'],
      ['EQUIPMENT', 'VESSEL', 'TANK', 'PUMP', 'COMPRESSOR', 'HEAT EXCHANGER', 'NOZZLE', 'DATASHEET']);
  static const piping = DrawingType('PP', 'Piping', ['P', 'PP', 'PI', 'PIP', 'PIPING', 'PIPE'],
      ['ISO', 'ISOMETRIC', 'GAD', 'PIPING LAYOUT', 'PIPE SUPPORT', 'ROUTING', 'LINE LIST']);
  static const electrical = DrawingType('EL', 'Electrical', ['E', 'EL', 'ELE', 'ELEC', 'ELECTRICAL'],
      ['SLD', 'SINGLE LINE', 'CABLE', 'EARTHING', 'LIGHTING', 'LIGHTNING', 'SUBSTATION', 'TRANSFORMER', 'MCC', 'PANEL']);
  static const instrumentation = DrawingType('IN', 'Instrumentation & Control', ['I', 'IN', 'INS', 'INST', 'IC', 'C&I', 'CI', 'INSTRUMENTATION'],
      ['LOOP', 'HOOK UP', 'HOOKUP', 'HOOK-UP', 'JB', 'JUNCTION BOX', 'DCS', 'PLC', 'CONTROL', 'INSTRUMENT']);
  static const hvac = DrawingType('HV', 'HVAC', ['H', 'HV', 'HVAC'],
      ['DUCT', 'AHU', 'VENTILATION', 'AIR CONDITIONING', 'CHILLER']);
  static const fire = DrawingType('FP', 'Fire Protection', ['F', 'FF', 'FP', 'FPS', 'FIRE'],
      ['HYDRANT', 'SPRINKLER', 'DELUGE', 'FIRE ALARM', 'FIRE FIGHTING', 'FOAM']);
  static const plumbing = DrawingType('PL', 'Plumbing / Utilities', ['PL', 'PH', 'PLB', 'PLUMB', 'PLUMBING', 'UT', 'UTIL', 'UTILITY'],
      ['PLUMBING', 'WATER SUPPLY', 'SANITARY', 'UTILITY']);
  static const other = DrawingType('OT', 'Other / Unclassified', [], []);

  /// Display / print order of the register.
  static const all = [
    general,
    process,
    civil,
    structural,
    architectural,
    mechanical,
    piping,
    electrical,
    instrumentation,
    hvac,
    fire,
    plumbing,
    other,
  ];

  static DrawingType byCode(String code) =>
      all.firstWhere((t) => t.code == code, orElse: () => other);

  int get order => all.indexOf(this);
}

/// Common drawing kinds (sheet types). Used to describe a drawing when the
/// file name carries no title.
const drawingKinds = <String, String>{
  'GA': 'General Arrangement',
  'GAD': 'General Arrangement',
  'P&ID': 'Piping & Instrumentation Diagram',
  'PID': 'Piping & Instrumentation Diagram',
  'PFD': 'Process Flow Diagram',
  'UFD': 'Utility Flow Diagram',
  'ISO': 'Isometric',
  'SLD': 'Single Line Diagram',
  'LAY': 'Layout',
  'LAYOUT': 'Layout',
  'PLN': 'Plan',
  'PLAN': 'Plan',
  'SEC': 'Section',
  'SECTION': 'Section',
  'ELEV': 'Elevation',
  'DET': 'Detail',
  'DTL': 'Detail',
  'DETAIL': 'Detail',
  'FDN': 'Foundation',
  'BBS': 'Bar Bending Schedule',
  'SCH': 'Schematic',
  'LOOP': 'Loop Diagram',
  'HU': 'Hook-up',
  'HOOKUP': 'Hook-up',
  'SUP': 'Support Detail',
  'KP': 'Key Plan',
};

enum DrawingStatus { forReview, forApproval, forConstruction, asBuilt, forInformation }

extension DrawingStatusLabel on DrawingStatus {
  String get code => switch (this) {
        DrawingStatus.forReview => 'IFR',
        DrawingStatus.forApproval => 'IFA',
        DrawingStatus.forConstruction => 'IFC',
        DrawingStatus.asBuilt => 'AB',
        DrawingStatus.forInformation => 'FI',
      };

  String get label => switch (this) {
        DrawingStatus.forReview => 'Issued for Review',
        DrawingStatus.forApproval => 'Issued for Approval',
        DrawingStatus.forConstruction => 'Issued for Construction',
        DrawingStatus.asBuilt => 'As-Built',
        DrawingStatus.forInformation => 'For Information',
      };
}

/// File formats the register knows about.
enum DrawingFormat { pdf, image, cad, other }

DrawingFormat drawingFormatOf(String fileName) {
  final ext = fileName.toLowerCase().split('.').last;
  return switch (ext) {
    'pdf' => DrawingFormat.pdf,
    'png' || 'jpg' || 'jpeg' || 'webp' || 'gif' || 'bmp' => DrawingFormat.image,
    'dwg' || 'dxf' || 'dgn' || 'dwf' || 'dwfx' || 'tif' || 'tiff' => DrawingFormat.cad,
    _ => DrawingFormat.other,
  };
}

class Drawing {
  Drawing({
    String? id,
    required this.projectId,
    required this.number,
    this.title = '',
    this.revision = '0',
    this.plant = '',
    this.unit = '',
    this.typeCode = 'OT',
    this.kind = '',
    this.status = DrawingStatus.forConstruction,
    this.fileName = '',
    this.localPath = '',
    this.sourcePath = '',
    this.remarks = '',
    this.sizeBytes = 0,
    DateTime? importedAt,
  })  : id = id ?? newId(),
        importedAt = importedAt ?? DateTime.now();

  final String id;
  final String projectId;

  /// Drawing / document number without the revision, e.g. "RF-100-ST-GA-0001".
  String number;
  String title;

  /// Revision as written on the drawing: "0", "1", "A", "B"…
  String revision;
  String plant;

  /// Unit / area, e.g. "U-100" or "CDU".
  String unit;

  /// [DrawingType.code].
  String typeCode;

  /// Sheet kind, e.g. "General Arrangement", "Isometric".
  String kind;
  DrawingStatus status;

  /// Original file name inside the ZIP.
  String fileName;

  /// Where the file is stored in app storage.
  String localPath;

  /// Folder path inside the ZIP, kept for traceability.
  String sourcePath;
  String remarks;
  int sizeBytes;
  DateTime importedAt;

  DrawingType get type => DrawingType.byCode(typeCode);
  DrawingFormat get format => drawingFormatOf(fileName.isEmpty ? localPath : fileName);

  /// Revisions of the same drawing share this key.
  String get registerKey =>
      '${plant.trim().toUpperCase()}|${unit.trim().toUpperCase()}|${number.trim().toUpperCase()}';

  String get displayTitle {
    if (title.isNotEmpty) return title;
    if (kind.isNotEmpty) return '$kind – ${type.name}';
    return type.name;
  }

  Map<String, dynamic> toJson() => {
        'id': id,
        'projectId': projectId,
        'number': number,
        'title': title,
        'revision': revision,
        'plant': plant,
        'unit': unit,
        'typeCode': typeCode,
        'kind': kind,
        'status': status.name,
        'fileName': fileName,
        'localPath': localPath,
        'sourcePath': sourcePath,
        'remarks': remarks,
        'sizeBytes': sizeBytes,
        'importedAt': importedAt.toIso8601String(),
      };

  factory Drawing.fromJson(Map<String, dynamic> j) => Drawing(
        id: _s(j['id']),
        projectId: _s(j['projectId']),
        number: _s(j['number']),
        title: _s(j['title']),
        revision: _s(j['revision']),
        plant: _s(j['plant']),
        unit: _s(j['unit']),
        typeCode: j['typeCode'] == null ? 'OT' : _s(j['typeCode']),
        kind: _s(j['kind']),
        status: DrawingStatus.values.firstWhere((s) => s.name == j['status'],
            orElse: () => DrawingStatus.forConstruction),
        fileName: _s(j['fileName']),
        localPath: _s(j['localPath']),
        sourcePath: _s(j['sourcePath']),
        remarks: _s(j['remarks']),
        sizeBytes: j['sizeBytes'] is num ? (j['sizeBytes'] as num).toInt() : 0,
        importedAt: _dt(j['importedAt']),
      );
}

/// Sort rank of a revision. Lettered revisions (A, B, C… issued for review /
/// approval) come before numbered ones (0, 1, 2… issued for construction),
/// which is the usual convention on Indian EPC and PSU projects.
int revisionRank(String rev) {
  final r = rev.trim().toUpperCase();
  if (r.isEmpty) return -1;
  final n = int.tryParse(r);
  if (n != null) return 1000 + n;
  // "A" = 1, "B" = 2 … "AA" = 27.
  var v = 0;
  for (final c in r.codeUnits) {
    if (c < 65 || c > 90) return 0;
    v = v * 26 + (c - 64);
  }
  return v.clamp(0, 999);
}

int compareRevisions(String a, String b) {
  final c = revisionRank(a).compareTo(revisionRank(b));
  return c != 0 ? c : a.compareTo(b);
}

/// Natural compare so that "U-20" sorts before "U-100" and "0002" before "0010".
int naturalCompare(String a, String b) {
  final re = RegExp(r'(\d+)|(\D+)');
  final ma = re.allMatches(a.toUpperCase()).toList();
  final mb = re.allMatches(b.toUpperCase()).toList();
  for (var i = 0; i < ma.length && i < mb.length; i++) {
    final x = ma[i].group(0)!, y = mb[i].group(0)!;
    final nx = int.tryParse(x), ny = int.tryParse(y);
    final c = nx != null && ny != null ? nx.compareTo(ny) : x.compareTo(y);
    if (c != 0) return c;
  }
  return ma.length.compareTo(mb.length);
}

/// One drawing with all its revisions, newest first.
class DrawingEntry {
  DrawingEntry(this.revisions);

  /// Newest revision first.
  final List<Drawing> revisions;

  Drawing get current => revisions.first;
  List<Drawing> get superseded => revisions.skip(1).toList();
}

class UnitGroup {
  UnitGroup(this.unit, this.types);
  final String unit;

  /// Drawing types in register order, each with its drawings sorted by number.
  final List<(DrawingType, List<DrawingEntry>)> types;

  int get count => types.fold(0, (s, t) => s + t.$2.length);
}

class PlantGroup {
  PlantGroup(this.plant, this.units);
  final String plant;
  final List<UnitGroup> units;

  int get count => units.fold(0, (s, u) => s + u.count);
}

/// Builds the Plant → Unit → Type → Drawing tree used by the register screen,
/// the viewer and the printed register.
List<PlantGroup> organiseDrawings(Iterable<Drawing> drawings) {
  final byKey = <String, List<Drawing>>{};
  for (final d in drawings) {
    byKey.putIfAbsent(d.registerKey, () => []).add(d);
  }
  final entries = byKey.values.map((revs) {
    revs.sort((a, b) {
      final c = compareRevisions(b.revision, a.revision);
      return c != 0 ? c : b.importedAt.compareTo(a.importedAt);
    });
    return DrawingEntry(revs);
  }).toList();

  final plants = <String, Map<String, Map<DrawingType, List<DrawingEntry>>>>{};
  for (final e in entries) {
    final d = e.current;
    plants
        .putIfAbsent(d.plant, () => {})
        .putIfAbsent(d.unit, () => {})
        .putIfAbsent(d.type, () => [])
        .add(e);
  }

  int blankLast(String a, String b) {
    if (a.isEmpty != b.isEmpty) return a.isEmpty ? 1 : -1;
    return naturalCompare(a, b);
  }

  final plantNames = plants.keys.toList()..sort(blankLast);
  return [
    for (final plant in plantNames)
      PlantGroup(plant, [
        for (final unit in (plants[plant]!.keys.toList()..sort(blankLast)))
          UnitGroup(unit, [
            for (final type in DrawingType.all)
              if (plants[plant]![unit]![type] != null)
                (
                  type,
                  plants[plant]![unit]![type]!
                    ..sort((a, b) => naturalCompare(a.current.number, b.current.number)),
                ),
          ]),
      ]),
  ];
}
