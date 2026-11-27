import 'dart:math' as math;
import 'dart:ui';

import '../models/drawing.dart';
import 'canvas_controller.dart';
import 'geometry.dart';

/// A keyword option shown as a chip and accepted from the command line.
class CmdOption {
  const CmdOption(this.label, this.key);
  final String label;
  final String key;
}

/// A running CAD command, AutoCAD style: it asks for points with a prompt,
/// accepts typed coordinates, numbers and keywords, and previews its result.
abstract class CadCommand {
  CadCommand(this.c);

  final CanvasController c;
  final List<Offset> pts = [];

  String get name;
  String get prompt;
  List<CmdOption> get options => const [];

  /// The reference point for relative input, ortho and direct distance.
  Offset? get base => pts.isEmpty ? null : pts.last;

  /// Whether a finished command restarts while its tool stays selected.
  bool get repeats => true;

  /// Whether dragging from somewhere else starts a new chain (touch drawing).
  bool get dragRestart => false;

  /// Whether the next pick selects an object instead of a point.
  bool get picksObject => false;

  bool get fillPreview => false;

  void pick(Offset p);
  bool number(double v) => false;
  bool keyword(String k) => false;
  void enter() => done();
  void done() => c.endCommand(this);

  List<Entity> preview(Offset cursor) => const [];
  List<Seg> guides(Offset cursor) => base == null ? const [] : [(base!, cursor)];

  String? dynLabel(Offset cursor) {
    final b = base;
    if (b == null) return null;
    var deg = normAngle(angleOf(b, cursor)) * 180 / math.pi;
    if (deg >= 359.95) deg = 0;
    return '${c.drawing.formatLength((cursor - b).distance)}  <  ${deg.toStringAsFixed(0)}°';
  }

  String get _layer => c.drawing.currentLayerId;
  double get _weight => c.lineWeight;
}

// ---------------------------------------------------------------------------
// Draw
// ---------------------------------------------------------------------------

class LineCmd extends CadCommand {
  LineCmd(super.c);

  final List<String> _made = [];

  @override
  String get name => 'LINE';
  @override
  String get prompt => pts.isEmpty ? 'Specify first point' : 'Specify next point';
  @override
  List<CmdOption> get options => [
        if (pts.isNotEmpty) const CmdOption('Undo', 'U'),
        if (_made.length >= 2) const CmdOption('Close', 'C'),
      ];
  @override
  bool get dragRestart => true;

  @override
  void pick(Offset p) {
    if (pts.isNotEmpty) {
      if (p == pts.last) return;
      final e = LineEnt(layerId: _layer, weight: _weight, a: pts.last, b: p);
      c.addEntity(e);
      _made.add(e.id);
    }
    pts.add(p);
  }

  @override
  bool keyword(String k) {
    if (k == 'C' && _made.length >= 2) {
      c.addEntity(LineEnt(layerId: _layer, weight: _weight, a: pts.last, b: pts.first));
      done();
      return true;
    }
    if (k == 'U' && pts.isNotEmpty) {
      if (_made.isNotEmpty) c.removeEntityById(_made.removeLast());
      pts.removeLast();
      return true;
    }
    return false;
  }

  @override
  List<Entity> preview(Offset cursor) =>
      pts.isEmpty ? const [] : [LineEnt(layerId: _layer, weight: _weight, a: pts.last, b: cursor)];

  @override
  List<Seg> guides(Offset cursor) => const [];
}

class PlineCmd extends CadCommand {
  PlineCmd(super.c);

  @override
  String get name => 'PLINE';
  @override
  String get prompt => pts.isEmpty ? 'Specify start point' : 'Specify next point or press Enter to finish';
  @override
  List<CmdOption> get options => [
        if (pts.isNotEmpty) const CmdOption('Undo', 'U'),
        if (pts.length >= 3) const CmdOption('Close', 'C'),
      ];
  @override
  bool get dragRestart => true;

  @override
  void pick(Offset p) {
    if (pts.isEmpty || pts.last != p) pts.add(p);
  }

  void _create(bool closed) {
    if (pts.length >= 2) {
      c.addEntity(PolyEnt(layerId: _layer, weight: _weight, points: List.of(pts), closed: closed));
    }
  }

  @override
  bool keyword(String k) {
    if (k == 'C' && pts.length >= 3) {
      _create(true);
      done();
      return true;
    }
    if (k == 'U' && pts.isNotEmpty) {
      pts.removeLast();
      return true;
    }
    return false;
  }

  @override
  void enter() {
    _create(false);
    done();
  }

  @override
  List<Entity> preview(Offset cursor) =>
      pts.isEmpty ? const [] : [PolyEnt(layerId: _layer, weight: _weight, points: [...pts, cursor])];

  @override
  List<Seg> guides(Offset cursor) => const [];
}

class RectCmd extends CadCommand {
  RectCmd(super.c);

  @override
  String get name => 'RECTANG';
  @override
  String get prompt => pts.isEmpty ? 'Specify first corner point' : 'Specify other corner point (or @width,height)';

  static List<Offset> corners(Offset a, Offset b) => [a, Offset(b.dx, a.dy), b, Offset(a.dx, b.dy)];

  @override
  void pick(Offset p) {
    if (pts.isEmpty) {
      pts.add(p);
      return;
    }
    if (p.dx != pts.first.dx && p.dy != pts.first.dy) {
      c.addEntity(PolyEnt(layerId: _layer, weight: _weight, points: corners(pts.first, p), closed: true));
    }
    done();
  }

  @override
  List<Entity> preview(Offset cursor) => pts.isEmpty
      ? const []
      : [PolyEnt(layerId: _layer, weight: _weight, points: corners(pts.first, cursor), closed: true)];

  @override
  List<Seg> guides(Offset cursor) => const [];

  @override
  String? dynLabel(Offset cursor) {
    if (pts.isEmpty) return null;
    final d = cursor - pts.first;
    return '${c.drawing.formatLength(d.dx.abs())} × ${c.drawing.formatLength(d.dy.abs())}';
  }
}

class CircleCmd extends CadCommand {
  CircleCmd(super.c);

  bool _diameter = false;

  @override
  String get name => 'CIRCLE';
  @override
  String get prompt => pts.isEmpty
      ? 'Specify center point'
      : (_diameter ? 'Specify diameter' : 'Specify radius (or D for diameter)');
  @override
  List<CmdOption> get options => pts.isEmpty ? const [] : [CmdOption(_diameter ? 'Radius' : 'Diameter', 'D')];

  void _make(double r) {
    if (r > 0) c.addEntity(CircleEnt(layerId: _layer, weight: _weight, c: pts.first, r: r));
    done();
  }

  @override
  void pick(Offset p) {
    if (pts.isEmpty) {
      pts.add(p);
    } else {
      final d = (p - pts.first).distance;
      _make(_diameter ? d / 2 : d);
    }
  }

  @override
  bool number(double v) {
    if (pts.isEmpty) return false;
    _make(_diameter ? v / 2 : v);
    return true;
  }

  @override
  bool keyword(String k) {
    if (k == 'D' && pts.isNotEmpty) {
      _diameter = !_diameter;
      return true;
    }
    return false;
  }

  @override
  List<Entity> preview(Offset cursor) {
    if (pts.isEmpty) return const [];
    final d = (cursor - pts.first).distance;
    return [CircleEnt(layerId: _layer, weight: _weight, c: pts.first, r: _diameter ? d / 2 : d)];
  }

  @override
  String? dynLabel(Offset cursor) {
    if (pts.isEmpty) return null;
    final d = (cursor - pts.first).distance;
    return _diameter ? 'Ø ${c.drawing.formatLength(d)}' : 'R ${c.drawing.formatLength(d)}';
  }
}

class ArcCmd extends CadCommand {
  ArcCmd(super.c);

  @override
  String get name => 'ARC';
  @override
  String get prompt => switch (pts.length) {
        0 => 'Specify start point of arc',
        1 => 'Specify second point of arc',
        _ => 'Specify end point of arc',
      };

  @override
  void pick(Offset p) {
    if (pts.isNotEmpty && pts.last == p) return;
    pts.add(p);
    if (pts.length == 3) {
      final a = ArcEnt.through(pts[0], pts[1], pts[2], layerId: _layer, weight: _weight);
      if (a != null) c.addEntity(a);
      done();
    }
  }

  @override
  List<Entity> preview(Offset cursor) {
    if (pts.length == 1) return [LineEnt(layerId: _layer, weight: _weight, a: pts[0], b: cursor)];
    if (pts.length == 2) {
      final a = ArcEnt.through(pts[0], pts[1], cursor, layerId: _layer, weight: _weight);
      return a == null ? const [] : [a];
    }
    return const [];
  }

  @override
  List<Seg> guides(Offset cursor) => const [];
}

class TextCmd extends CadCommand {
  TextCmd(super.c);

  bool _waiting = false;

  @override
  String get name => 'TEXT';
  @override
  String get prompt => _waiting ? 'Enter text' : 'Specify start point of text';
  @override
  List<Seg> guides(Offset cursor) => const [];
  @override
  String? dynLabel(Offset cursor) => null;

  @override
  void pick(Offset p) {
    if (_waiting) return;
    _waiting = true;
    c.refresh();
    c.askText('', c.drawing.textHeight).then((r) {
      _waiting = false;
      if (r != null && r.$1.trim().isNotEmpty) {
        c.drawing.textHeight = r.$2;
        final color = c.drawing.paper.isDark ? null : c.pens[Tool.pen]!.color;
        c.addEntity(TextEnt(layerId: _layer, color: color, pos: p, text: r.$1, height: r.$2));
      }
      if (identical(c.command, this)) done();
    });
  }
}

class DimCmd extends CadCommand {
  DimCmd(super.c);

  @override
  String get name => 'DIMALIGNED';
  @override
  String get prompt => switch (pts.length) {
        0 => 'Specify first extension line origin',
        1 => 'Specify second extension line origin',
        _ => 'Specify dimension line location',
      };

  @override
  void pick(Offset p) {
    if (pts.length == 1 && pts.first == p) return;
    pts.add(p);
    if (pts.length == 3) {
      c.addEntity(DimEnt(layerId: _layer, weight: 1, a: pts[0], b: pts[1], loc: pts[2]));
      done();
    }
  }

  @override
  bool number(double v) {
    // A typed offset distance places the dimension line on the cursor side.
    if (pts.length != 2) return false;
    final d = pts[1] - pts[0];
    if (d.distance == 0) return false;
    var n = Offset(-d.dy, d.dx) / d.distance;
    final cur = c.cursorWorld;
    if (cur != null && dot(cur - pts[0], n) < 0) n = -n;
    pick(pts[0] + n * v);
    return true;
  }

  @override
  List<Entity> preview(Offset cursor) {
    if (pts.length == 1) return [DimEnt(layerId: _layer, weight: 1, a: pts[0], b: cursor, loc: cursor)];
    if (pts.length == 2) return [DimEnt(layerId: _layer, weight: 1, a: pts[0], b: pts[1], loc: cursor)];
    return const [];
  }

  @override
  List<Seg> guides(Offset cursor) => const [];
}

class DistCmd extends CadCommand {
  DistCmd(super.c);

  @override
  String get name => 'DIST';
  @override
  String get prompt => pts.isEmpty ? 'Specify first point' : 'Specify second point';

  @override
  void pick(Offset p) {
    pts.add(p);
    if (pts.length == 2) {
      final a = pts[0], b = pts[1];
      var deg = normAngle(angleOf(a, b)) * 180 / math.pi;
      if (deg >= 359.95) deg = 0;
      final f = c.drawing.formatLength;
      c.say('Distance = ${f((b - a).distance, withUnits: true)},  Angle = ${deg.toStringAsFixed(1)}°,  '
          'ΔX = ${f(b.dx - a.dx)},  ΔY = ${f(b.dy - a.dy)}');
      c.measurement = [a, b];
      done();
    }
  }
}

class AreaCmd extends CadCommand {
  AreaCmd(super.c);

  @override
  String get name => 'AREA';
  @override
  String get prompt => pts.length < 3 ? 'Specify corner points' : 'Specify next corner or press Enter for total';
  @override
  bool get fillPreview => true;
  @override
  List<CmdOption> get options => [if (pts.isNotEmpty) const CmdOption('Undo', 'U')];

  @override
  void pick(Offset p) => pts.add(p);

  @override
  bool keyword(String k) {
    if (k == 'U' && pts.isNotEmpty) {
      pts.removeLast();
      return true;
    }
    return false;
  }

  @override
  void enter() {
    if (pts.length >= 3) {
      final f = c.drawing.formatLength;
      final u = c.drawing.units.suffix;
      c.say('Area = ${f(polygonArea(pts).abs())}${u.isEmpty ? '' : ' $u²'},  '
          'Perimeter = ${f(polylineLength(pts, closed: true), withUnits: true)}');
      c.measurement = [...pts, pts.first];
    }
    done();
  }

  @override
  List<Entity> preview(Offset cursor) =>
      pts.isEmpty ? const [] : [PolyEnt(layerId: _layer, weight: 1, points: [...pts, cursor], closed: true)];

  @override
  List<Seg> guides(Offset cursor) => const [];
}

// ---------------------------------------------------------------------------
// Modify
// ---------------------------------------------------------------------------

/// Asks the user to pick objects before running a modify command.
class SelectObjectsCmd extends CadCommand {
  SelectObjectsCmd(super.c, this.next);

  final String next;

  @override
  String get name => next;
  @override
  String get prompt => 'Select objects, then press Enter';
  @override
  bool get repeats => false;
  @override
  bool get picksObject => true;
  @override
  List<CmdOption> get options => const [CmdOption('All', 'ALL')];

  @override
  void pick(Offset p) {}

  @override
  bool keyword(String k) {
    if (k == 'ALL') {
      c.selectAll();
      return true;
    }
    return false;
  }

  @override
  void enter() {
    if (c.selection.isEmpty) {
      done();
    } else {
      c.startCommand(next);
    }
  }

  @override
  String? dynLabel(Offset cursor) => null;
}

abstract class ModifyCmd extends CadCommand {
  ModifyCmd(super.c) : targets = c.selectedEntities.map((e) => e.clone()).toList();

  /// Copies of the selection at the start, used to draw ghosts.
  final List<Entity> targets;

  @override
  bool get repeats => false;

  List<Entity> ghosts(void Function(Entity e) apply) {
    final out = [for (final t in targets) t.clone()];
    out.forEach(apply);
    return out;
  }
}

class MoveCmd extends ModifyCmd {
  MoveCmd(super.c, {this.copy = false});

  final bool copy;

  @override
  String get name => copy ? 'COPY' : 'MOVE';
  @override
  String get prompt => pts.isEmpty
      ? 'Specify base point'
      : (copy ? 'Specify second point or press Enter to finish' : 'Specify second point');

  @override
  void pick(Offset p) {
    if (pts.isEmpty) {
      pts.add(p);
      return;
    }
    final d = p - pts.first;
    if (copy) {
      c.addEntities([for (final t in targets) t.duplicate()..transform((q) => q + d)]);
    } else {
      c.transformSelection((q) => q + d);
      done();
    }
  }

  @override
  List<Entity> preview(Offset cursor) {
    if (pts.isEmpty) return const [];
    final d = cursor - pts.first;
    return ghosts((e) => e.transform((q) => q + d));
  }

  @override
  List<Seg> guides(Offset cursor) => pts.isEmpty ? const [] : [(pts.first, cursor)];

  @override
  Offset? get base => pts.isEmpty ? null : pts.first;
}

class RotateCmd extends ModifyCmd {
  RotateCmd(super.c);

  @override
  String get name => 'ROTATE';
  @override
  String get prompt => pts.isEmpty ? 'Specify base point' : 'Specify rotation angle (degrees)';

  void _apply(double a) {
    final b = pts.first;
    c.transformSelection((q) => rotateAround(q, b, a), rotation: a);
    done();
  }

  @override
  void pick(Offset p) {
    if (pts.isEmpty) {
      pts.add(p);
    } else if (p != pts.first) {
      _apply(angleOf(pts.first, p));
    }
  }

  @override
  bool number(double v) {
    if (pts.isEmpty) return false;
    _apply(v * math.pi / 180);
    return true;
  }

  @override
  List<Entity> preview(Offset cursor) {
    if (pts.isEmpty || cursor == pts.first) return const [];
    final b = pts.first;
    final a = angleOf(b, cursor);
    return ghosts((e) => e.transform((q) => rotateAround(q, b, a), rotation: a));
  }

  @override
  String? dynLabel(Offset cursor) {
    if (pts.isEmpty) return null;
    return '${(normAngle(angleOf(pts.first, cursor)) * 180 / math.pi).toStringAsFixed(1)}°';
  }
}

class ScaleCmd extends ModifyCmd {
  ScaleCmd(super.c);

  @override
  String get name => 'SCALE';
  @override
  String get prompt => pts.isEmpty ? 'Specify base point' : 'Specify scale factor (or drag)';

  /// Dragging to the far corner of the selection means a factor of 1.
  double get _ref {
    Rect? r;
    for (final t in targets) {
      r = unionRect(r, t.bounds);
    }
    if (r == null || pts.isEmpty) return 1;
    final b = pts.first;
    final d = [r.topLeft, r.topRight, r.bottomLeft, r.bottomRight].map((p) => (p - b).distance).reduce(math.max);
    return d == 0 ? 1 : d;
  }

  double _factor(Offset cursor) => (cursor - pts.first).distance / _ref;

  void _apply(double f) {
    if (f <= 0) return;
    final b = pts.first;
    c.transformSelection((q) => b + (q - b) * f, scale: f);
    done();
  }

  @override
  void pick(Offset p) {
    if (pts.isEmpty) {
      pts.add(p);
    } else {
      _apply(_factor(p));
    }
  }

  @override
  bool number(double v) {
    if (pts.isEmpty) return false;
    _apply(v);
    return true;
  }

  @override
  List<Entity> preview(Offset cursor) {
    if (pts.isEmpty) return const [];
    final f = _factor(cursor);
    if (f <= 0) return const [];
    final b = pts.first;
    return ghosts((e) => e.transform((q) => b + (q - b) * f, scale: f));
  }

  @override
  String? dynLabel(Offset cursor) => pts.isEmpty ? null : '× ${_factor(cursor).toStringAsFixed(3)}';
}

class MirrorCmd extends ModifyCmd {
  MirrorCmd(super.c);

  bool eraseSource = false;

  @override
  String get name => 'MIRROR';
  @override
  String get prompt => pts.isEmpty ? 'Specify first point of mirror line' : 'Specify second point of mirror line';
  @override
  List<CmdOption> get options => [CmdOption(eraseSource ? 'Erase source: Yes' : 'Erase source: No', 'E')];

  @override
  bool keyword(String k) {
    if (k == 'E' || k == 'Y' || k == 'N') {
      eraseSource = k == 'E' ? !eraseSource : k == 'Y';
      return true;
    }
    return false;
  }

  @override
  void pick(Offset p) {
    if (pts.isEmpty) {
      pts.add(p);
      return;
    }
    if (p == pts.first) return;
    final a = pts.first;
    Offset f(Offset q) => mirrorAcross(q, a, p);
    if (eraseSource) {
      c.transformSelection(f, mirror: true);
    } else {
      c.addEntities([for (final t in targets) t.duplicate()..transform(f, mirror: true)]);
    }
    done();
  }

  @override
  List<Entity> preview(Offset cursor) {
    if (pts.isEmpty || cursor == pts.first) return const [];
    final a = pts.first;
    return ghosts((e) => e.transform((q) => mirrorAcross(q, a, cursor), mirror: true));
  }

  @override
  List<Seg> guides(Offset cursor) => pts.isEmpty ? const [] : [(pts.first, cursor)];
}

/// AutoCAD OFFSET: distance, then object, then side; repeats until Enter.
class OffsetCmd extends CadCommand {
  OffsetCmd(super.c);

  Entity? _target;
  bool _haveDistance = false;

  @override
  String get name => 'OFFSET';
  @override
  bool get repeats => false;
  @override
  String get prompt {
    if (!_haveDistance) return 'Specify offset distance <${c.drawing.formatLength(c.offsetDistance)}>';
    if (_target == null) return 'Select object to offset or press Enter to exit';
    return 'Specify point on side to offset';
  }

  @override
  bool get picksObject => _haveDistance && _target == null;
  @override
  Offset? get base => null;

  @override
  bool number(double v) {
    if (_haveDistance || v <= 0) return false;
    c.offsetDistance = v;
    _haveDistance = true;
    return true;
  }

  @override
  void enter() {
    if (!_haveDistance) {
      _haveDistance = true;
    } else {
      done();
    }
  }

  @override
  void pick(Offset p) {
    if (!_haveDistance) {
      _haveDistance = true;
    }
    if (_target == null) {
      final e = c.hitEntity(p);
      if (e is LineEnt || e is PolyEnt || e is CircleEnt || e is ArcEnt) {
        _target = e;
      } else if (e != null) {
        c.say('That object cannot be offset.');
      }
      return;
    }
    final made = offsetOf(_target!, c.offsetDistance, p);
    if (made != null) c.addEntity(made);
    _target = null;
  }

  @override
  List<Entity> preview(Offset cursor) {
    final t = _target;
    if (t == null) return const [];
    final e = offsetOf(t, c.offsetDistance, cursor);
    return e == null ? const [] : [e];
  }

  @override
  List<Seg> guides(Offset cursor) => const [];
  @override
  String? dynLabel(Offset cursor) => null;

  /// Copy of [e] moved [d] towards [side].
  static Entity? offsetOf(Entity e, double d, Offset side) {
    if (e is LineEnt) {
      final dir = e.b - e.a;
      if (dir.distance == 0) return null;
      var n = Offset(-dir.dy, dir.dx) / dir.distance * d;
      if (cross(dir, side - e.a) < 0) n = -n;
      return e.duplicate()..transform((q) => q + n);
    }
    if (e is CircleEnt || e is ArcEnt) {
      final (center, r) = e.circle!;
      final nr = (side - center).distance > r ? r + d : r - d;
      if (nr <= 0) return null;
      final out = e.duplicate();
      if (out is CircleEnt) out.r = nr;
      if (out is ArcEnt) out.r = nr;
      return out;
    }
    if (e is PolyEnt && e.points.length >= 2) {
      var best = double.infinity;
      Seg? seg;
      for (final s in e.segments) {
        final dist = distToSegment(side, s.$1, s.$2);
        if (dist < best) {
          best = dist;
          seg = s;
        }
      }
      if (seg == null) return null;
      final left = cross(seg.$2 - seg.$1, side - seg.$1) > 0;
      return PolyEnt(
        layerId: e.layerId,
        color: e.color,
        weight: e.weight,
        closed: e.closed,
        points: offsetPolyline(e.points, left ? d : -d, closed: e.closed),
      );
    }
    return null;
  }
}

class ZoomCmd extends CadCommand {
  ZoomCmd(super.c);

  @override
  String get name => 'ZOOM';
  @override
  String get prompt => 'Enter E for extents, or a scale factor';
  @override
  bool get repeats => false;
  @override
  List<CmdOption> get options => const [CmdOption('Extents', 'E')];

  @override
  void pick(Offset p) {}

  @override
  bool keyword(String k) {
    if (k == 'E' || k == 'A') {
      c.zoomExtents();
      done();
      return true;
    }
    return false;
  }

  @override
  bool number(double v) {
    if (v <= 0) return false;
    c.zoomAt(c.size.center(Offset.zero), v);
    done();
    return true;
  }

  @override
  String? dynLabel(Offset cursor) => null;
}
