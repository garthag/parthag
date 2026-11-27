import 'dart:math' as math;
import 'dart:ui' show PointMode;

import 'package:flutter/widgets.dart';

import '../models/drawing.dart';
import 'canvas_controller.dart';
import 'geometry.dart';

const kSelectColor = Color(0xFF3B82F6);
const kGripColor = Color(0xFF2563EB);
const kSnapColor = Color(0xFF22C55E);
const kCrossingColor = Color(0xFF22C55E);

/// World ↔ screen transform; world +Y points up.
class ViewXf {
  const ViewXf(this.zoom, this.pan);

  /// Fits [world] into [size] with [margin] pixels around it.
  factory ViewXf.fit(Rect world, Size size, {double margin = 12, double maxZoom = double.infinity}) {
    final w = math.max(world.width, 1e-6);
    final h = math.max(world.height, 1e-6);
    final z = math.min(math.min((size.width - margin * 2) / w, (size.height - margin * 2) / h), maxZoom).clamp(1e-6, 1e6);
    final c = world.center;
    return ViewXf(z, Offset(size.width / 2 - c.dx * z, size.height / 2 + c.dy * z));
  }

  final double zoom;
  final Offset pan;

  Offset s(Offset w) => Offset(w.dx * zoom + pan.dx, -w.dy * zoom + pan.dy);
  Offset w(Offset s) => Offset((s.dx - pan.dx) / zoom, -(s.dy - pan.dy) / zoom);
}

Paint _stroke(Color c, double w) => Paint()
  ..style = PaintingStyle.stroke
  ..strokeWidth = w
  ..color = c
  ..strokeCap = StrokeCap.round
  ..strokeJoin = StrokeJoin.round
  ..isAntiAlias = true;

/// Draws drawings onto a canvas. Shared by the editor and thumbnails.
class DrawingRenderer {
  DrawingRenderer(this.d, this.v, {this.annotationScale = 1, this.weightScale = 1});

  final Drawing d;
  final ViewXf v;

  /// Scale for screen-sized annotation (dimension text, arrows).
  final double annotationScale;
  final double weightScale;

  static final Map<String, TextPainter> _textCache = {};

  // -- Background -------------------------------------------------------------

  void background(Canvas canvas, Size size, {bool cadGrid = true}) {
    canvas.drawRect(Offset.zero & size, Paint()..color = d.paper.background);
    final vis = Rect.fromPoints(v.w(Offset.zero), v.w(Offset(size.width, size.height)));
    switch (d.paper) {
      case PaperStyle.lined:
        _paperLines(canvas, size, vis, 32, const Color(0x5582AEDC), horizontalOnly: true);
        final x = v.s(Offset.zero).dx;
        if (x > 0 && x < size.width) {
          canvas.drawLine(Offset(x - 8, 0), Offset(x - 8, size.height), _stroke(const Color(0x66E57373), 1));
        }
      case PaperStyle.grid:
        _paperLines(canvas, size, vis, 24, const Color(0x3382AEDC));
      case PaperStyle.dotted:
        _paperDots(canvas, size, vis, 24, const Color(0x5578849A));
      case PaperStyle.plain:
      case PaperStyle.model:
      case PaperStyle.blueprint:
        break;
    }
    if (cadGrid && d.gridOn) _cadGrid(canvas, size, vis);
  }

  void _paperLines(Canvas canvas, Size size, Rect vis, double step, Color color, {bool horizontalOnly = false}) {
    if (step * v.zoom < 5) return;
    final path = Path();
    for (var y = (vis.top / step).floorToDouble() * step; y <= vis.bottom; y += step) {
      final sy = v.s(Offset(0, y)).dy;
      path
        ..moveTo(0, sy)
        ..lineTo(size.width, sy);
    }
    if (!horizontalOnly) {
      for (var x = (vis.left / step).floorToDouble() * step; x <= vis.right; x += step) {
        final sx = v.s(Offset(x, 0)).dx;
        path
          ..moveTo(sx, 0)
          ..lineTo(sx, size.height);
      }
    }
    canvas.drawPath(path, _stroke(color, 1)..strokeCap = StrokeCap.butt);
  }

  void _paperDots(Canvas canvas, Size size, Rect vis, double step, Color color) {
    var s = step;
    while (s * v.zoom < 10) {
      s *= 2;
    }
    final pts = <Offset>[];
    for (var y = (vis.top / s).floorToDouble() * s; y <= vis.bottom; y += s) {
      for (var x = (vis.left / s).floorToDouble() * s; x <= vis.right; x += s) {
        pts.add(v.s(Offset(x, y)));
      }
    }
    canvas.drawPoints(
      PointMode.points,
      pts,
      Paint()
        ..color = color
        ..strokeWidth = 2.4
        ..strokeCap = StrokeCap.round,
    );
  }

  /// AutoCAD style adaptive grid with major lines every fifth line and the
  /// X and Y axes.
  void _cadGrid(Canvas canvas, Size size, Rect vis) {
    var step = d.gridSize;
    if (step <= 0) return;
    while (step * v.zoom < 10) {
      step *= 5;
    }
    while (step * v.zoom > 150) {
      step /= 5;
    }
    final major = step * 5;
    final fg = d.paper.foreground;
    final minorPath = Path();
    final majorPath = Path();
    bool isMajor(double t) => ((t / major) - (t / major).roundToDouble()).abs() < 1e-6;
    for (var x = (vis.left / step).floorToDouble() * step; x <= vis.right; x += step) {
      final sx = v.s(Offset(x, 0)).dx;
      (isMajor(x) ? majorPath : minorPath)
        ..moveTo(sx, 0)
        ..lineTo(sx, size.height);
    }
    for (var y = (vis.top / step).floorToDouble() * step; y <= vis.bottom; y += step) {
      final sy = v.s(Offset(0, y)).dy;
      (isMajor(y) ? majorPath : minorPath)
        ..moveTo(0, sy)
        ..lineTo(size.width, sy);
    }
    final dark = d.paper.isDark;
    canvas.drawPath(minorPath, _stroke(fg.withValues(alpha: dark ? .06 : .07), 1)..strokeCap = StrokeCap.butt);
    canvas.drawPath(majorPath, _stroke(fg.withValues(alpha: dark ? .13 : .14), 1)..strokeCap = StrokeCap.butt);
    final o = v.s(Offset.zero);
    if (o.dy >= 0 && o.dy <= size.height) {
      canvas.drawLine(Offset(0, o.dy), Offset(size.width, o.dy), _stroke(const Color(0x99E5484D), 1));
    }
    if (o.dx >= 0 && o.dx <= size.width) {
      canvas.drawLine(Offset(o.dx, 0), Offset(o.dx, size.height), _stroke(const Color(0x9930A46C), 1));
    }
  }

  // -- Entities ---------------------------------------------------------------

  void entities(Canvas canvas, Size size, {Set<String> selection = const {}}) {
    final vis = Rect.fromPoints(v.w(Offset.zero), v.w(Offset(size.width, size.height))).inflate(4 / v.zoom);
    for (final e in d.entities) {
      final layer = d.layer(e.layerId);
      if (!layer.visible) continue;
      if (!e.bounds.overlaps(vis) && e is! DimEnt) continue;
      if (selection.contains(e.id)) highlight(canvas, e);
      entity(canvas, e, alpha: layer.locked ? .45 : 1);
    }
  }

  void highlight(Canvas canvas, Entity e) {
    if (e is TextEnt) {
      final pts = [for (final p in e.corners) v.s(p)];
      canvas.drawPath(Path()..addPolygon(pts, true), _stroke(kSelectColor.withValues(alpha: .7), 1.5));
      canvas.drawPath(Path()..addPolygon(pts, true), Paint()..color = kSelectColor.withValues(alpha: .12));
      return;
    }
    entity(canvas, e, override: kSelectColor.withValues(alpha: .45), extraWidth: 5);
  }

  void entity(Canvas canvas, Entity e, {Color? override, double alpha = 1, double extraWidth = 0}) {
    var color = override ?? d.resolveColor(e);
    if (alpha < 1) color = color.withValues(alpha: color.a * alpha);
    final w = math.max(e.weight * weightScale, .8) + extraWidth;
    switch (e) {
      case InkStroke():
        _ink(canvas, e, color, extraWidth);
      case LineEnt():
        canvas.drawLine(v.s(e.a), v.s(e.b), _stroke(color, w));
      case PolyEnt():
        if (e.points.isEmpty) return;
        final path = Path()..addPolygon([for (final p in e.points) v.s(p)], e.closed);
        canvas.drawPath(path, _stroke(color, w));
      case CircleEnt():
        canvas.drawCircle(v.s(e.c), e.r * v.zoom, _stroke(color, w));
      case ArcEnt():
        canvas.drawArc(
          Rect.fromCircle(center: v.s(e.c), radius: e.r * v.zoom),
          -e.start,
          -e.sweep,
          false,
          _stroke(color, w),
        );
      case TextEnt():
        _text(canvas, e, color);
      case DimEnt():
        _dim(canvas, e, color, extraWidth);
    }
  }

  void _ink(Canvas canvas, InkStroke s, Color color, double extra) {
    final pts = [for (final p in s.points) v.s(p)];
    if (pts.isEmpty) return;
    final w = math.max(s.weight * v.zoom, .6) + extra;
    final c = switch (s.pen) {
      InkPen.highlighter => color.withValues(alpha: color.a * .4),
      InkPen.pencil => color.withValues(alpha: color.a * .85),
      InkPen.pen => color,
    };
    final paint = _stroke(c, w);
    if (s.pen == InkPen.highlighter) paint.strokeCap = StrokeCap.square;
    if (pts.length == 1) {
      canvas.drawCircle(pts.first, w / 2, Paint()..color = c);
      return;
    }
    if (s.pen == InkPen.pen && s.pressures.length == pts.length && extra == 0) {
      for (var i = 0; i + 1 < pts.length; i++) {
        final pr = (s.pressures[i] + s.pressures[i + 1]) / 2;
        paint.strokeWidth = w * (.35 + .95 * pr);
        canvas.drawLine(pts[i], pts[i + 1], paint);
      }
      return;
    }
    final path = Path()..moveTo(pts.first.dx, pts.first.dy);
    for (var i = 1; i + 1 < pts.length; i++) {
      final mid = (pts[i] + pts[i + 1]) / 2;
      path.quadraticBezierTo(pts[i].dx, pts[i].dy, mid.dx, mid.dy);
    }
    path.lineTo(pts.last.dx, pts.last.dy);
    canvas.drawPath(path, paint);
  }

  TextPainter _painter(String text, double fontSize, Color color) {
    final key = '$text|${fontSize.toStringAsFixed(1)}|${color.toARGB32()}';
    final hit = _textCache[key];
    if (hit != null) return hit;
    if (_textCache.length > 400) _textCache.clear();
    final tp = TextPainter(
      text: TextSpan(text: text, style: TextStyle(color: color, fontSize: fontSize, height: 1.0)),
      textDirection: TextDirection.ltr,
    )..layout();
    return _textCache[key] = tp;
  }

  void _text(Canvas canvas, TextEnt t, Color color) {
    final fs = t.height / .7 * v.zoom;
    final p = v.s(t.pos);
    if (fs < 2) {
      // Too small to read: show a hint line, as CAD programs do.
      final end = v.s(t.pos + dirOf(t.angle) * t.size.width);
      canvas.drawLine(p, end, _stroke(color.withValues(alpha: color.a * .5), 1));
      return;
    }
    if (fs > 4000) return;
    final tp = _painter(t.text, fs, color);
    final base = tp.computeDistanceToActualBaseline(TextBaseline.alphabetic);
    canvas.save();
    canvas.translate(p.dx, p.dy);
    if (t.angle != 0) canvas.rotate(-t.angle);
    tp.paint(canvas, Offset(0, -base));
    canvas.restore();
  }

  void _dim(Canvas canvas, DimEnt d, Color color, double extra) {
    final (d1w, d2w, _) = d.dimLine;
    final s1 = v.s(d1w), s2 = v.s(d2w), sa = v.s(d.a), sb = v.s(d.b);
    final k = annotationScale;
    final paint = _stroke(color, 1 + extra);
    void ext(Offset from, Offset to) {
      final dir = to - from;
      final len = dir.distance;
      if (len < 1) return;
      final u = dir / len;
      canvas.drawLine(from + u * math.min(3 * k, len / 2), to + u * 5 * k, paint);
    }

    ext(sa, s1);
    ext(sb, s2);
    canvas.drawLine(s1, s2, paint);
    final len = (s2 - s1).distance;
    if (len < 1 || extra > 0) return;
    final u = (s2 - s1) / len;
    final n = Offset(-u.dy, u.dx);
    final fill = Paint()..color = color;
    final al = 9 * k, aw = 3 * k;
    // Arrows point outwards when the dimension is too short for them.
    final inside = len > al * 2.6;
    for (final (tip, dir) in [(s1, inside ? u : -u), (s2, inside ? -u : u)]) {
      canvas.drawPath(
        Path()
          ..moveTo(tip.dx, tip.dy)
          ..lineTo((tip + dir * al + n * aw).dx, (tip + dir * al + n * aw).dy)
          ..lineTo((tip + dir * al - n * aw).dx, (tip + dir * al - n * aw).dy)
          ..close(),
        fill,
      );
    }
    final tp = _painter(this.d.formatLength(d.value), 12 * k, color);
    var ang = math.atan2(u.dy, u.dx);
    if (ang > math.pi / 2 + 1e-6 || ang < -math.pi / 2 + 1e-6) ang += math.pi;
    final up = Offset(math.sin(ang), -math.cos(ang));
    final center = (s1 + s2) / 2 + up * (tp.height / 2 + 2 * k);
    canvas.save();
    canvas.translate(center.dx, center.dy);
    canvas.rotate(ang);
    tp.paint(canvas, Offset(-tp.width / 2, -tp.height / 2));
    canvas.restore();
  }

  void grips(Canvas canvas, List<Entity> list) {
    final fill = Paint()..color = kGripColor;
    final border = _stroke(const Color(0xFFFFFFFF), 1)..strokeJoin = StrokeJoin.miter;
    for (final e in list) {
      for (final g in e.grips) {
        final r = Rect.fromCenter(center: v.s(g), width: 9, height: 9);
        canvas.drawRect(r, fill);
        canvas.drawRect(r, border);
      }
    }
  }
}

void dashedLine(Canvas canvas, Offset a, Offset b, Paint paint, {double dash = 6, double gap = 4}) {
  final len = (b - a).distance;
  if (len == 0) return;
  final u = (b - a) / len;
  for (var t = 0.0; t < len; t += dash + gap) {
    canvas.drawLine(a + u * t, a + u * math.min(t + dash, len), paint);
  }
}

/// Committed drawing: paper, grid, objects, selection and grips.
class ScenePainter extends CustomPainter {
  ScenePainter(this.c) : super(repaint: c.scene);

  final CanvasController c;

  @override
  void paint(Canvas canvas, Size size) {
    final r = DrawingRenderer(c.drawing, ViewXf(c.zoom, c.pan));
    r.background(canvas, size);
    r.entities(canvas, size, selection: c.selection);
    final sel = c.selectedEntities.where(c.editable).toList();
    if (sel.isNotEmpty && sel.length <= 40 && c.command == null && !c.tool.isInk) r.grips(canvas, sel);
  }

  @override
  bool shouldRepaint(ScenePainter old) => old.c != c;
}

/// Everything that follows the pointer: live ink, previews, selection
/// window, lasso, snap marker, crosshair and dynamic input.
class OverlayPainter extends CustomPainter {
  OverlayPainter(this.c, {required this.accent}) : super(repaint: c.overlay);

  final CanvasController c;
  final Color accent;

  @override
  void paint(Canvas canvas, Size size) {
    final d = c.drawing;
    final v = ViewXf(c.zoom, c.pan);
    final r = DrawingRenderer(d, v);
    final fg = d.paper.foreground;

    final live = c.liveStroke;
    if (live != null) r.entity(canvas, live);

    if (c.lassoPath.length > 1) {
      final pts = [for (final p in c.lassoPath) v.s(p)];
      final path = Path()..addPolygon(pts, true);
      canvas.drawPath(path, Paint()..color = kSelectColor.withValues(alpha: .08));
      for (var i = 0; i + 1 < pts.length; i += 2) {
        canvas.drawLine(pts[i], pts[i + 1], _stroke(kSelectColor, 1.5));
      }
    }

    if (c.windowStart != null && c.windowEnd != null) {
      final a = v.s(c.windowStart!), b = v.s(c.windowEnd!);
      final crossing = b.dx < a.dx;
      final rect = Rect.fromPoints(a, b);
      final col = crossing ? kCrossingColor : kSelectColor;
      canvas.drawRect(rect, Paint()..color = col.withValues(alpha: .14));
      final p = _stroke(col, 1)..strokeCap = StrokeCap.butt;
      if (crossing) {
        for (final (s, e) in [
          (rect.topLeft, rect.topRight),
          (rect.topRight, rect.bottomRight),
          (rect.bottomRight, rect.bottomLeft),
          (rect.bottomLeft, rect.topLeft),
        ]) {
          dashedLine(canvas, s, e, p);
        }
      } else {
        canvas.drawRect(rect, p);
      }
    }

    final m = c.measurement;
    if (m != null && m.length >= 2) {
      final pts = [for (final p in m) v.s(p)];
      if (m.length > 2) canvas.drawPath(Path()..addPolygon(pts, true), Paint()..color = accent.withValues(alpha: .18));
      for (var i = 0; i + 1 < pts.length; i++) {
        dashedLine(canvas, pts[i], pts[i + 1], _stroke(accent, 2));
      }
      for (final p in pts) {
        canvas.drawCircle(p, 3.5, Paint()..color = accent);
      }
    }

    final cmd = c.command;
    final cur = c.cursorWorld;
    final showCursor = c.cursorScreen != null;
    if (cmd != null && cur != null && showCursor) {
      final ghost = fg.withValues(alpha: .75);
      for (final e in cmd.preview(cur)) {
        if (cmd.fillPreview && e is PolyEnt) {
          canvas.drawPath(Path()..addPolygon([for (final p in e.points) v.s(p)], true), Paint()..color = accent.withValues(alpha: .2));
        }
        r.entity(canvas, e, override: e.color == null && e is! InkStroke ? ghost : null, alpha: .85);
      }
      for (final g in cmd.guides(cur)) {
        dashedLine(canvas, v.s(g.$1), v.s(g.$2), _stroke(fg.withValues(alpha: .5), 1), dash: 4, gap: 4);
      }
    }

    final sc = c.cursorScreen;
    if (sc != null) {
      if (c.tool == Tool.eraser && cmd == null) {
        canvas.drawCircle(sc, c.eraserSize / 2, _stroke(fg.withValues(alpha: .6), 1));
      } else if (!c.tool.isInk || cmd != null) {
        _crosshair(canvas, cur != null && cmd != null && !cmd.picksObject ? v.s(cur) : sc, fg, pickbox: cmd == null || cmd.picksObject);
      }
    }

    final hit = c.snapHit;
    if (hit != null && sc != null) _snapMarker(canvas, v.s(hit.p), hit.kind);

    if (cmd != null && cur != null && sc != null) {
      final label = cmd.dynLabel(cur);
      if (label != null) _tooltip(canvas, size, v.s(cur) + const Offset(18, 20), label, fg);
    }

    if (d.paper.isDark) _ucsIcon(canvas, size);
  }

  void _crosshair(Canvas canvas, Offset p, Color fg, {bool pickbox = false}) {
    final paint = _stroke(fg.withValues(alpha: .85), 1)..strokeCap = StrokeCap.butt;
    const arm = 36.0;
    const gap = 6.0;
    canvas.drawLine(p - const Offset(arm, 0), p - const Offset(gap, 0), paint);
    canvas.drawLine(p + const Offset(gap, 0), p + const Offset(arm, 0), paint);
    canvas.drawLine(p - const Offset(0, arm), p - const Offset(0, gap), paint);
    canvas.drawLine(p + const Offset(0, gap), p + const Offset(0, arm), paint);
    if (pickbox) canvas.drawRect(Rect.fromCenter(center: p, width: 10, height: 10), paint);
  }

  void _snapMarker(Canvas canvas, Offset p, SnapKind k) {
    final paint = _stroke(kSnapColor, 2)..strokeJoin = StrokeJoin.miter;
    const s = 7.0;
    switch (k) {
      case SnapKind.endpoint:
        canvas.drawRect(Rect.fromCenter(center: p, width: s * 2, height: s * 2), paint);
      case SnapKind.midpoint:
        canvas.drawPath(
          Path()
            ..moveTo(p.dx, p.dy - s)
            ..lineTo(p.dx + s, p.dy + s * .8)
            ..lineTo(p.dx - s, p.dy + s * .8)
            ..close(),
          paint,
        );
      case SnapKind.center:
        canvas.drawCircle(p, s, paint);
      case SnapKind.quadrant:
        canvas.drawPath(
          Path()
            ..moveTo(p.dx, p.dy - s)
            ..lineTo(p.dx + s, p.dy)
            ..lineTo(p.dx, p.dy + s)
            ..lineTo(p.dx - s, p.dy)
            ..close(),
          paint,
        );
      case SnapKind.intersection:
        canvas.drawLine(p - const Offset(s, s), p + const Offset(s, s), paint);
        canvas.drawLine(p + const Offset(-s, s), p + const Offset(s, -s), paint);
    }
    final tp = TextPainter(
      text: TextSpan(text: k.label, style: const TextStyle(color: Color(0xFF111111), fontSize: 11)),
      textDirection: TextDirection.ltr,
    )..layout();
    final box = Rect.fromLTWH(p.dx + 12, p.dy + 12, tp.width + 10, tp.height + 6);
    canvas.drawRRect(RRect.fromRectAndRadius(box, const Radius.circular(3)), Paint()..color = const Color(0xFFFFF4C2));
    tp.paint(canvas, box.topLeft + const Offset(5, 3));
  }

  void _tooltip(Canvas canvas, Size size, Offset at, String text, Color fg) {
    final tp = TextPainter(
      text: TextSpan(text: text, style: const TextStyle(color: Color(0xFFFFFFFF), fontSize: 12, fontFeatures: [FontFeature.tabularFigures()])),
      textDirection: TextDirection.ltr,
    )..layout();
    var box = Rect.fromLTWH(at.dx, at.dy + 16, tp.width + 12, tp.height + 8);
    if (box.right > size.width - 4) box = box.shift(Offset(size.width - 4 - box.right, 0));
    if (box.bottom > size.height - 4) box = box.shift(Offset(0, -box.height - 48));
    canvas.drawRRect(RRect.fromRectAndRadius(box, const Radius.circular(4)), Paint()..color = const Color(0xE62563EB));
    tp.paint(canvas, box.topLeft + const Offset(6, 4));
  }

  void _ucsIcon(Canvas canvas, Size size) {
    final o = Offset(18, size.height - 18);
    final x = _stroke(const Color(0xFFE5484D), 2);
    final y = _stroke(const Color(0xFF30A46C), 2);
    canvas.drawLine(o, o + const Offset(34, 0), x);
    canvas.drawLine(o, o - const Offset(0, 34), y);
    canvas.drawRect(Rect.fromCenter(center: o, width: 6, height: 6), Paint()..color = const Color(0xFFBBBBBB));
    for (final (label, at, col) in [
      ('X', o + const Offset(38, -7), const Color(0xFFE5484D)),
      ('Y', o + const Offset(-4, -50), const Color(0xFF30A46C)),
    ]) {
      final tp = TextPainter(
        text: TextSpan(text: label, style: TextStyle(color: col, fontSize: 11, fontWeight: FontWeight.w700)),
        textDirection: TextDirection.ltr,
      )..layout();
      tp.paint(canvas, at);
    }
  }

  @override
  bool shouldRepaint(OverlayPainter old) => old.c != c || old.accent != accent;
}

/// Static thumbnail of a drawing, fitted to its size.
class DrawingPreviewPainter extends CustomPainter {
  DrawingPreviewPainter(this.d, {this.paperLines = true});

  final Drawing d;
  final bool paperLines;

  @override
  void paint(Canvas canvas, Size size) {
    final ext = d.extents;
    final v = ext == null ? const ViewXf(1, Offset.zero) : ViewXf.fit(ext, size, margin: math.min(14, size.shortestSide * .08), maxZoom: d.paper.isDark ? double.infinity : 1.2);
    final small = size.shortestSide < 120;
    final r = DrawingRenderer(d, v, annotationScale: small ? .6 : .85, weightScale: small ? .7 : 1);
    canvas.save();
    canvas.clipRect(Offset.zero & size);
    if (paperLines) {
      r.background(canvas, size, cadGrid: d.paper.isDark);
    } else {
      canvas.drawRect(Offset.zero & size, Paint()..color = d.paper.background);
    }
    r.entities(canvas, size);
    canvas.restore();
  }

  @override
  bool shouldRepaint(DrawingPreviewPainter old) => true;
}
