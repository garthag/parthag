import 'dart:math' as math;
import 'dart:ui';

import 'package:flutter/foundation.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/scheduler.dart';
import 'package:flutter/services.dart';

import '../models/drawing.dart';
import 'command_parser.dart';
import 'commands.dart';
import 'geometry.dart';

enum Tool {
  select('Select', 'SELECT'),
  lasso('Lasso', 'LASSO'),
  pen('Pen', 'PEN'),
  pencil('Pencil', 'PENCIL'),
  highlighter('Highlighter', 'HIGHLIGHTER'),
  eraser('Eraser', 'ERASER'),
  line('Line', 'LINE'),
  polyline('Polyline', 'PLINE'),
  rect('Rectangle', 'RECTANG'),
  circle('Circle', 'CIRCLE'),
  arc('Arc', 'ARC'),
  text('Text', 'TEXT'),
  dimension('Dimension', 'DIMALIGNED'),
  measure('Measure', 'DIST'),
  area('Area', 'AREA'),
  pan('Pan', 'PAN');

  const Tool(this.label, this.command);
  final String label;
  final String command;

  bool get isInk => this == Tool.pen || this == Tool.pencil || this == Tool.highlighter;
  bool get isCad => index >= Tool.line.index && index <= Tool.area.index;

  InkPen get inkPen => switch (this) {
        Tool.pencil => InkPen.pencil,
        Tool.highlighter => InkPen.highlighter,
        _ => InkPen.pen,
      };

  static Tool? forCommand(String name) => Tool.values.where((t) => t.command == name).firstOrNull;
}

enum SnapKind {
  endpoint('Endpoint', .8),
  intersection('Intersection', .85),
  center('Center', .9),
  midpoint('Midpoint', 1),
  quadrant('Quadrant', 1);

  const SnapKind(this.label, this.weight);
  final String label;
  final double weight;
}

class SnapHit {
  const SnapHit(this.p, this.kind);
  final Offset p;
  final SnapKind kind;
}

class PenSettings {
  PenSettings(this.color, this.width);
  int color;

  /// Width in screen pixels at the zoom level the stroke is drawn.
  double width;
}

/// A listenable that painters use to repaint without rebuilding widgets.
class Tick extends ChangeNotifier {
  void fire() => notifyListeners();
}

enum _Drag { none, pan, ink, erase, lasso, window, move, grip, tapEntity, command }

/// Everything the drawing canvas does: view transform, tools, CAD commands,
/// snapping, selection, grips and undo. Widgets only forward input to it.
class CanvasController extends ChangeNotifier {
  CanvasController(this.drawing, {this.onChanged}) {
    final dark = drawing.paper.isDark;
    pens = {
      Tool.pen: PenSettings(dark ? 0xFFF2F4F7 : 0xFF1B1B1F, 3),
      Tool.pencil: PenSettings(dark ? 0xFFB0B8C4 : 0xFF55585E, 2),
      Tool.highlighter: PenSettings(0xFFFFE066, 18),
    };
    tool = dark ? Tool.select : Tool.pen;
  }

  final Drawing drawing;

  /// Called after every change to the drawing's content.
  VoidCallback? onChanged;

  /// Asks the user for text and height (set by the screen).
  Future<(String, double)?> Function(String text, double height)? textPrompt;

  /// Asks the screen to open a panel: LAYER, PROPERTIES or UNITS.
  void Function(String panel)? onPanel;

  /// Repaints the committed drawing.
  final scene = Tick();

  /// Repaints previews, cursor and snap markers.
  final overlay = Tick();

  late final Map<Tool, PenSettings> pens;
  late Tool tool;
  double eraserSize = 24;
  bool eraseWholeStrokes = false;
  bool stylusOnly = false;
  bool stylusSeen = false;
  double lineWeight = 1.4;
  double offsetDistance = 100;

  final Set<String> selection = {};
  CadCommand? command;
  String? lastCommand;
  final List<String> log = ['Type a command, or pick a tool. Type HELP for the list.'];

  // -- View -------------------------------------------------------------------

  Size size = Size.zero;
  double zoom = 1;

  /// Screen position of the world origin.
  Offset pan = Offset.zero;
  bool _viewReady = false;

  Offset toScreen(Offset w) => Offset(w.dx * zoom + pan.dx, -w.dy * zoom + pan.dy);
  Offset toWorld(Offset s) => Offset((s.dx - pan.dx) / zoom, -(s.dy - pan.dy) / zoom);

  Rect get visibleWorld => Rect.fromPoints(toWorld(Offset.zero), toWorld(Offset(size.width, size.height)));

  /// Called during layout; never notifies. After the first layout the view
  /// keeps its top-left corner, so panels opening or closing don't make the
  /// drawing jump.
  void setSize(Size s) {
    if (s == size || s.isEmpty) return;
    size = s;
    if (_viewReady) return;
    _viewReady = true;
    if (drawing.viewZoom != null && drawing.viewCenter != null) {
      zoom = drawing.viewZoom!;
      _centerOn(drawing.viewCenter!);
    } else {
      _fitExtents();
    }
    // Widgets built before layout (status bar zoom) need one more paint.
    SchedulerBinding.instance.addPostFrameCallback((_) => scene.fire());
  }

  void _centerOn(Offset w) => pan = Offset(size.width / 2 - w.dx * zoom, size.height / 2 + w.dy * zoom);

  void _fitExtents() {
    final r = drawing.extents;
    if (r == null || size.isEmpty) {
      zoom = drawing.paper.isDark ? 1 : 1;
      _centerOn(drawing.paper.isDark ? Offset.zero : Offset(size.width / 2 - 24, -size.height / 2 + 24));
      return;
    }
    const margin = 48.0;
    final w = math.max(r.width, 1e-6);
    final h = math.max(r.height, 1e-6);
    zoom = math.min((size.width - margin * 2) / w, (size.height - margin * 2) / h).clamp(1e-5, 1e5);
    if (!drawing.paper.isDark) zoom = math.min(zoom, 2.5);
    _centerOn(r.center);
  }

  void zoomExtents() {
    _fitExtents();
    _viewChanged();
  }

  void zoomAt(Offset screen, double factor) {
    final w = toWorld(screen);
    zoom = (zoom * factor).clamp(1e-5, 1e5);
    pan = screen - Offset(w.dx * zoom, -w.dy * zoom);
    _viewChanged();
  }

  void panBy(Offset delta) {
    pan += delta;
    _viewChanged();
  }

  void _viewChanged() {
    drawing.viewZoom = zoom;
    if (!size.isEmpty) drawing.viewCenter = toWorld(size.center(Offset.zero));
    scene.fire();
    overlay.fire();
  }

  // -- Changes and undo -------------------------------------------------------

  final List<_Snapshot> _undo = [];
  final List<_Snapshot> _redo = [];

  bool get canUndo => _undo.isNotEmpty;
  bool get canRedo => _redo.isNotEmpty;

  _Snapshot _snap() => _Snapshot(
        [for (final e in drawing.entities) e.clone()],
        [for (final l in drawing.layers) l.clone()],
        drawing.currentLayerId,
      );

  void _restore(_Snapshot s) {
    drawing.entities = s.entities;
    drawing.layers = s.layers;
    drawing.currentLayerId = s.currentLayer;
    final ids = {for (final e in drawing.entities) e.id};
    selection.removeWhere((id) => !ids.contains(id));
  }

  /// Saves the current state so the next change can be undone.
  void checkpoint() {
    _undo.add(_snap());
    if (_undo.length > 150) _undo.removeAt(0);
    _redo.clear();
  }

  void _dropCheckpoint() {
    if (_undo.isNotEmpty) _undo.removeLast();
  }

  void _revertToCheckpoint() {
    if (_undo.isNotEmpty) _restore(_undo.removeLast());
    scene.fire();
  }

  void undo() {
    if (_undo.isEmpty) return;
    _redo.add(_snap());
    _restore(_undo.removeLast());
    changed();
  }

  void redo() {
    if (_redo.isEmpty) return;
    _undo.add(_snap());
    _restore(_redo.removeLast());
    changed();
  }

  /// Repaints everything and reports a content change.
  void changed() {
    scene.fire();
    overlay.fire();
    notifyListeners();
    onChanged?.call();
  }

  /// Repaints and rebuilds without a content change.
  void refresh() {
    scene.fire();
    overlay.fire();
    notifyListeners();
  }

  void addEntity(Entity e) => addEntities([e]);

  void addEntities(List<Entity> list) {
    if (list.isEmpty) return;
    checkpoint();
    drawing.entities.addAll(list);
    changed();
  }

  void removeEntityById(String id) {
    checkpoint();
    drawing.entities.removeWhere((e) => e.id == id);
    selection.remove(id);
    changed();
  }

  bool editable(Entity e) {
    final l = drawing.layer(e.layerId);
    return l.visible && !l.locked;
  }

  // -- Settings ---------------------------------------------------------------

  void setTool(Tool t) {
    _abortDrag();
    command = null;
    measurement = null;
    tool = t;
    if (t.isCad) command = _make(t.command);
    if (t.isInk || t == Tool.eraser) selection.clear();
    refresh();
  }

  void toggleGrid() {
    drawing.gridOn = !drawing.gridOn;
    say('<Grid ${drawing.gridOn ? 'on' : 'off'}>');
    _settingChanged();
  }

  void toggleSnap() {
    drawing.snapOn = !drawing.snapOn;
    say('<Snap ${drawing.snapOn ? 'on' : 'off'}>');
    _settingChanged();
  }

  void toggleOrtho() {
    drawing.orthoOn = !drawing.orthoOn;
    say('<Ortho ${drawing.orthoOn ? 'on' : 'off'}>');
    _settingChanged();
  }

  void toggleOsnap() {
    drawing.osnapOn = !drawing.osnapOn;
    say('<Osnap ${drawing.osnapOn ? 'on' : 'off'}>');
    _settingChanged();
  }

  void setPaper(PaperStyle p) {
    drawing.paper = p;
    _settingChanged();
  }

  void _settingChanged() {
    refresh();
    onChanged?.call();
  }

  void say(String line) {
    log.add(line);
    if (log.length > 300) log.removeRange(0, 100);
    notifyListeners();
  }

  // -- Layers -----------------------------------------------------------------

  CadLayer addLayer(String name, int color) {
    checkpoint();
    final l = CadLayer(name: name, color: color);
    drawing.layers.add(l);
    drawing.currentLayerId = l.id;
    changed();
    return l;
  }

  void updateLayer(CadLayer l, void Function(CadLayer l) edit) {
    checkpoint();
    edit(l);
    if (!l.visible || l.locked) {
      selection.removeWhere((id) => drawing.entities.any((e) => e.id == id && e.layerId == l.id));
    }
    changed();
  }

  /// Deletes a layer and moves its objects to layer 0.
  void deleteLayer(CadLayer l) {
    if (l.id == drawing.layers.first.id) return;
    checkpoint();
    final first = drawing.layers.first.id;
    for (final e in drawing.entities.where((e) => e.layerId == l.id)) {
      e.layerId = first;
    }
    drawing.layers.remove(l);
    if (drawing.currentLayerId == l.id) drawing.currentLayerId = first;
    changed();
  }

  void setCurrentLayer(CadLayer l) {
    drawing.currentLayerId = l.id;
    _settingChanged();
  }

  // -- Selection --------------------------------------------------------------

  List<Entity> get selectedEntities => drawing.entities.where((e) => selection.contains(e.id)).toList();

  void selectAll() {
    selection
      ..clear()
      ..addAll(drawing.entities.where(editable).map((e) => e.id));
    refresh();
  }

  void clearSelection() {
    selection.clear();
    refresh();
  }

  void deleteSelection() {
    if (selection.isEmpty) return;
    checkpoint();
    final n = selection.length;
    drawing.entities.removeWhere((e) => selection.contains(e.id) && editable(e));
    selection.clear();
    say('$n object${n == 1 ? '' : 's'} erased');
    changed();
  }

  void transformSelection(Offset Function(Offset) f, {double rotation = 0, double scale = 1, bool mirror = false}) {
    checkpoint();
    for (final e in selectedEntities.where(editable)) {
      e.transform(f, rotation: rotation, scale: scale, mirror: mirror);
    }
    changed();
  }

  void editSelection(void Function(Entity e) edit) {
    if (selection.isEmpty) return;
    checkpoint();
    for (final e in selectedEntities.where(editable)) {
      edit(e);
    }
    changed();
  }

  void duplicateSelection() {
    final copies = [for (final e in selectedEntities) e.duplicate()..transform((q) => q + Offset(20 / zoom, -20 / zoom))];
    addEntities(copies);
    selection
      ..clear()
      ..addAll(copies.map((e) => e.id));
    refresh();
  }

  Rect? get selectionBounds {
    Rect? r;
    for (final e in selectedEntities) {
      r = unionRect(r, e.bounds);
    }
    return r;
  }

  /// The top-most editable object near world point [w].
  Entity? hitEntity(Offset w, {double px = 10}) {
    final tol = px / zoom;
    Entity? best;
    var bestD = tol;
    for (final e in drawing.entities.reversed) {
      if (!editable(e)) continue;
      if (!e.bounds.inflate(tol).contains(w)) continue;
      final d = e.distanceTo(w);
      if (d <= bestD) {
        bestD = d;
        best = e;
        if (d == 0) break;
      }
    }
    return best;
  }

  void _selectWindow(Offset a, Offset b) {
    final r = Rect.fromPoints(a, b);
    // Dragging right selects objects fully inside; dragging left also
    // selects objects that cross the window, as in AutoCAD.
    final crossing = toScreen(b).dx < toScreen(a).dx;
    for (final e in drawing.entities) {
      if (!editable(e)) continue;
      final inside = crossing ? _crosses(e, r) : e.samples().every(r.contains) && r.contains(e.bounds.center);
      if (inside) selection.add(e.id);
    }
  }

  static bool _crosses(Entity e, Rect r) {
    if (!e.bounds.overlaps(r)) return false;
    if (e.samples().any(r.contains)) return true;
    if (e.segments.any((s) => segmentTouchesRect(s.$1, s.$2, r))) return true;
    final c = e.circle;
    if (c != null) {
      final k = [r.topLeft, r.topRight, r.bottomRight, r.bottomLeft];
      for (var i = 0; i < 4; i++) {
        if (segmentCircleIntersections(k[i], k[(i + 1) % 4], c.$1, c.$2).any((p) => _onArc(e, p))) return true;
      }
    }
    return e.distanceTo(r.center) == 0;
  }

  void _selectLasso(List<Offset> poly) {
    if (poly.length < 3) return;
    selection.clear();
    for (final e in drawing.entities) {
      if (!editable(e)) continue;
      final s = e.samples();
      if (s.isEmpty) continue;
      final inside = s.where((p) => pointInPolygon(p, poly)).length;
      if (inside / s.length >= .6) selection.add(e.id);
    }
  }

  // -- Commands ---------------------------------------------------------------

  CadCommand? _make(String name) => switch (name) {
        'LINE' => LineCmd(this),
        'PLINE' => PlineCmd(this),
        'RECTANG' => RectCmd(this),
        'CIRCLE' => CircleCmd(this),
        'ARC' => ArcCmd(this),
        'TEXT' => TextCmd(this),
        'DIMALIGNED' => DimCmd(this),
        'DIST' => DistCmd(this),
        'AREA' => AreaCmd(this),
        'OFFSET' => OffsetCmd(this),
        'ZOOM' => ZoomCmd(this),
        _ => null,
      };

  static const _modify = {'MOVE', 'COPY', 'ROTATE', 'SCALE', 'MIRROR', 'ERASE'};

  /// Starts a command by its full name (see [commandAliases]).
  void startCommand(String name) {
    _abortDrag();
    measurement = null;
    final t = Tool.forCommand(name);
    if (t != null) {
      setTool(t);
      if (t.isCad) say('${command?.name}: ${command?.prompt}');
      return;
    }
    if (_modify.contains(name)) {
      if (selection.isEmpty) {
        command = SelectObjectsCmd(this, name);
        if (tool.isInk || tool == Tool.eraser) tool = Tool.select;
        say('$name: Select objects, then press Enter');
        refresh();
        return;
      }
      lastCommand = name;
      if (name == 'ERASE') {
        command = null;
        deleteSelection();
        _resumeTool();
        return;
      }
      command = switch (name) {
        'MOVE' => MoveCmd(this),
        'COPY' => MoveCmd(this, copy: true),
        'ROTATE' => RotateCmd(this),
        'SCALE' => ScaleCmd(this),
        _ => MirrorCmd(this),
      };
      say('$name: ${command!.prompt}');
      refresh();
      return;
    }
    final cmd = _make(name);
    if (cmd != null) {
      command = cmd;
      lastCommand = name;
      say('$name: ${cmd.prompt}');
      refresh();
      return;
    }
    switch (name) {
      case 'UNDO':
        undo();
      case 'REDO':
        redo();
      case 'ZOOMEXTENTS':
        zoomExtents();
      case 'GRID':
        toggleGrid();
      case 'SNAP':
        toggleSnap();
      case 'ORTHO':
        toggleOrtho();
      case 'OSNAP':
        toggleOsnap();
      case 'SELECTALL':
        selectAll();
      case 'HELP':
        helpText.forEach(say);
      case 'LAYER':
      case 'PROPERTIES':
      case 'UNITS':
        onPanel?.call(name);
    }
  }

  /// Called by a command when it has finished.
  void endCommand(CadCommand cmd) {
    if (!identical(command, cmd)) return;
    lastCommand = cmd is SelectObjectsCmd ? lastCommand : cmd.name;
    command = null;
    _resumeTool();
  }

  void _resumeTool() {
    if (tool.isCad) command = _make(tool.command);
    refresh();
  }

  /// Enter / Space / right click: finish the command, or repeat the last one.
  void enter() {
    final cmd = command;
    if (cmd != null) {
      cmd.enter();
      refresh();
    } else if (lastCommand != null) {
      startCommand(lastCommand!);
    }
  }

  void escape() {
    _abortDrag();
    measurement = null;
    final cmd = command;
    if (cmd != null) {
      final fresh = tool.isCad && cmd.name == tool.command && cmd.pts.isEmpty;
      command = null;
      if (fresh) {
        tool = Tool.select;
      } else {
        say('*Cancel*');
        _resumeTool();
      }
    } else if (selection.isNotEmpty) {
      selection.clear();
    } else if (tool.isCad) {
      tool = Tool.select;
    }
    refresh();
  }

  /// Handles a line typed into the command line.
  void submit(String input) {
    final s = input.trim();
    if (s.isEmpty) {
      enter();
      return;
    }
    final cmd = command;
    if (cmd != null) {
      say('${cmd.prompt}: $s');
      final p = parsePoint(s, last: cmd.base);
      if (p != null) {
        cmd.pick(p);
        refresh();
        return;
      }
      final n = double.tryParse(s);
      if (n != null) {
        if (!cmd.number(n)) {
          // Direct distance entry: the distance along the cursor direction.
          final b = cmd.base;
          final cur = cursorWorld;
          if (b != null && cur != null && cur != b) {
            var dir = cur - b;
            if (drawing.orthoOn) dir = dir.dx.abs() >= dir.dy.abs() ? Offset(dir.dx, 0) : Offset(0, dir.dy);
            cmd.pick(b + dir / dir.distance * n);
          } else {
            say('Point or option expected.');
          }
        }
        refresh();
        return;
      }
      if (cmd.keyword(s.toUpperCase())) {
        refresh();
        return;
      }
    }
    final name = commandAliases[s.toUpperCase()];
    if (name == null) {
      say('Unknown command "${s.toUpperCase()}". Type HELP for a list.');
      return;
    }
    say('Command: $name');
    startCommand(name);
  }

  Future<(String, double)?> askText(String text, double height) =>
      textPrompt?.call(text, height) ?? Future.value(null);

  // -- Snapping ---------------------------------------------------------------

  Offset? cursorScreen;
  Offset? cursorWorld;
  SnapHit? snapHit;

  /// Converts a screen point to a world point, applying object snap, ortho
  /// and grid snap.
  Offset snapPoint(Offset screen, {Offset? base, Set<String> exclude = const {}, bool osnap = true}) {
    final raw = toWorld(screen);
    snapHit = null;
    if (osnap && drawing.osnapOn) {
      final hit = _objectSnap(raw, exclude);
      if (hit != null) {
        snapHit = hit;
        return hit.p;
      }
    }
    var p = raw;
    var horizontal = false, vertical = false;
    if (drawing.orthoOn && base != null) {
      final d = p - base;
      if (d.dx.abs() >= d.dy.abs()) {
        p = Offset(p.dx, base.dy);
        horizontal = true;
      } else {
        p = Offset(base.dx, p.dy);
        vertical = true;
      }
    }
    if (drawing.snapOn) {
      final g = drawing.gridSize;
      p = Offset(
        vertical ? p.dx : (p.dx / g).roundToDouble() * g,
        horizontal ? p.dy : (p.dy / g).roundToDouble() * g,
      );
    }
    return p;
  }

  SnapHit? _objectSnap(Offset w, Set<String> exclude) {
    final tol = 14 / zoom;
    final near = <Entity>[];
    for (final e in drawing.entities) {
      if (e is InkStroke || exclude.contains(e.id) || !drawing.layer(e.layerId).visible) continue;
      if (e.bounds.inflate(tol).contains(w)) near.add(e);
    }
    SnapHit? best;
    var bestScore = tol;
    void consider(Offset p, SnapKind k) {
      final score = (p - w).distance * k.weight;
      if (score <= bestScore) {
        bestScore = score;
        best = SnapHit(p, k);
      }
    }

    for (final e in near) {
      for (final p in e.endPoints) {
        consider(p, SnapKind.endpoint);
      }
      for (final p in e.midPoints) {
        consider(p, SnapKind.midpoint);
      }
      for (final p in e.centers) {
        consider(p, SnapKind.center);
      }
      for (final p in e.quadrants) {
        consider(p, SnapKind.quadrant);
      }
    }
    // Intersections between objects close to the cursor.
    final close = near.where((e) => e.distanceTo(w) <= tol).take(24).toList();
    for (var i = 0; i < close.length; i++) {
      for (var j = i + 1; j < close.length; j++) {
        for (final p in _intersections(close[i], close[j])) {
          consider(p, SnapKind.intersection);
        }
      }
    }
    return best;
  }

  static bool _onArc(Entity e, Offset p) => e is! ArcEnt || angleInSweep(angleOf(e.c, p), e.start, e.sweep);

  static List<Offset> _intersections(Entity a, Entity b) {
    final out = <Offset>[];
    final ca = a.circle, cb = b.circle;
    for (final s in a.segments) {
      for (final t in b.segments) {
        final p = segmentIntersection(s.$1, s.$2, t.$1, t.$2);
        if (p != null) out.add(p);
      }
      if (cb != null) {
        out.addAll(segmentCircleIntersections(s.$1, s.$2, cb.$1, cb.$2).where((p) => _onArc(b, p)));
      }
    }
    if (ca != null) {
      for (final t in b.segments) {
        out.addAll(segmentCircleIntersections(t.$1, t.$2, ca.$1, ca.$2).where((p) => _onArc(a, p)));
      }
      if (cb != null) {
        out.addAll(circleCircleIntersections(ca.$1, ca.$2, cb.$1, cb.$2).where((p) => _onArc(a, p) && _onArc(b, p)));
      }
    }
    return out;
  }

  void _updateCursor(Offset screen) {
    cursorScreen = screen;
    final cmd = command;
    if (cmd != null && !cmd.picksObject) {
      cursorWorld = snapPoint(screen, base: cmd.base);
    } else {
      cursorWorld = toWorld(screen);
      snapHit = null;
    }
  }

  // -- Pointer input ----------------------------------------------------------

  /// Live state the overlay painter draws.
  InkStroke? liveStroke;
  final List<Offset> lassoPath = [];
  Offset? windowStart;
  Offset? windowEnd;
  List<Offset>? measurement;
  bool hovering = false;

  final Map<int, Offset> _touches = {};
  bool _pinching = false;
  double _pinchDist0 = 1;
  double _pinchZoom0 = 1;
  Offset _pinchWorld0 = Offset.zero;
  int? _active;
  _Drag _drag = _Drag.none;
  Offset _downScreen = Offset.zero;
  Offset _downWorld = Offset.zero;
  bool _moved = false;
  bool _started = false;
  Entity? _gripEntity;
  int _gripIndex = -1;
  Offset _gripOrigin = Offset.zero;
  Entity? _tapped;
  Map<String, Entity> _originals = {};
  double _lastPanZoomScale = 1;

  bool get _shift => HardwareKeyboard.instance.isShiftPressed;

  void onPointerDown(PointerDownEvent e) {
    final kind = e.kind;
    hovering = kind == PointerDeviceKind.mouse;
    if (kind == PointerDeviceKind.stylus || kind == PointerDeviceKind.invertedStylus) stylusSeen = true;
    if (kind == PointerDeviceKind.touch) {
      _touches[e.pointer] = e.localPosition;
      if (_touches.length == 2) {
        _abortDrag();
        _startPinch();
        return;
      }
      if (_touches.length > 2 || _pinching) return;
    }
    if (_active != null) return;
    _active = e.pointer;
    final s = e.localPosition;
    _downScreen = s;
    _moved = false;
    _started = false;
    cursorScreen = s;

    if (kind == PointerDeviceKind.mouse && (e.buttons & kSecondaryMouseButton) != 0) {
      _drag = _Drag.none;
      enter();
      return;
    }
    final fingerPans = stylusOnly && kind == PointerDeviceKind.touch;
    if ((e.buttons & kMiddleMouseButton) != 0 || tool == Tool.pan || fingerPans) {
      _drag = _Drag.pan;
      return;
    }
    final cmd = command;
    if (measurement != null && (cmd == null || cmd.pts.isEmpty)) measurement = null;

    if (cmd != null && cmd is! SelectObjectsCmd) {
      _drag = _Drag.command;
      _updateCursor(s);
      _downWorld = cursorWorld!;
      overlay.fire();
      return;
    }
    if (kind == PointerDeviceKind.invertedStylus || tool == Tool.eraser) {
      _drag = _Drag.erase;
      _eraseAt(toWorld(s));
      return;
    }
    if (tool.isInk && cmd == null) {
      _drag = _Drag.ink;
      final pen = pens[tool]!;
      liveStroke = InkStroke(
        layerId: drawing.currentLayerId,
        color: pen.color,
        weight: pen.width / zoom,
        pen: tool.inkPen,
        points: [toWorld(s)],
        pressures: _pressure(e) == null ? [] : [_pressure(e)!],
      );
      overlay.fire();
      return;
    }
    if (tool == Tool.lasso && cmd == null) {
      final r = selectionBounds;
      if (r != null && r.inflate(8 / zoom).contains(toWorld(s))) {
        _beginMove(s);
        return;
      }
      _drag = _Drag.lasso;
      lassoPath
        ..clear()
        ..add(toWorld(s));
      return;
    }
    _selectDown(s);
  }

  double? _pressure(PointerEvent e) {
    if (e.kind != PointerDeviceKind.stylus || e.pressureMax <= e.pressureMin) return null;
    return ((e.pressure - e.pressureMin) / (e.pressureMax - e.pressureMin)).clamp(0.0, 1.0);
  }

  void _beginMove(Offset s) {
    _drag = _Drag.move;
    _downWorld = toWorld(s);
    _originals = {for (final e in selectedEntities.where(editable)) e.id: e.clone()};
  }

  void _selectDown(Offset s) {
    final w = toWorld(s);
    final picking = command is SelectObjectsCmd;
    if (!picking && selection.isNotEmpty && selection.length <= 40) {
      for (final e in selectedEntities.where(editable)) {
        final g = e.grips;
        for (var i = 0; i < g.length; i++) {
          if ((toScreen(g[i]) - s).distance <= 12) {
            _drag = _Drag.grip;
            _gripEntity = e;
            _gripIndex = i;
            _gripOrigin = g[i];
            checkpoint();
            return;
          }
        }
      }
    }
    final hit = hitEntity(w);
    if (hit != null && !picking && selection.contains(hit.id) && !_shift) {
      _beginMove(s);
      _tapped = hit;
      return;
    }
    if (hit != null) {
      _drag = _Drag.tapEntity;
      _tapped = hit;
      return;
    }
    _drag = _Drag.window;
    windowStart = w;
    windowEnd = w;
  }

  void onPointerMove(PointerMoveEvent e) {
    if (e.kind == PointerDeviceKind.touch && _touches.containsKey(e.pointer)) _touches[e.pointer] = e.localPosition;
    if (_pinching) {
      _updatePinch();
      return;
    }
    if (e.pointer != _active) return;
    final s = e.localPosition;
    final slop = e.kind == PointerDeviceKind.touch ? 8.0 : 3.0;
    if (!_moved && (s - _downScreen).distance > slop) _moved = true;
    cursorScreen = s;
    switch (_drag) {
      case _Drag.pan:
        panBy(e.delta);
      case _Drag.ink:
        final st = liveStroke!;
        final w = toWorld(s);
        if ((toScreen(st.points.last) - s).distance >= .8) {
          st.points.add(w);
          final pr = _pressure(e);
          if (pr != null && st.pressures.isNotEmpty) st.pressures.add(pr);
          overlay.fire();
        }
      case _Drag.erase:
        _eraseAt(toWorld(s));
      case _Drag.lasso:
        lassoPath.add(toWorld(s));
        overlay.fire();
      case _Drag.window:
        windowEnd = toWorld(s);
        overlay.fire();
      case _Drag.tapEntity:
        if (!_moved) return;
        if (command == null) {
          selection.add(_tapped!.id);
          _beginMove(_downScreen);
          _moveTo(s);
        } else {
          _drag = _Drag.window;
          windowStart = toWorld(_downScreen);
          windowEnd = toWorld(s);
          overlay.fire();
        }
      case _Drag.move:
        if (_moved) _moveTo(s);
      case _Drag.grip:
        final p = snapPoint(s, base: _gripOrigin, exclude: {_gripEntity!.id});
        cursorWorld = p;
        _gripEntity!.moveGrip(_gripIndex, p);
        _started = true;
        scene.fire();
        overlay.fire();
      case _Drag.command:
        _updateCursor(s);
        overlay.fire();
      case _Drag.none:
        break;
    }
  }

  void _moveTo(Offset s) {
    if (!_started) {
      checkpoint();
      _started = true;
    }
    final p = snapPoint(s, base: _downWorld, osnap: false);
    cursorWorld = p;
    final d = p - _downWorld;
    for (var i = 0; i < drawing.entities.length; i++) {
      final orig = _originals[drawing.entities[i].id];
      if (orig != null) drawing.entities[i] = orig.clone()..transform((q) => q + d);
    }
    scene.fire();
    overlay.fire();
  }

  void onPointerUp(PointerUpEvent e) {
    _touches.remove(e.pointer);
    if (_pinching) {
      if (_touches.isEmpty) _pinching = false;
      return;
    }
    if (e.pointer != _active) return;
    _active = null;
    final s = e.localPosition;
    switch (_drag) {
      case _Drag.ink:
        final st = liveStroke!;
        liveStroke = null;
        addEntity(st);
      case _Drag.erase:
        if (_started) changed();
      case _Drag.lasso:
        _selectLasso(lassoPath);
        lassoPath.clear();
        refresh();
      case _Drag.window:
        if (_moved && windowStart != null) {
          _selectWindow(windowStart!, toWorld(s));
        } else if (command == null && !_shift) {
          selection.clear();
        }
        windowStart = windowEnd = null;
        refresh();
      case _Drag.tapEntity:
        final id = _tapped!.id;
        if (selection.contains(id)) {
          selection.remove(id);
        } else {
          selection.add(id);
        }
        refresh();
      case _Drag.move:
        if (_started) {
          changed();
        } else if (_shift && _tapped != null) {
          selection.remove(_tapped!.id);
          refresh();
        }
      case _Drag.grip:
        if (_started) {
          changed();
        } else {
          _dropCheckpoint();
        }
      case _Drag.command:
        _release(s);
      case _Drag.pan:
      case _Drag.none:
        break;
    }
    _drag = _Drag.none;
    _tapped = null;
    _gripEntity = null;
    _originals = {};
    if (e.kind != PointerDeviceKind.mouse) {
      cursorScreen = null;
      snapHit = null;
    }
    overlay.fire();
    notifyListeners();
  }

  /// A pick: a tap picks one point; a drag picks where it started and ended.
  void _release(Offset s) {
    final cmd = command;
    if (cmd == null) return;
    if (cmd.picksObject) {
      cmd.pick(toWorld(s));
      refresh();
      return;
    }
    if (_moved) {
      final far = cmd.pts.isNotEmpty && (toScreen(_downWorld) - toScreen(cmd.pts.last)).distance > 14;
      if (cmd.pts.isEmpty || (cmd.dragRestart && far)) {
        var target = cmd;
        if (cmd.pts.isNotEmpty) {
          cmd.enter();
          final next = command;
          if (next == null) return;
          target = next;
        }
        target.pick(_downWorld);
        if (!identical(command, target)) {
          refresh();
          return;
        }
      }
    }
    final now = command;
    if (now == null) return;
    _updateCursor(s);
    now.pick(cursorWorld!);
    refresh();
  }

  void onPointerHover(PointerHoverEvent e) {
    hovering = true;
    _updateCursor(e.localPosition);
    overlay.fire();
  }

  void onPointerExit() {
    cursorScreen = null;
    snapHit = null;
    overlay.fire();
  }

  void onPointerCancel(PointerCancelEvent e) {
    _touches.remove(e.pointer);
    if (_touches.isEmpty) _pinching = false;
    if (e.pointer == _active) _abortDrag();
  }

  void onPointerSignal(PointerSignalEvent e) {
    if (e is PointerScrollEvent) {
      final dy = e.scrollDelta.dy;
      if (dy == 0) {
        panBy(-e.scrollDelta);
      } else {
        zoomAt(e.localPosition, math.exp(-dy / 400));
      }
    } else if (e is PointerScaleEvent) {
      zoomAt(e.localPosition, e.scale);
    }
  }

  void onPanZoomStart(PointerPanZoomStartEvent e) => _lastPanZoomScale = 1;

  void onPanZoomUpdate(PointerPanZoomUpdateEvent e) {
    pan += e.panDelta;
    final f = e.scale / _lastPanZoomScale;
    _lastPanZoomScale = e.scale;
    zoomAt(e.localPosition, f);
  }

  void _startPinch() {
    _pinching = true;
    final p = _touches.values.take(2).toList();
    final focal = (p[0] + p[1]) / 2;
    _pinchDist0 = math.max((p[0] - p[1]).distance, 1);
    _pinchZoom0 = zoom;
    _pinchWorld0 = toWorld(focal);
  }

  void _updatePinch() {
    if (_touches.length < 2) return;
    final p = _touches.values.take(2).toList();
    final focal = (p[0] + p[1]) / 2;
    final dist = math.max((p[0] - p[1]).distance, 1);
    zoom = (_pinchZoom0 * dist / _pinchDist0).clamp(1e-5, 1e5);
    pan = focal - Offset(_pinchWorld0.dx * zoom, -_pinchWorld0.dy * zoom);
    _viewChanged();
  }

  /// Abandons the current pointer action, e.g. when a second finger lands.
  void _abortDrag() {
    switch (_drag) {
      case _Drag.ink:
        liveStroke = null;
      case _Drag.lasso:
        lassoPath.clear();
      case _Drag.window:
      case _Drag.tapEntity:
        windowStart = windowEnd = null;
      case _Drag.move:
        if (_started) _revertToCheckpoint();
      case _Drag.grip:
        _revertToCheckpoint();
      case _Drag.erase:
        if (_started) changed();
      case _Drag.command:
      case _Drag.pan:
      case _Drag.none:
        break;
    }
    _drag = _Drag.none;
    _active = null;
    _originals = {};
    overlay.fire();
  }

  // -- Eraser -----------------------------------------------------------------

  /// Erases at world point [w]: whole objects, or only the touched part of
  /// handwriting strokes (Samsung Notes "erase by area").
  void _eraseAt(Offset w) {
    final r = eraserSize / 2 / zoom;
    final out = <Entity>[];
    var changedAny = false;
    for (final e in drawing.entities) {
      if (!editable(e) || !e.bounds.inflate(r).contains(w) || e.distanceTo(w) > r) {
        out.add(e);
        continue;
      }
      if (!_started) {
        checkpoint();
        _started = true;
      }
      changedAny = true;
      if (eraseWholeStrokes || e is! InkStroke || e.points.length < 2) continue;
      var piece = <int>[];
      void flush() {
        if (piece.length >= 2) {
          out.add(InkStroke(
            layerId: e.layerId,
            color: e.color!,
            weight: e.weight,
            pen: e.pen,
            points: [for (final i in piece) e.points[i]],
            pressures: e.pressures.length == e.points.length ? [for (final i in piece) e.pressures[i]] : [],
          ));
        }
        piece = [];
      }

      for (var i = 0; i < e.points.length; i++) {
        if ((e.points[i] - w).distance > r) {
          piece.add(i);
        } else {
          flush();
        }
      }
      flush();
    }
    if (changedAny) {
      drawing.entities = out;
      selection.removeWhere((id) => !out.any((e) => e.id == id));
      scene.fire();
    }
    cursorScreen = toScreen(w);
    overlay.fire();
  }

  // -- Keyboard ---------------------------------------------------------------

  /// Handles shortcuts when the canvas has focus. Returns false for keys the
  /// caller should send to the command line.
  bool handleKey(KeyEvent e) {
    if (e is! KeyDownEvent && e is! KeyRepeatEvent) return false;
    final k = e.logicalKey;
    final kb = HardwareKeyboard.instance;
    final ctrl = kb.isControlPressed || kb.isMetaPressed;
    if (k == LogicalKeyboardKey.escape) {
      escape();
    } else if (k == LogicalKeyboardKey.enter || k == LogicalKeyboardKey.numpadEnter || k == LogicalKeyboardKey.space) {
      enter();
    } else if (k == LogicalKeyboardKey.delete || (k == LogicalKeyboardKey.backspace && command == null)) {
      deleteSelection();
    } else if (ctrl && k == LogicalKeyboardKey.keyZ) {
      kb.isShiftPressed ? redo() : undo();
    } else if (ctrl && k == LogicalKeyboardKey.keyY) {
      redo();
    } else if (ctrl && k == LogicalKeyboardKey.keyA) {
      selectAll();
    } else if (ctrl && k == LogicalKeyboardKey.keyD) {
      duplicateSelection();
    } else if (k == LogicalKeyboardKey.f3) {
      toggleOsnap();
    } else if (k == LogicalKeyboardKey.f7) {
      toggleGrid();
    } else if (k == LogicalKeyboardKey.f8) {
      toggleOrtho();
    } else if (k == LogicalKeyboardKey.f9) {
      toggleSnap();
    } else {
      return false;
    }
    return true;
  }

  @override
  void dispose() {
    scene.dispose();
    overlay.dispose();
    super.dispose();
  }
}

class _Snapshot {
  _Snapshot(this.entities, this.layers, this.currentLayer);
  final List<Entity> entities;
  final List<CadLayer> layers;
  final String currentLayer;
}
