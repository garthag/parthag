import 'dart:math' as math;
import 'dart:typed_data';
import 'dart:ui' as ui;
import 'dart:ui' show Offset, Rect;

import '../models/drawing.dart';
import 'geometry.dart';
import 'painter.dart';

// ---------------------------------------------------------------------------
// DXF (AutoCAD R12 ASCII), readable by AutoCAD, LibreCAD, QCAD, DraftSight.
// ---------------------------------------------------------------------------

const _aci = <int, (int, int, int)>{
  1: (255, 0, 0),
  2: (255, 255, 0),
  3: (0, 255, 0),
  4: (0, 255, 255),
  5: (0, 0, 255),
  6: (255, 0, 255),
  8: (128, 128, 128),
  9: (192, 192, 192),
  30: (255, 127, 0),
  40: (255, 191, 0),
  140: (0, 127, 255),
  200: (191, 0, 255),
  210: (255, 0, 191),
};

/// Nearest AutoCAD Color Index for an ARGB colour; 7 for black/white.
int aciFor(int argb) {
  if (argb == kAdaptiveColor) return 7;
  final r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
  final maxC = math.max(r, math.max(g, b)), minC = math.min(r, math.min(g, b));
  if (maxC - minC < 24 && (maxC > 225 || maxC < 40)) return 7;
  var best = 7;
  var bestD = double.infinity;
  _aci.forEach((index, c) {
    final d = math.pow(r - c.$1, 2) + math.pow(g - c.$2, 2) + math.pow(b - c.$3, 2);
    if (d < bestD) {
      bestD = d.toDouble();
      best = index;
    }
  });
  return best;
}

String _dxfName(String name) {
  final s = name.toUpperCase().replaceAll(RegExp(r'[^A-Z0-9_\-$]'), '_');
  return s.isEmpty ? 'LAYER' : s;
}

String _n(double v) {
  if (v.isNaN || v.isInfinite) return '0.0';
  final s = v.toStringAsFixed(6);
  return s.contains('.') ? s.replaceFirst(RegExp(r'0+$'), '').replaceFirst(RegExp(r'\.$'), '.0') : s;
}

String drawingToDxf(Drawing d) {
  final b = StringBuffer();
  void g(int code, Object v) {
    b
      ..write(code.toString().padLeft(3))
      ..write('\r\n')
      ..write(v is double ? _n(v) : v)
      ..write('\r\n');
  }

  final names = <String, String>{};
  final used = <String>{};
  for (final l in d.layers) {
    var n = l.id == d.layers.first.id ? '0' : _dxfName(l.name);
    var i = 1;
    final base = n;
    while (used.contains(n)) {
      n = '${base}_${i++}';
    }
    used.add(n);
    names[l.id] = n;
  }

  final ext = d.extents ?? Rect.zero;
  g(0, 'SECTION');
  g(2, 'HEADER');
  g(9, r'$ACADVER');
  g(1, 'AC1009');
  g(9, r'$EXTMIN');
  g(10, ext.left);
  g(20, ext.top);
  g(30, 0.0);
  g(9, r'$EXTMAX');
  g(10, ext.right);
  g(20, ext.bottom);
  g(30, 0.0);
  g(0, 'ENDSEC');

  g(0, 'SECTION');
  g(2, 'TABLES');
  g(0, 'TABLE');
  g(2, 'LTYPE');
  g(70, 1);
  g(0, 'LTYPE');
  g(2, 'CONTINUOUS');
  g(70, 0);
  g(3, 'Solid line');
  g(72, 65);
  g(73, 0);
  g(40, 0.0);
  g(0, 'ENDTAB');
  g(0, 'TABLE');
  g(2, 'LAYER');
  g(70, d.layers.length);
  for (final l in d.layers) {
    g(0, 'LAYER');
    g(2, names[l.id]!);
    g(70, l.locked ? 4 : 0);
    g(62, aciFor(l.color) * (l.visible ? 1 : -1));
    g(6, 'CONTINUOUS');
  }
  g(0, 'ENDTAB');
  g(0, 'ENDSEC');

  g(0, 'SECTION');
  g(2, 'ENTITIES');
  void head(String type, Entity e, {int? color}) {
    g(0, type);
    g(8, names[e.layerId] ?? '0');
    final c = color ?? e.color;
    if (c != null) g(62, aciFor(c));
  }

  void line(Entity e, Offset p, Offset q) {
    head('LINE', e);
    g(10, p.dx);
    g(20, p.dy);
    g(30, 0.0);
    g(11, q.dx);
    g(21, q.dy);
    g(31, 0.0);
  }

  void polyline(Entity e, List<Offset> pts, {bool closed = false, double width = 0}) {
    head('POLYLINE', e);
    g(66, 1);
    g(10, 0.0);
    g(20, 0.0);
    g(30, 0.0);
    g(70, closed ? 1 : 0);
    if (width > 0) {
      g(40, width);
      g(41, width);
    }
    for (final p in pts) {
      g(0, 'VERTEX');
      g(8, names[e.layerId] ?? '0');
      g(10, p.dx);
      g(20, p.dy);
      g(30, 0.0);
    }
    g(0, 'SEQEND');
    g(8, names[e.layerId] ?? '0');
  }

  void text(Entity e, Offset p, String s, double h, double angleRad) {
    head('TEXT', e);
    g(10, p.dx);
    g(20, p.dy);
    g(30, 0.0);
    g(40, h);
    g(1, s);
    if (angleRad != 0) g(50, angleRad * 180 / math.pi);
  }

  final annoH = d.textHeight * .7;
  for (final e in d.entities) {
    switch (e) {
      case InkStroke():
        if (e.points.length == 1) {
          head('CIRCLE', e);
          g(10, e.points.first.dx);
          g(20, e.points.first.dy);
          g(30, 0.0);
          g(40, e.weight / 2);
        } else {
          polyline(e, e.points, width: e.weight);
        }
      case LineEnt():
        line(e, e.a, e.b);
      case PolyEnt():
        polyline(e, e.points, closed: e.closed);
      case CircleEnt():
        head('CIRCLE', e);
        g(10, e.c.dx);
        g(20, e.c.dy);
        g(30, 0.0);
        g(40, e.r);
      case ArcEnt():
        head('ARC', e);
        g(10, e.c.dx);
        g(20, e.c.dy);
        g(30, 0.0);
        g(40, e.r);
        final a = e.sweep >= 0 ? e.start : e.start + e.sweep;
        g(50, normAngle(a) * 180 / math.pi);
        g(51, normAngle(a + e.sweep.abs()) * 180 / math.pi);
      case TextEnt():
        final down = dirOf(e.angle - math.pi / 2) * e.height * 1.4;
        final lines = e.lines;
        for (var i = 0; i < lines.length; i++) {
          if (lines[i].isNotEmpty) text(e, e.pos + down * i.toDouble(), lines[i], e.height, e.angle);
        }
      case DimEnt():
        // Exploded into lines and text, which every DXF reader supports.
        final (d1, d2, n) = e.dimLine;
        line(e, e.a, d1 + n * annoH * .3);
        line(e, e.b, d2 + n * annoH * .3);
        line(e, d1, d2);
        final len = (d2 - d1).distance;
        if (len > 0) {
          final u = (d2 - d1) / len;
          final perp = Offset(-u.dy, u.dx);
          for (final (tip, dir) in [(d1, u), (d2, -u)]) {
            line(e, tip, tip + dir * annoH + perp * annoH * .3);
            line(e, tip, tip + dir * annoH - perp * annoH * .3);
          }
          var ang = math.atan2(u.dy, u.dx);
          if (ang > math.pi / 2 || ang <= -math.pi / 2) ang += math.pi;
          final label = d.formatLength(e.value);
          final along = dirOf(ang);
          final up = dirOf(ang + math.pi / 2);
          final start = (d1 + d2) / 2 - along * (label.length * annoH * .36) + up * annoH * .4;
          text(e, start, label, annoH, ang);
        }
    }
  }
  g(0, 'ENDSEC');
  g(0, 'EOF');
  return b.toString();
}

// ---------------------------------------------------------------------------
// SVG
// ---------------------------------------------------------------------------

String _hex(ui.Color c) => '#${(c.toARGB32() & 0xFFFFFF).toRadixString(16).padLeft(6, '0')}';

String _esc(String s) =>
    s.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');

String drawingToSvg(Drawing d, {double pixelWidth = 1200}) {
  final ext = (d.extents ?? const Rect.fromLTWH(0, 0, 100, 100));
  final margin = math.max(ext.longestSide, 1) * .05;
  final r = ext.inflate(margin);
  final k = r.width / pixelWidth; // world units per output pixel
  final heightPx = pixelWidth * r.height / r.width;
  String x(double v) => _n(v - r.left);
  String y(double v) => _n(r.bottom - v);
  String pt(Offset p) => '${x(p.dx)},${y(p.dy)}';

  final b = StringBuffer()
    ..writeln('<?xml version="1.0" encoding="UTF-8"?>')
    ..writeln('<svg xmlns="http://www.w3.org/2000/svg" width="${_n(pixelWidth)}" height="${_n(heightPx)}" '
        'viewBox="0 0 ${_n(r.width)} ${_n(r.height)}">')
    ..writeln('<rect width="100%" height="100%" fill="${_hex(d.paper.background)}"/>')
    ..writeln('<g fill="none" stroke-linecap="round" stroke-linejoin="round">');

  for (final e in d.entities) {
    final layer = d.layer(e.layerId);
    if (!layer.visible) continue;
    final col = _hex(d.resolveColor(e));
    final sw = _n(math.max(e.weight, .8) * k);
    switch (e) {
      case InkStroke():
        final op = switch (e.pen) {
          InkPen.highlighter => .4,
          InkPen.pencil => .85,
          InkPen.pen => 1.0,
        };
        b.writeln('<polyline points="${e.points.map(pt).join(' ')}" stroke="$col" stroke-width="${_n(e.weight)}" '
            'stroke-opacity="$op"${e.pen == InkPen.highlighter ? ' stroke-linecap="square"' : ''}/>');
      case LineEnt():
        b.writeln('<line x1="${x(e.a.dx)}" y1="${y(e.a.dy)}" x2="${x(e.b.dx)}" y2="${y(e.b.dy)}" stroke="$col" stroke-width="$sw"/>');
      case PolyEnt():
        b.writeln('<${e.closed ? 'polygon' : 'polyline'} points="${e.points.map(pt).join(' ')}" stroke="$col" stroke-width="$sw"/>');
      case CircleEnt():
        b.writeln('<circle cx="${x(e.c.dx)}" cy="${y(e.c.dy)}" r="${_n(e.r)}" stroke="$col" stroke-width="$sw"/>');
      case ArcEnt():
        final large = e.sweep.abs() > math.pi ? 1 : 0;
        // SVG's y axis points down, so counter-clockwise becomes sweep-flag 0.
        final sweepFlag = e.sweep > 0 ? 0 : 1;
        b.writeln('<path d="M ${pt(e.p1)} A ${_n(e.r)} ${_n(e.r)} 0 $large $sweepFlag ${pt(e.p2)}" stroke="$col" stroke-width="$sw"/>');
      case TextEnt():
        final fs = e.height / .7;
        final deg = -e.angle * 180 / math.pi;
        final lines = e.lines;
        b.write('<text x="${x(e.pos.dx)}" y="${y(e.pos.dy)}" font-family="Helvetica, Arial, sans-serif" '
            'font-size="${_n(fs)}" fill="$col" stroke="none"'
            '${deg != 0 ? ' transform="rotate(${_n(deg)} ${x(e.pos.dx)} ${y(e.pos.dy)})"' : ''}>');
        for (var i = 0; i < lines.length; i++) {
          b.write('<tspan x="${x(e.pos.dx)}" dy="${i == 0 ? 0 : _n(fs)}">${_esc(lines[i])}</tspan>');
        }
        b.writeln('</text>');
      case DimEnt():
        final (d1, d2, n) = e.dimLine;
        final a = 9 * k;
        b.writeln('<g stroke="$col" stroke-width="${_n(k)}">');
        b.writeln('<line x1="${x(e.a.dx)}" y1="${y(e.a.dy)}" x2="${x((d1 + n * 5 * k).dx)}" y2="${y((d1 + n * 5 * k).dy)}"/>');
        b.writeln('<line x1="${x(e.b.dx)}" y1="${y(e.b.dy)}" x2="${x((d2 + n * 5 * k).dx)}" y2="${y((d2 + n * 5 * k).dy)}"/>');
        b.writeln('<line x1="${x(d1.dx)}" y1="${y(d1.dy)}" x2="${x(d2.dx)}" y2="${y(d2.dy)}"/>');
        final len = (d2 - d1).distance;
        if (len > 0) {
          final u = (d2 - d1) / len;
          final perp = Offset(-u.dy, u.dx);
          for (final (tip, dir) in [(d1, u), (d2, -u)]) {
            b.writeln('<polygon fill="$col" stroke="none" points="${pt(tip)} ${pt(tip + dir * a + perp * a / 3)} ${pt(tip + dir * a - perp * a / 3)}"/>');
          }
          var ang = math.atan2(u.dy, u.dx);
          if (ang > math.pi / 2 || ang <= -math.pi / 2) ang += math.pi;
          final c = (d1 + d2) / 2 + dirOf(ang + math.pi / 2) * 4 * k;
          b.writeln('<text x="${x(c.dx)}" y="${y(c.dy)}" text-anchor="middle" font-family="Helvetica, Arial, sans-serif" '
              'font-size="${_n(12 * k)}" fill="$col" stroke="none" transform="rotate(${_n(-ang * 180 / math.pi)} ${x(c.dx)} ${y(c.dy)})">'
              '${_esc(d.formatLength(e.value))}</text>');
        }
        b.writeln('</g>');
    }
  }
  b
    ..writeln('</g>')
    ..writeln('</svg>');
  return b.toString();
}

// ---------------------------------------------------------------------------
// PNG
// ---------------------------------------------------------------------------

Future<Uint8List> drawingToPng(Drawing d, {double width = 1600}) async {
  final ext = d.extents ?? const Rect.fromLTWH(0, 0, 100, 100);
  final aspect = (ext.height + 1e-6) / (ext.width + 1e-6);
  final size = ui.Size(width, (width * aspect).clamp(width * .3, width * 2.5));
  final v = ViewXf.fit(ext, size, margin: width * .04, maxZoom: d.paper.isDark ? double.infinity : 3);
  final rec = ui.PictureRecorder();
  final canvas = ui.Canvas(rec);
  final r = DrawingRenderer(d, v, annotationScale: width / 1000, weightScale: width / 1000);
  r.background(canvas, size, cadGrid: false);
  r.entities(canvas, size);
  final img = await rec.endRecording().toImage(size.width.round(), size.height.round());
  final data = await img.toByteData(format: ui.ImageByteFormat.png);
  return data!.buffer.asUint8List();
}
