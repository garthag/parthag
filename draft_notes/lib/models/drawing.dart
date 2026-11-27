import 'dart:math' as math;
import 'dart:ui';

import 'package:uuid/uuid.dart';

import '../cad/geometry.dart';

const _uuid = Uuid();
String newId() => _uuid.v4();

/// Colour value meaning "white on dark paper, black on light paper", like
/// AutoCAD colour 7.
const int kAdaptiveColor = 0;

/// Background of a drawing. Model space and blueprint are the dark CAD
/// backgrounds; the others are paper templates for handwriting.
enum PaperStyle {
  model('Model space', true),
  blueprint('Blueprint', true),
  plain('Plain paper', false),
  lined('Lined paper', false),
  grid('Squared paper', false),
  dotted('Dotted paper', false);

  const PaperStyle(this.label, this.isDark);
  final String label;
  final bool isDark;

  Color get background => switch (this) {
        PaperStyle.model => const Color(0xFF1F252D),
        PaperStyle.blueprint => const Color(0xFF103A6E),
        _ => const Color(0xFFFFFDF7),
      };

  Color get foreground => isDark ? const Color(0xFFF2F4F7) : const Color(0xFF1B1B1F);
}

enum DrawUnits {
  mm('mm'),
  cm('cm'),
  m('m'),
  inch('in'),
  ft('ft'),
  none('');

  const DrawUnits(this.suffix);
  final String suffix;
}

enum InkPen { pen, pencil, highlighter }

double _r(double v) => (v * 1000).roundToDouble() / 1000;
List<double> _o(Offset o) => [_r(o.dx), _r(o.dy)];
Offset _p(Object? l) {
  final list = l as List;
  return Offset((list[0] as num).toDouble(), (list[1] as num).toDouble());
}

List<double> _flat(List<Offset> pts) => [for (final p in pts) ...[_r(p.dx), _r(p.dy)]];
List<Offset> _unflat(Object? l) {
  final list = (l as List?) ?? const [];
  return [
    for (var i = 0; i + 1 < list.length; i += 2)
      Offset((list[i] as num).toDouble(), (list[i + 1] as num).toDouble()),
  ];
}

double _d(Object? v, [double fallback = 0]) => (v as num?)?.toDouble() ?? fallback;

// ---------------------------------------------------------------------------
// Entities
// ---------------------------------------------------------------------------

/// A drawable object. Coordinates are world units with +Y pointing up, as in
/// CAD. [weight] is a world-unit width for ink and a screen-pixel line
/// weight for CAD objects.
abstract class Entity {
  Entity({String? id, required this.layerId, this.color, this.weight = 1.4}) : id = id ?? newId();

  String id;
  String layerId;

  /// ARGB colour, or null for "ByLayer".
  int? color;
  double weight;

  String get type;
  String get label;
  Rect get bounds;

  /// Points that can be dragged to edit the object (AutoCAD grips).
  List<Offset> get grips => const [];
  void moveGrip(int index, Offset to) {}

  /// Applies a similarity transform. [f] maps points; [rotation] and [scale]
  /// describe the same transform for sizes and angles.
  void transform(Offset Function(Offset) f, {double rotation = 0, double scale = 1, bool mirror = false});

  double distanceTo(Offset p);

  /// Representative points, used by lasso and window selection.
  List<Offset> samples();

  /// Straight pieces, used by crossing selection and intersection snaps.
  List<Seg> get segments => const [];

  /// Circle carrying this object, used by intersection snaps.
  (Offset, double)? get circle => null;

  List<Offset> get endPoints => const [];
  List<Offset> get midPoints => const [];
  List<Offset> get centers => const [];
  List<Offset> get quadrants => const [];

  /// Key measurements for the properties panel.
  List<(String, String)> describe(String Function(double) len) => const [];

  Entity clone();

  Entity duplicate() => clone()..id = newId();

  Map<String, dynamic> data();

  Map<String, dynamic> toJson() => {
        't': type,
        'id': id,
        'l': layerId,
        if (color != null) 'c': color,
        'w': _r(weight),
        ...data(),
      };

  static Entity? fromJson(Map<String, dynamic> j) {
    final id = j['id'] as String?;
    final layer = (j['l'] as String?) ?? '0';
    final color = j['c'] as int?;
    final w = _d(j['w'], 1.4);
    switch (j['t']) {
      case 'ink':
        return InkStroke(
          id: id,
          layerId: layer,
          color: color ?? 0xFF000000,
          weight: w,
          pen: InkPen.values.asNameMap()[j['pen']] ?? InkPen.pen,
          points: _unflat(j['p']),
          pressures: [for (final v in (j['pr'] as List?) ?? const []) (v as num).toDouble()],
        );
      case 'line':
        return LineEnt(id: id, layerId: layer, color: color, weight: w, a: _p(j['a']), b: _p(j['b']));
      case 'poly':
        return PolyEnt(
            id: id, layerId: layer, color: color, weight: w, points: _unflat(j['p']), closed: j['closed'] == true);
      case 'circle':
        return CircleEnt(id: id, layerId: layer, color: color, weight: w, c: _p(j['c0']), r: _d(j['r']));
      case 'arc':
        return ArcEnt(
            id: id,
            layerId: layer,
            color: color,
            weight: w,
            c: _p(j['c0']),
            r: _d(j['r']),
            start: _d(j['s']),
            sweep: _d(j['sw']));
      case 'text':
        return TextEnt(
            id: id,
            layerId: layer,
            color: color,
            weight: w,
            pos: _p(j['p']),
            text: (j['tx'] as String?) ?? '',
            height: _d(j['h'], 10),
            angle: _d(j['a']));
      case 'dim':
        return DimEnt(id: id, layerId: layer, color: color, weight: w, a: _p(j['a']), b: _p(j['b']), loc: _p(j['loc']));
    }
    return null;
  }
}

/// Freehand handwriting stroke (Samsung Notes style pens).
class InkStroke extends Entity {
  InkStroke({
    super.id,
    required super.layerId,
    required int color,
    super.weight = 3,
    this.pen = InkPen.pen,
    List<Offset>? points,
    List<double>? pressures,
  })  : points = points ?? [],
        pressures = pressures ?? [],
        super(color: color);

  InkPen pen;
  List<Offset> points;

  /// Per-point pen pressure (0–1). Empty when the device has no pressure.
  List<double> pressures;

  @override
  String get type => 'ink';
  @override
  String get label => switch (pen) {
        InkPen.pen => 'Pen stroke',
        InkPen.pencil => 'Pencil stroke',
        InkPen.highlighter => 'Highlighter',
      };

  @override
  Rect get bounds => boundsOf(points).inflate(weight / 2);

  @override
  void transform(Offset Function(Offset) f, {double rotation = 0, double scale = 1, bool mirror = false}) {
    points = points.map(f).toList();
    weight *= scale;
  }

  @override
  double distanceTo(Offset p) {
    if (points.isEmpty) return double.infinity;
    if (points.length == 1) return math.max(0, (p - points.first).distance - weight / 2);
    var best = double.infinity;
    for (var i = 0; i + 1 < points.length; i++) {
      best = math.min(best, distToSegment(p, points[i], points[i + 1]));
    }
    return math.max(0, best - weight / 2);
  }

  @override
  List<Offset> samples() => points;

  @override
  List<Seg> get segments => [for (var i = 0; i + 1 < points.length; i++) (points[i], points[i + 1])];

  @override
  InkStroke clone() => InkStroke(
      id: id,
      layerId: layerId,
      color: color!,
      weight: weight,
      pen: pen,
      points: List.of(points),
      pressures: List.of(pressures));

  @override
  Map<String, dynamic> data() => {
        'pen': pen.name,
        'p': _flat(points),
        if (pressures.isNotEmpty) 'pr': [for (final v in pressures) (v * 100).roundToDouble() / 100],
      };
}

class LineEnt extends Entity {
  LineEnt({super.id, required super.layerId, super.color, super.weight, required this.a, required this.b});

  Offset a;
  Offset b;

  Offset get mid => (a + b) / 2;
  double get length => (b - a).distance;

  @override
  String get type => 'line';
  @override
  String get label => 'Line';
  @override
  Rect get bounds => Rect.fromPoints(a, b);

  @override
  List<Offset> get grips => [a, mid, b];

  @override
  void moveGrip(int index, Offset to) {
    switch (index) {
      case 0:
        a = to;
      case 1:
        final d = to - mid;
        a += d;
        b += d;
      case 2:
        b = to;
    }
  }

  @override
  void transform(Offset Function(Offset) f, {double rotation = 0, double scale = 1, bool mirror = false}) {
    a = f(a);
    b = f(b);
  }

  @override
  double distanceTo(Offset p) => distToSegment(p, a, b);

  @override
  List<Offset> samples() => [a, mid, b];

  @override
  List<Seg> get segments => [(a, b)];

  @override
  List<Offset> get endPoints => [a, b];
  @override
  List<Offset> get midPoints => [mid];

  @override
  List<(String, String)> describe(String Function(double) len) {
    var deg = normAngle(angleOf(a, b)) * 180 / math.pi;
    if (deg >= 359.995) deg = 0;
    return [
      ('Length', len(length)),
      ('Angle', '${deg.toStringAsFixed(1)}°'),
      ('Delta X', len(b.dx - a.dx)),
      ('Delta Y', len(b.dy - a.dy)),
    ];
  }

  @override
  LineEnt clone() => LineEnt(id: id, layerId: layerId, color: color, weight: weight, a: a, b: b);

  @override
  Map<String, dynamic> data() => {'a': _o(a), 'b': _o(b)};
}

/// Polyline; rectangles are closed polylines, as in AutoCAD.
class PolyEnt extends Entity {
  PolyEnt({super.id, required super.layerId, super.color, super.weight, required this.points, this.closed = false});

  List<Offset> points;
  bool closed;

  @override
  String get type => 'poly';
  @override
  String get label => closed && points.length == 4 && _isRect ? 'Rectangle' : 'Polyline';

  bool get _isRect {
    for (var i = 0; i < 4; i++) {
      final a = points[i], b = points[(i + 1) % 4], c = points[(i + 2) % 4];
      if (dot(b - a, c - b).abs() > 1e-6 * ((b - a).distance * (c - b).distance + 1)) return false;
    }
    return true;
  }

  @override
  Rect get bounds => boundsOf(points);

  @override
  List<Offset> get grips => points;

  @override
  void moveGrip(int index, Offset to) => points[index] = to;

  @override
  void transform(Offset Function(Offset) f, {double rotation = 0, double scale = 1, bool mirror = false}) {
    points = points.map(f).toList();
  }

  @override
  List<Seg> get segments => [
        for (var i = 0; i + 1 < points.length; i++) (points[i], points[i + 1]),
        if (closed && points.length > 2) (points.last, points.first),
      ];

  @override
  double distanceTo(Offset p) {
    if (points.length == 1) return (p - points.first).distance;
    var best = double.infinity;
    for (final s in segments) {
      best = math.min(best, distToSegment(p, s.$1, s.$2));
    }
    return best;
  }

  @override
  List<Offset> samples() => [...points, for (final s in segments) (s.$1 + s.$2) / 2];

  @override
  List<Offset> get endPoints => points;
  @override
  List<Offset> get midPoints => [for (final s in segments) (s.$1 + s.$2) / 2];

  @override
  List<(String, String)> describe(String Function(double) len) => [
        ('Vertices', '${points.length}'),
        ('Length', len(polylineLength(points, closed: closed))),
        if (closed) ('Area', '${len(polygonArea(points).abs())}²'),
        if (closed && label == 'Rectangle') ...[
          ('Width', len((points[1] - points[0]).distance)),
          ('Height', len((points[2] - points[1]).distance)),
        ],
      ];

  @override
  PolyEnt clone() =>
      PolyEnt(id: id, layerId: layerId, color: color, weight: weight, points: List.of(points), closed: closed);

  @override
  Map<String, dynamic> data() => {'p': _flat(points), if (closed) 'closed': true};
}

class CircleEnt extends Entity {
  CircleEnt({super.id, required super.layerId, super.color, super.weight, required this.c, required this.r});

  Offset c;
  double r;

  @override
  String get type => 'circle';
  @override
  String get label => 'Circle';
  @override
  Rect get bounds => Rect.fromCircle(center: c, radius: r);

  @override
  List<Offset> get grips => [c, ...quadrants];

  @override
  void moveGrip(int index, Offset to) {
    if (index == 0) {
      c = to;
    } else {
      r = (to - c).distance;
    }
  }

  @override
  void transform(Offset Function(Offset) f, {double rotation = 0, double scale = 1, bool mirror = false}) {
    c = f(c);
    r *= scale;
  }

  @override
  double distanceTo(Offset p) => ((p - c).distance - r).abs();

  @override
  List<Offset> samples() => [for (var i = 0; i < 16; i++) polar(c, r, i * kTwoPi / 16)];

  @override
  (Offset, double)? get circle => (c, r);

  @override
  List<Offset> get centers => [c];
  @override
  List<Offset> get quadrants => [c + Offset(r, 0), c + Offset(0, r), c - Offset(r, 0), c - Offset(0, r)];

  @override
  List<(String, String)> describe(String Function(double) len) => [
        ('Radius', len(r)),
        ('Diameter', len(r * 2)),
        ('Circumference', len(kTwoPi * r)),
        ('Area', '${len(math.pi * r * r)}²'),
      ];

  @override
  CircleEnt clone() => CircleEnt(id: id, layerId: layerId, color: color, weight: weight, c: c, r: r);

  @override
  Map<String, dynamic> data() => {'c0': _o(c), 'r': _r(r)};
}

class ArcEnt extends Entity {
  ArcEnt({
    super.id,
    required super.layerId,
    super.color,
    super.weight,
    required this.c,
    required this.r,
    required this.start,
    required this.sweep,
  });

  /// Arc through three points, or null when they are collinear.
  static ArcEnt? through(Offset p1, Offset p2, Offset p3, {required String layerId, int? color, double weight = 1.4}) {
    final circ = circleFrom3(p1, p2, p3);
    if (circ == null) return null;
    final (center, radius) = circ;
    final a1 = angleOf(center, p1);
    final a2 = angleOf(center, p2);
    final a3 = angleOf(center, p3);
    final ccw = normAngle(a3 - a1);
    final sweep = normAngle(a2 - a1) < ccw ? ccw : ccw - kTwoPi;
    return ArcEnt(layerId: layerId, color: color, weight: weight, c: center, r: radius, start: a1, sweep: sweep);
  }

  Offset c;
  double r;

  /// Start angle and signed sweep in radians (positive = counter-clockwise).
  double start;
  double sweep;

  Offset get p1 => polar(c, r, start);
  Offset get p2 => polar(c, r, start + sweep);
  Offset get mid => polar(c, r, start + sweep / 2);

  @override
  String get type => 'arc';
  @override
  String get label => 'Arc';

  @override
  Rect get bounds => boundsOf([p1, p2, ...quadrants]);

  @override
  List<Offset> get grips => [p1, mid, p2, c];

  @override
  void moveGrip(int index, Offset to) {
    if (index == 3) {
      c = to;
      return;
    }
    final pts = [p1, mid, p2];
    pts[index] = to;
    final a = ArcEnt.through(pts[0], pts[1], pts[2], layerId: layerId);
    if (a != null) _setFrom(a);
  }

  void _setFrom(ArcEnt a) {
    c = a.c;
    r = a.r;
    start = a.start;
    sweep = a.sweep;
  }

  @override
  void transform(Offset Function(Offset) f, {double rotation = 0, double scale = 1, bool mirror = false}) {
    final a = ArcEnt.through(f(p1), f(mid), f(p2), layerId: layerId);
    if (a != null) _setFrom(a);
  }

  @override
  double distanceTo(Offset p) {
    if (angleInSweep(angleOf(c, p), start, sweep)) return ((p - c).distance - r).abs();
    return math.min((p - p1).distance, (p - p2).distance);
  }

  @override
  List<Offset> samples() => [for (var i = 0; i <= 12; i++) polar(c, r, start + sweep * i / 12)];

  @override
  (Offset, double)? get circle => (c, r);

  @override
  List<Offset> get endPoints => [p1, p2];
  @override
  List<Offset> get midPoints => [mid];
  @override
  List<Offset> get centers => [c];
  @override
  List<Offset> get quadrants => [
        for (var i = 0; i < 4; i++)
          if (angleInSweep(i * math.pi / 2, start, sweep)) polar(c, r, i * math.pi / 2),
      ];

  @override
  List<(String, String)> describe(String Function(double) len) => [
        ('Radius', len(r)),
        ('Arc length', len(sweep.abs() * r)),
        ('Included angle', '${(sweep.abs() * 180 / math.pi).toStringAsFixed(1)}°'),
      ];

  @override
  ArcEnt clone() =>
      ArcEnt(id: id, layerId: layerId, color: color, weight: weight, c: c, r: r, start: start, sweep: sweep);

  @override
  Map<String, dynamic> data() => {'c0': _o(c), 'r': _r(r), 's': _r(start), 'sw': _r(sweep)};
}

class TextEnt extends Entity {
  TextEnt({
    super.id,
    required super.layerId,
    super.color,
    super.weight,
    required this.pos,
    required this.text,
    required this.height,
    this.angle = 0,
  });

  /// Bottom-left of the first line's baseline.
  Offset pos;
  String text;
  double height;
  double angle;

  List<String> get lines => text.split('\n');

  /// Approximate size: width from character count, like a CAD text box.
  Size get size {
    final longest = lines.fold<int>(0, (m, l) => math.max(m, l.length));
    return Size(math.max(1, longest) * height * 0.72, height * (1 + (lines.length - 1) * 1.4));
  }

  List<Offset> get corners {
    final s = size;
    // The first line's baseline is at pos; extra lines go downwards.
    final down = height * 1.4 * (lines.length - 1);
    final local = [
      Offset(0, -down - height * 0.25),
      Offset(s.width, -down - height * 0.25),
      Offset(s.width, height),
      Offset(0, height),
    ];
    return [for (final p in local) pos + Offset(p.dx * math.cos(angle) - p.dy * math.sin(angle), p.dx * math.sin(angle) + p.dy * math.cos(angle))];
  }

  @override
  String get type => 'text';
  @override
  String get label => 'Text';
  @override
  Rect get bounds => boundsOf(corners);

  @override
  List<Offset> get grips => [pos];
  @override
  void moveGrip(int index, Offset to) => pos = to;

  @override
  void transform(Offset Function(Offset) f, {double rotation = 0, double scale = 1, bool mirror = false}) {
    pos = f(pos);
    height *= scale;
    if (!mirror) angle += rotation;
  }

  @override
  double distanceTo(Offset p) {
    final cs = corners;
    if (pointInPolygon(p, cs)) return 0;
    var best = double.infinity;
    for (var i = 0; i < 4; i++) {
      best = math.min(best, distToSegment(p, cs[i], cs[(i + 1) % 4]));
    }
    return best;
  }

  @override
  List<Offset> samples() => [...corners, pos];

  @override
  List<Seg> get segments {
    final cs = corners;
    return [for (var i = 0; i < 4; i++) (cs[i], cs[(i + 1) % 4])];
  }

  @override
  List<Offset> get endPoints => [pos];

  @override
  List<(String, String)> describe(String Function(double) len) => [
        ('Height', len(height)),
        ('Rotation', '${(angle * 180 / math.pi).toStringAsFixed(1)}°'),
      ];

  @override
  TextEnt clone() => TextEnt(
      id: id, layerId: layerId, color: color, weight: weight, pos: pos, text: text, height: height, angle: angle);

  @override
  Map<String, dynamic> data() => {'p': _o(pos), 'tx': text, 'h': _r(height), if (angle != 0) 'a': _r(angle)};
}

/// Aligned dimension between [a] and [b], with its dimension line passing
/// through [loc].
class DimEnt extends Entity {
  DimEnt({super.id, required super.layerId, super.color, super.weight, required this.a, required this.b, required this.loc});

  Offset a;
  Offset b;
  Offset loc;

  double get value => (b - a).distance;

  /// Dimension line end points and the unit normal pointing to the dim line.
  (Offset, Offset, Offset) get dimLine {
    final d = b - a;
    final len = d.distance;
    final u = len == 0 ? const Offset(1, 0) : d / len;
    var n = Offset(-u.dy, u.dx);
    final off = dot(loc - a, n);
    if (off < 0) n = -n;
    return (a + n * off.abs(), b + n * off.abs(), n);
  }

  @override
  String get type => 'dim';
  @override
  String get label => 'Dimension';
  @override
  Rect get bounds {
    final (d1, d2, _) = dimLine;
    return boundsOf([a, b, d1, d2]);
  }

  @override
  List<Offset> get grips {
    final (d1, d2, _) = dimLine;
    return [a, b, (d1 + d2) / 2];
  }

  @override
  void moveGrip(int index, Offset to) {
    switch (index) {
      case 0:
        a = to;
      case 1:
        b = to;
      case 2:
        loc = to;
    }
  }

  @override
  void transform(Offset Function(Offset) f, {double rotation = 0, double scale = 1, bool mirror = false}) {
    a = f(a);
    b = f(b);
    loc = f(loc);
  }

  @override
  double distanceTo(Offset p) {
    final (d1, d2, _) = dimLine;
    return [
      distToSegment(p, d1, d2),
      distToSegment(p, a, d1),
      distToSegment(p, b, d2),
    ].reduce(math.min);
  }

  @override
  List<Offset> samples() {
    final (d1, d2, _) = dimLine;
    return [a, b, d1, d2, (d1 + d2) / 2];
  }

  @override
  List<Offset> get endPoints {
    final (d1, d2, _) = dimLine;
    return [d1, d2];
  }

  @override
  List<(String, String)> describe(String Function(double) len) => [('Measurement', len(value))];

  @override
  DimEnt clone() => DimEnt(id: id, layerId: layerId, color: color, weight: weight, a: a, b: b, loc: loc);

  @override
  Map<String, dynamic> data() => {'a': _o(a), 'b': _o(b), 'loc': _o(loc)};
}

// ---------------------------------------------------------------------------
// Layers and drawings
// ---------------------------------------------------------------------------

class CadLayer {
  CadLayer({String? id, required this.name, this.color = kAdaptiveColor, this.visible = true, this.locked = false})
      : id = id ?? newId();

  final String id;
  String name;
  int color;
  bool visible;
  bool locked;

  CadLayer clone() => CadLayer(id: id, name: name, color: color, visible: visible, locked: locked);

  Map<String, dynamic> toJson() => {'id': id, 'name': name, 'color': color, 'visible': visible, 'locked': locked};

  factory CadLayer.fromJson(Map<String, dynamic> j) => CadLayer(
        id: j['id'] as String?,
        name: (j['name'] as String?) ?? 'Layer',
        color: (j['color'] as int?) ?? kAdaptiveColor,
        visible: j['visible'] != false,
        locked: j['locked'] == true,
      );
}

/// A sketch embedded in a note: layers, objects and CAD drafting settings.
class Drawing {
  Drawing({
    String? id,
    this.paper = PaperStyle.model,
    this.units = DrawUnits.mm,
    this.precision = 0,
    this.gridSize = 10,
    this.textHeight = 25,
    List<CadLayer>? layers,
    String? currentLayerId,
    List<Entity>? entities,
    this.gridOn = true,
    this.snapOn = false,
    this.orthoOn = false,
    this.osnapOn = true,
    this.viewCenter,
    this.viewZoom,
  })  : id = id ?? newId(),
        layers = layers ?? [CadLayer(id: '0', name: '0')],
        entities = entities ?? [] {
    this.currentLayerId = currentLayerId ?? this.layers.first.id;
  }

  /// A CAD sheet: dark model space, grid on, object snaps on.
  factory Drawing.cad() => Drawing(
        paper: PaperStyle.model,
        gridSize: 100,
        textHeight: 250,
        layers: [
          CadLayer(id: '0', name: '0'),
          CadLayer(name: 'Walls', color: 0xFFF2F4F7),
          CadLayer(name: 'Dimensions', color: 0xFF4FC3F7),
          CadLayer(name: 'Notes', color: 0xFFFFD54F),
        ],
        viewZoom: 0.08,
        viewCenter: const Offset(4000, 2500),
      );

  /// A handwriting page: lined paper, grid and snaps off.
  factory Drawing.ink({PaperStyle paper = PaperStyle.lined}) => Drawing(
        paper: paper,
        units: DrawUnits.none,
        gridSize: 20,
        textHeight: 24,
        gridOn: false,
        osnapOn: false,
        viewZoom: 1,
        viewCenter: const Offset(400, -300),
      );

  final String id;
  PaperStyle paper;
  DrawUnits units;

  /// Decimal places shown for lengths.
  int precision;
  double gridSize;
  double textHeight;
  List<CadLayer> layers;
  late String currentLayerId;
  List<Entity> entities;
  bool gridOn;
  bool snapOn;
  bool orthoOn;
  bool osnapOn;
  Offset? viewCenter;
  double? viewZoom;

  CadLayer layer(String id) => layers.firstWhere((l) => l.id == id, orElse: () => layers.first);
  CadLayer get currentLayer => layer(currentLayerId);

  bool get isEmpty => entities.isEmpty;

  Rect? get extents {
    Rect? r;
    for (final e in entities) {
      if (!layer(e.layerId).visible) continue;
      r = unionRect(r, e.bounds);
    }
    return r;
  }

  Color resolveColor(Entity e) {
    final c = e.color ?? layer(e.layerId).color;
    return c == kAdaptiveColor ? paper.foreground : Color(c);
  }

  String formatLength(double v, {bool withUnits = false}) {
    final s = v.toStringAsFixed(precision);
    return withUnits && units.suffix.isNotEmpty ? '$s ${units.suffix}' : s;
  }

  Map<String, dynamic> toJson() => {
        'id': id,
        'paper': paper.name,
        'units': units.name,
        'precision': precision,
        'grid': gridSize,
        'textH': textHeight,
        'layers': [for (final l in layers) l.toJson()],
        'cur': currentLayerId,
        'ents': [for (final e in entities) e.toJson()],
        'gridOn': gridOn,
        'snapOn': snapOn,
        'orthoOn': orthoOn,
        'osnapOn': osnapOn,
        if (viewCenter != null) 'vc': _o(viewCenter!),
        if (viewZoom != null) 'vz': viewZoom,
      };

  factory Drawing.fromJson(Map<String, dynamic> j) {
    final layers = [for (final l in (j['layers'] as List?) ?? const []) CadLayer.fromJson((l as Map).cast())];
    return Drawing(
      id: j['id'] as String?,
      paper: PaperStyle.values.asNameMap()[j['paper']] ?? PaperStyle.model,
      units: DrawUnits.values.asNameMap()[j['units']] ?? DrawUnits.mm,
      precision: (j['precision'] as int?) ?? 0,
      gridSize: _d(j['grid'], 10),
      textHeight: _d(j['textH'], 25),
      layers: layers.isEmpty ? null : layers,
      currentLayerId: j['cur'] as String?,
      entities: [
        for (final e in (j['ents'] as List?) ?? const [])
          ?Entity.fromJson((e as Map).cast()),
      ],
      gridOn: j['gridOn'] != false,
      snapOn: j['snapOn'] == true,
      orthoOn: j['orthoOn'] == true,
      osnapOn: j['osnapOn'] != false,
      viewCenter: j['vc'] == null ? null : _p(j['vc']),
      viewZoom: (j['vz'] as num?)?.toDouble(),
    );
  }

  Drawing copy() => Drawing.fromJson(toJson());
}
