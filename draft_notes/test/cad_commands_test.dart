import 'dart:math' as math;

import 'package:draft_notes/cad/canvas_controller.dart';
import 'package:draft_notes/models/drawing.dart';
import 'package:flutter_test/flutter_test.dart';

CanvasController _cad() => CanvasController(Drawing.cad()..entities.clear());

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  test('LINE with typed coordinates and Close', () {
    final c = _cad();
    for (final s in ['L', '0,0', '100,0', '@0,50', 'C']) {
      c.submit(s);
    }
    final lines = c.drawing.entities.whereType<LineEnt>().toList();
    expect(lines, hasLength(3));
    expect(lines[1].b, const Offset(100, 50));
    expect(lines.last.b, Offset.zero);
    // The tool stays on LINE, ready for a new chain.
    expect(c.command?.name, 'LINE');
    expect(c.command?.pts, isEmpty);
  });

  test('LINE U removes the last segment', () {
    final c = _cad();
    for (final s in ['LINE', '0,0', '10,0', '10,10', 'U']) {
      c.submit(s);
    }
    expect(c.drawing.entities, hasLength(1));
    expect(c.command?.pts.last, const Offset(10, 0));
  });

  test('RECTANG, CIRCLE by radius and polar ARC', () {
    final c = _cad();
    for (final s in ['REC', '0,0', '@40,20']) {
      c.submit(s);
    }
    c.escape();
    for (final s in ['C', '5,5', '25']) {
      c.submit(s);
    }
    c.escape();
    for (final s in ['A', '10,0', '0,10', '-10,0']) {
      c.submit(s);
    }
    final rect = c.drawing.entities.whereType<PolyEnt>().single;
    expect(rect.closed, isTrue);
    expect(rect.label, 'Rectangle');
    expect(rect.points[2], const Offset(40, 20));
    expect(c.drawing.entities.whereType<CircleEnt>().single.r, 25);
    final arc = c.drawing.entities.whereType<ArcEnt>().single;
    expect(arc.r, closeTo(10, 1e-9));
    expect(arc.sweep, closeTo(math.pi, 1e-9));
  });

  test('PLINE closes, AREA and DIST report results', () {
    final c = _cad();
    for (final s in ['PL', '0,0', '30,0', '30,40', 'C']) {
      c.submit(s);
    }
    expect(c.drawing.entities.whereType<PolyEnt>().single.points, hasLength(3));
    c.escape();
    for (final s in ['AA', '0,0', '30,0', '30,40', '']) {
      c.submit(s);
    }
    expect(c.log.any((l) => l.startsWith('Area = 600')), isTrue);
    c.escape();
    for (final s in ['DI', '0,0', '30,40']) {
      c.submit(s);
    }
    expect(c.log.any((l) => l.startsWith('Distance = 50')), isTrue);
  });

  test('MOVE, ROTATE, SCALE, COPY and MIRROR act on the selection', () {
    final c = _cad();
    c.submit('L');
    c.submit('0,0');
    c.submit('10,0');
    c.escape();
    c.escape();
    final line = c.drawing.entities.single as LineEnt;
    c.selection.add(line.id);
    for (final s in ['M', '0,0', '5,5']) {
      c.submit(s);
    }
    expect((c.drawing.entities.single as LineEnt).a, const Offset(5, 5));
    for (final s in ['RO', '5,5', '90']) {
      c.submit(s);
    }
    final rotated = c.drawing.entities.single as LineEnt;
    expect(rotated.b.dx, closeTo(5, 1e-9));
    expect(rotated.b.dy, closeTo(15, 1e-9));
    for (final s in ['SC', '5,5', '2']) {
      c.submit(s);
    }
    expect((c.drawing.entities.single as LineEnt).length, closeTo(20, 1e-9));
    for (final s in ['CO', '0,0', '100,0', '200,0', '']) {
      c.submit(s);
    }
    expect(c.drawing.entities, hasLength(3));
    for (final s in ['MI', '0,0', '0,10']) {
      c.submit(s);
    }
    final mirrored = c.drawing.entities.last as LineEnt;
    expect(mirrored.a.dx, closeTo(-5, 1e-9));
  });

  test('modify commands ask for a selection first', () {
    final c = _cad();
    c.drawing.entities.add(LineEnt(layerId: '0', a: Offset.zero, b: const Offset(1, 0)));
    c.submit('E');
    expect(c.command?.prompt, contains('Select objects'));
    c.submit('ALL');
    c.submit('');
    expect(c.drawing.entities, isEmpty);
  });

  test('OFFSET a line to the picked side', () {
    final c = _cad();
    final line = LineEnt(layerId: '0', a: Offset.zero, b: const Offset(100, 0));
    c.drawing.entities.add(line);
    c.zoom = 1;
    c.submit('O');
    c.submit('10');
    c.command!.pick(const Offset(50, 0));
    c.command!.pick(const Offset(50, 30));
    final copy = c.drawing.entities.last as LineEnt;
    expect(copy.a, const Offset(0, 10));
    expect(copy.b, const Offset(100, 10));
  });

  test('undo and redo restore the drawing', () {
    final c = _cad();
    for (final s in ['C', '0,0', '5']) {
      c.submit(s);
    }
    expect(c.drawing.entities, hasLength(1));
    c.undo();
    expect(c.drawing.entities, isEmpty);
    c.redo();
    expect(c.drawing.entities, hasLength(1));
  });

  test('object snap finds end points, mid points and intersections', () {
    final c = _cad();
    c.zoom = 1;
    c.drawing.entities.addAll([
      LineEnt(layerId: '0', a: const Offset(0, 0), b: const Offset(100, 100)),
      LineEnt(layerId: '0', a: const Offset(0, 100), b: const Offset(100, 0)),
    ]);
    c.drawing.osnapOn = true;
    c.pan = const Offset(0, 200);
    // Screen (102, 98) is world (102, 102): near the end point (100, 100).
    expect(c.snapPoint(const Offset(102, 98)), const Offset(100, 100));
    expect(c.snapHit?.kind, SnapKind.endpoint);
    final p = c.snapPoint(c.toScreen(const Offset(51, 49)));
    expect(p.dx, closeTo(50, 1e-9));
    expect(p.dy, closeTo(50, 1e-9));
    expect(c.snapHit?.kind, SnapKind.intersection);
  });

  test('ortho and grid snap', () {
    final c = _cad();
    c.zoom = 1;
    c.drawing
      ..osnapOn = false
      ..orthoOn = true
      ..snapOn = true
      ..gridSize = 10;
    final p = c.snapPoint(c.toScreen(const Offset(47, 3)), base: Offset.zero);
    expect(p, const Offset(50, 0));
  });

  test('layers: deleting moves objects to layer 0', () {
    final c = _cad();
    final l = c.addLayer('Furniture', 0xFFFF0000);
    expect(c.drawing.currentLayerId, l.id);
    for (final s in ['C', '0,0', '5']) {
      c.submit(s);
    }
    expect(c.drawing.entities.single.layerId, l.id);
    c.deleteLayer(l);
    expect(c.drawing.entities.single.layerId, '0');
  });
}
