import 'dart:math' as math;
import 'dart:ui';

/// A straight segment between two points.
typedef Seg = (Offset, Offset);

const double kTwoPi = math.pi * 2;

double cross(Offset a, Offset b) => a.dx * b.dy - a.dy * b.dx;
double dot(Offset a, Offset b) => a.dx * b.dx + a.dy * b.dy;

/// Unit vector at [angle] (radians, counter-clockwise from +X).
Offset dirOf(double angle) => Offset(math.cos(angle), math.sin(angle));

Offset polar(Offset origin, double dist, double angle) => origin + dirOf(angle) * dist;

double angleOf(Offset from, Offset to) => math.atan2(to.dy - from.dy, to.dx - from.dx);

/// Normalises an angle into [0, 2π).
double normAngle(double a) {
  final r = a % kTwoPi;
  return r < 0 ? r + kTwoPi : r;
}

/// Whether [a] lies on the arc that starts at [start] and sweeps by [sweep]
/// (positive = counter-clockwise).
bool angleInSweep(double a, double start, double sweep) {
  if (sweep >= 0) return normAngle(a - start) <= sweep + 1e-9;
  return normAngle(start - a) <= -sweep + 1e-9;
}

Offset nearestOnSegment(Offset p, Offset a, Offset b) {
  final ab = b - a;
  final len2 = dot(ab, ab);
  if (len2 == 0) return a;
  final t = (dot(p - a, ab) / len2).clamp(0.0, 1.0);
  return a + ab * t;
}

double distToSegment(Offset p, Offset a, Offset b) => (p - nearestOnSegment(p, a, b)).distance;

/// Intersection of the infinite lines a1-a2 and b1-b2.
Offset? lineIntersection(Offset a1, Offset a2, Offset b1, Offset b2) {
  final r = a2 - a1;
  final s = b2 - b1;
  final denom = cross(r, s);
  if (denom.abs() < 1e-12) return null;
  final t = cross(b1 - a1, s) / denom;
  return a1 + r * t;
}

/// Intersection of two bounded segments.
Offset? segmentIntersection(Offset a1, Offset a2, Offset b1, Offset b2) {
  final r = a2 - a1;
  final s = b2 - b1;
  final denom = cross(r, s);
  if (denom.abs() < 1e-12) return null;
  final t = cross(b1 - a1, s) / denom;
  final u = cross(b1 - a1, r) / denom;
  const e = 1e-9;
  if (t < -e || t > 1 + e || u < -e || u > 1 + e) return null;
  return a1 + r * t;
}

List<Offset> segmentCircleIntersections(Offset a, Offset b, Offset c, double r) {
  final d = b - a;
  final f = a - c;
  final qa = dot(d, d);
  if (qa == 0) return const [];
  final qb = 2 * dot(f, d);
  final qc = dot(f, f) - r * r;
  final disc = qb * qb - 4 * qa * qc;
  if (disc < 0) return const [];
  final sq = math.sqrt(disc);
  final out = <Offset>[];
  for (final t in [(-qb - sq) / (2 * qa), (-qb + sq) / (2 * qa)]) {
    if (t >= -1e-9 && t <= 1 + 1e-9) out.add(a + d * t);
  }
  return out;
}

List<Offset> circleCircleIntersections(Offset c1, double r1, Offset c2, double r2) {
  final d = (c2 - c1).distance;
  if (d == 0 || d > r1 + r2 || d < (r1 - r2).abs()) return const [];
  final a = (r1 * r1 - r2 * r2 + d * d) / (2 * d);
  final h2 = r1 * r1 - a * a;
  final h = h2 <= 0 ? 0.0 : math.sqrt(h2);
  final u = (c2 - c1) / d;
  final m = c1 + u * a;
  final n = Offset(-u.dy, u.dx);
  if (h == 0) return [m];
  return [m + n * h, m - n * h];
}

/// Circle through three points, or null when they are collinear.
(Offset, double)? circleFrom3(Offset a, Offset b, Offset c) {
  final d = 2 * (a.dx * (b.dy - c.dy) + b.dx * (c.dy - a.dy) + c.dx * (a.dy - b.dy));
  if (d.abs() < 1e-12) return null;
  final a2 = a.dx * a.dx + a.dy * a.dy;
  final b2 = b.dx * b.dx + b.dy * b.dy;
  final c2 = c.dx * c.dx + c.dy * c.dy;
  final ux = (a2 * (b.dy - c.dy) + b2 * (c.dy - a.dy) + c2 * (a.dy - b.dy)) / d;
  final uy = (a2 * (c.dx - b.dx) + b2 * (a.dx - c.dx) + c2 * (b.dx - a.dx)) / d;
  final center = Offset(ux, uy);
  return (center, (a - center).distance);
}

bool pointInPolygon(Offset p, List<Offset> poly) {
  var inside = false;
  for (var i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    final a = poly[i];
    final b = poly[j];
    if ((a.dy > p.dy) != (b.dy > p.dy) &&
        p.dx < (b.dx - a.dx) * (p.dy - a.dy) / (b.dy - a.dy) + a.dx) {
      inside = !inside;
    }
  }
  return inside;
}

/// Signed shoelace area (positive when counter-clockwise).
double polygonArea(List<Offset> pts) {
  var s = 0.0;
  for (var i = 0; i < pts.length; i++) {
    s += cross(pts[i], pts[(i + 1) % pts.length]);
  }
  return s / 2;
}

double polylineLength(List<Offset> pts, {bool closed = false}) {
  var s = 0.0;
  for (var i = 0; i + 1 < pts.length; i++) {
    s += (pts[i + 1] - pts[i]).distance;
  }
  if (closed && pts.length > 2) s += (pts.first - pts.last).distance;
  return s;
}

Offset rotateAround(Offset p, Offset c, double angle) {
  final cs = math.cos(angle);
  final sn = math.sin(angle);
  final d = p - c;
  return c + Offset(d.dx * cs - d.dy * sn, d.dx * sn + d.dy * cs);
}

Offset mirrorAcross(Offset p, Offset a, Offset b) {
  final q = nearestOnInfiniteLine(p, a, b);
  return q * 2 - p;
}

Offset nearestOnInfiniteLine(Offset p, Offset a, Offset b) {
  final ab = b - a;
  final len2 = dot(ab, ab);
  if (len2 == 0) return a;
  return a + ab * (dot(p - a, ab) / len2);
}

Rect boundsOf(Iterable<Offset> pts) {
  var minX = double.infinity, minY = double.infinity;
  var maxX = -double.infinity, maxY = -double.infinity;
  for (final p in pts) {
    minX = math.min(minX, p.dx);
    minY = math.min(minY, p.dy);
    maxX = math.max(maxX, p.dx);
    maxY = math.max(maxY, p.dy);
  }
  if (minX == double.infinity) return Rect.zero;
  return Rect.fromLTRB(minX, minY, maxX, maxY);
}

Rect unionRect(Rect? a, Rect b) => a == null ? b : a.expandToInclude(b);

/// Whether the segment touches the rectangle (inside or crossing an edge).
bool segmentTouchesRect(Offset a, Offset b, Rect r) {
  if (r.contains(a) || r.contains(b)) return true;
  final corners = [r.topLeft, r.topRight, r.bottomRight, r.bottomLeft];
  for (var i = 0; i < 4; i++) {
    if (segmentIntersection(a, b, corners[i], corners[(i + 1) % 4]) != null) return true;
  }
  return false;
}

/// Offsets an open or closed polyline by [d] to the left of its direction of
/// travel (negative = right). Corners are mitred, like AutoCAD's OFFSET.
List<Offset> offsetPolyline(List<Offset> pts, double d, {bool closed = false}) {
  final n = pts.length;
  if (n < 2) return List.of(pts);
  final segCount = closed ? n : n - 1;
  final lines = <(Offset, Offset)>[];
  for (var i = 0; i < segCount; i++) {
    final a = pts[i];
    final b = pts[(i + 1) % n];
    final dir = b - a;
    final len = dir.distance;
    if (len == 0) {
      lines.add((a, b));
      continue;
    }
    final nrm = Offset(-dir.dy, dir.dx) / len * d;
    lines.add((a + nrm, b + nrm));
  }
  final out = <Offset>[];
  for (var i = 0; i < n; i++) {
    if (!closed && i == 0) {
      out.add(lines.first.$1);
    } else if (!closed && i == n - 1) {
      out.add(lines.last.$2);
    } else {
      final prev = lines[(i - 1 + segCount) % segCount];
      final next = lines[i % segCount];
      out.add(lineIntersection(prev.$1, prev.$2, next.$1, next.$2) ?? next.$1);
    }
  }
  return out;
}

/// Rounds [v] to a "nice" value (1, 2, 5 × 10ⁿ) at or above it.
double niceStep(double v) {
  if (v <= 0) return 1;
  final exp = math.pow(10, (math.log(v) / math.ln10).floor()).toDouble();
  final f = v / exp;
  final nice = f <= 1 ? 1 : (f <= 2 ? 2 : (f <= 5 ? 5 : 10));
  return nice * exp;
}
