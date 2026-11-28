import 'dart:math' as math;

import 'package:draft_notes/cad/command_parser.dart';
import 'package:draft_notes/cad/geometry.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('geometry', () {
    test('segment intersection is bounded, line intersection is not', () {
      expect(segmentIntersection(Offset.zero, const Offset(10, 10), const Offset(0, 10), const Offset(10, 0)), const Offset(5, 5));
      expect(segmentIntersection(Offset.zero, const Offset(1, 1), const Offset(0, 10), const Offset(10, 0)), isNull);
      expect(lineIntersection(Offset.zero, const Offset(1, 1), const Offset(0, 10), const Offset(10, 0)), const Offset(5, 5));
    });

    test('circle through three points', () {
      final (c, r) = circleFrom3(const Offset(10, 0), const Offset(0, 10), const Offset(-10, 0))!;
      expect(c.dx, closeTo(0, 1e-9));
      expect(c.dy, closeTo(0, 1e-9));
      expect(r, closeTo(10, 1e-9));
      expect(circleFrom3(Offset.zero, const Offset(1, 1), const Offset(2, 2)), isNull);
    });

    test('segment and circle intersections', () {
      final pts = segmentCircleIntersections(const Offset(-20, 0), const Offset(20, 0), Offset.zero, 10);
      expect(pts.map((p) => p.dx.round()).toSet(), {-10, 10});
      expect(circleCircleIntersections(Offset.zero, 5, const Offset(8, 0), 5), hasLength(2));
    });

    test('area, length and point in polygon', () {
      final sq = [Offset.zero, const Offset(4, 0), const Offset(4, 3), const Offset(0, 3)];
      expect(polygonArea(sq), 12);
      expect(polylineLength(sq, closed: true), 14);
      expect(pointInPolygon(const Offset(1, 1), sq), isTrue);
      expect(pointInPolygon(const Offset(5, 1), sq), isFalse);
    });

    test('arc sweep test handles both directions and wrap-around', () {
      expect(angleInSweep(0.1, -0.5, 1), isTrue);
      expect(angleInSweep(math.pi, 0, math.pi / 2), isFalse);
      expect(angleInSweep(-math.pi / 4, 0, -math.pi / 2), isTrue);
    });

    test('offset polyline mitres the corners', () {
      final out = offsetPolyline([Offset.zero, const Offset(10, 0), const Offset(10, 10)], 1);
      expect(out[0], const Offset(0, 1));
      expect(out[1].dx, closeTo(9, 1e-9));
      expect(out[1].dy, closeTo(1, 1e-9));
      expect(out[2], const Offset(9, 10));
      final box = offsetPolyline([Offset.zero, const Offset(10, 0), const Offset(10, 10), const Offset(0, 10)], -1, closed: true);
      expect(polygonArea(box).abs(), closeTo(144, 1e-9));
    });

    test('nice steps', () {
      expect(niceStep(3), 5);
      expect(niceStep(0.7), 1);
      expect(niceStep(120), 200);
    });
  });

  group('command line input', () {
    test('absolute, relative and polar points', () {
      expect(parsePoint('10,20'), const Offset(10, 20));
      expect(parsePoint('@5,-5', last: const Offset(10, 20)), const Offset(15, 15));
      final p = parsePoint('@10<90', last: const Offset(1, 1))!;
      expect(p.dx, closeTo(1, 1e-9));
      expect(p.dy, closeTo(11, 1e-9));
      expect(parsePoint('#3,4', last: const Offset(100, 100)), const Offset(3, 4));
      expect(parsePoint('@', last: const Offset(7, 8)), const Offset(7, 8));
      expect(parsePoint('LINE'), isNull);
      expect(parsePoint('12'), isNull);
    });

    test('aliases resolve to commands', () {
      expect(commandAliases['L'], 'LINE');
      expect(commandAliases['REC'], 'RECTANG');
      expect(commandAliases['DI'], 'DIST');
      expect(commandAliases['O'], 'OFFSET');
    });
  });
}
