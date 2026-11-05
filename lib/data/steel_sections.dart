// Indian Standard steel sections and their unit weights (kg per metre).
//
// Rolled sections (ISMB, ISMC, ISLB, ISJB, ISHB, ISWB, ISLC, ISJC, ISA) carry
// the nominal mass from IS 808 "Dimensions for hot rolled steel beam, column,
// channel and angle sections" and the ISI steel table SP 6(1). UB / UC are
// the British BS 4-1 sections that are also rolled and stocked in India.
//
// Plates, flats, bars, pipes and hollow sections are worked out from their
// size using the density of steel, 7850 kg/m³ (IS 1730, IS 1731, IS 1732,
// IS 1786, IS 1239, IS 4923). Figures are nominal; a mill's actual weight
// varies within the rolling tolerance of IS 1852, so check the supplier's
// weight chart or test certificate before billing.

import 'dart:math' as math;

/// Density of steel, kg/m³.
const steelDensity = 7850.0;

/// One standard size with its mass per metre.
class SteelSection {
  const SteelSection(this.designation, this.kgPerM, [this.dims = '']);

  final String designation;
  final double kgPerM;

  /// Main dimensions in mm, e.g. "h 200 × b 100 × tw 5.7 × tf 10.8".
  final String dims;
}

/// How the weight of a family is found.
enum SteelShape {
  /// Pick a size from the IS table.
  table,

  /// Plate or flat: width × thickness.
  flat,

  /// Round bar: diameter.
  round,

  /// Square bar: side.
  square,

  /// Circular hollow section / pipe: outside diameter × thickness.
  chs,

  /// Square or rectangular hollow section: B × H × t.
  rhs,
}

/// A size the user types in for a calculated family.
class SteelDim {
  const SteelDim(this.key, this.label);
  final String key;
  final String label;
}

class SteelFamily {
  const SteelFamily({
    required this.code,
    required this.name,
    required this.standard,
    this.shape = SteelShape.table,
    this.sections = const [],
  });

  final String code;
  final String name;
  final String standard;
  final SteelShape shape;

  /// Standard sizes. For calculated families these are common sizes whose
  /// weight is worked out from the dimensions.
  final List<SteelSection> sections;

  String get title => '$code – $name';

  /// The sizes to type in when no table size is picked.
  List<SteelDim> get inputs => switch (shape) {
        SteelShape.table => const [],
        SteelShape.flat => const [SteelDim('w', 'Width (mm)'), SteelDim('t', 'Thickness (mm)')],
        SteelShape.round => const [SteelDim('d', 'Diameter (mm)')],
        SteelShape.square => const [SteelDim('a', 'Side (mm)')],
        SteelShape.chs => const [SteelDim('od', 'Outside dia (mm)'), SteelDim('t', 'Thickness (mm)')],
        SteelShape.rhs => const [
            SteelDim('b', 'Width B (mm)'),
            SteelDim('h', 'Depth H (mm)'),
            SteelDim('t', 'Thickness (mm)'),
          ],
      };

  /// kg per metre for typed-in sizes; 0 when a size is missing or invalid.
  double kgPerMFor(Map<String, double> d) {
    double v(String k) => d[k] ?? 0;
    return switch (shape) {
      SteelShape.table => 0,
      SteelShape.flat => SteelCalc.flat(v('w'), v('t')),
      SteelShape.round => SteelCalc.round(v('d')),
      SteelShape.square => SteelCalc.square(v('a')),
      SteelShape.chs => SteelCalc.chs(v('od'), v('t')),
      SteelShape.rhs => SteelCalc.rhs(v('b'), v('h'), v('t')),
    };
  }
}

/// Unit-weight formulas (sizes in mm, result in kg/m).
abstract final class SteelCalc {
  static const _k = steelDensity / 1e6; // kg per m per mm²  (0.00785)

  /// Plate / flat: w × t × 0.00785.
  static double flat(double w, double t) => w > 0 && t > 0 ? w * t * _k : 0;

  /// Plate weight per square metre: t × 7.85.
  static double plateKgPerSqm(double t) => t > 0 ? t * steelDensity / 1000 : 0;

  /// Round bar: d² × 0.006165 (= d²/162.2).
  static double round(double d) => d > 0 ? math.pi / 4 * d * d * _k : 0;

  /// Square bar: a² × 0.00785.
  static double square(double a) => a > 0 ? a * a * _k : 0;

  /// Pipe / CHS: (OD − t) × t × 0.02466.
  static double chs(double od, double t) => od > 0 && t > 0 && od > 2 * t ? math.pi * (od - t) * t * _k : 0;

  /// SHS / RHS with corner radii r(out) = 2t, r(in) = t as in IS 4923.
  static double rhs(double b, double h, double t) {
    if (b <= 2 * t || h <= 2 * t || t <= 0) return 0;
    final area = 2 * t * (b + h - 2 * t) - (4 - math.pi) * 3 * t * t;
    return area * _k;
  }
}

List<SteelSection> _pipes(List<(String, num, num)> rows) => [
      for (final (nb, od, t) in [for (final (n, o, w) in rows) (n, o.toDouble(), w.toDouble())])
        SteelSection('$nb NB (${_mm(od)} × ${_mm(t)})', _r2(SteelCalc.chs(od, t)), 'OD ${_mm(od)} × t ${_mm(t)}'),
    ];

List<SteelSection> _rhs(String prefix, List<(num, num, num)> rows) => [
      for (final (b, h, t) in [for (final (x, y, z) in rows) (x.toDouble(), y.toDouble(), z.toDouble())])
        SteelSection('$prefix ${_mm(b)}×${_mm(h)}×${_mm(t)}', _r2(SteelCalc.rhs(b, h, t)), 'B ${_mm(b)} × H ${_mm(h)} × t ${_mm(t)}'),
    ];

List<SteelSection> _flats(String prefix, List<(num, num)> rows) => [
      for (final (w, t) in [for (final (x, y) in rows) (x.toDouble(), y.toDouble())])
        SteelSection('$prefix ${_mm(w)}×${_mm(t)}', _r2(SteelCalc.flat(w, t)), 'w ${_mm(w)} × t ${_mm(t)}'),
    ];

List<SteelSection> _bars(String prefix, List<num> sizes, double Function(double) f) => [
      for (final s in sizes) SteelSection('$prefix ${_mm(s.toDouble())}', _r3(f(s.toDouble()))),
    ];

double _r2(double v) => (v * 100).roundToDouble() / 100;
double _r3(double v) => (v * 1000).roundToDouble() / 1000;
String _mm(double v) => v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toString();

abstract final class SteelTables {
  // ---------------------------------------------------------------- beams
  static const ismb = SteelFamily(
    code: 'ISMB',
    name: 'Medium weight beam',
    standard: 'IS 808 / SP 6(1)',
    sections: [
      SteelSection('ISMB 100', 11.5, 'h 100 × b 75 × tw 4.0 × tf 7.2'),
      SteelSection('ISMB 125', 13.0, 'h 125 × b 75 × tw 4.4 × tf 7.6'),
      SteelSection('ISMB 150', 14.9, 'h 150 × b 80 × tw 4.8 × tf 7.6'),
      SteelSection('ISMB 175', 19.3, 'h 175 × b 90 × tw 5.5 × tf 8.6'),
      SteelSection('ISMB 200', 25.4, 'h 200 × b 100 × tw 5.7 × tf 10.8'),
      SteelSection('ISMB 225', 31.2, 'h 225 × b 110 × tw 6.5 × tf 11.8'),
      SteelSection('ISMB 250', 37.3, 'h 250 × b 125 × tw 6.9 × tf 12.5'),
      SteelSection('ISMB 300', 44.2, 'h 300 × b 140 × tw 7.5 × tf 12.4'),
      SteelSection('ISMB 350', 52.4, 'h 350 × b 140 × tw 8.1 × tf 14.2'),
      SteelSection('ISMB 400', 61.6, 'h 400 × b 140 × tw 8.9 × tf 16.0'),
      SteelSection('ISMB 450', 72.4, 'h 450 × b 150 × tw 9.4 × tf 17.4'),
      SteelSection('ISMB 500', 86.9, 'h 500 × b 180 × tw 10.2 × tf 17.2'),
      SteelSection('ISMB 550', 103.7, 'h 550 × b 190 × tw 11.2 × tf 19.3'),
      SteelSection('ISMB 600', 122.6, 'h 600 × b 210 × tw 12.0 × tf 20.8'),
    ],
  );

  static const islb = SteelFamily(
    code: 'ISLB',
    name: 'Light weight beam',
    standard: 'IS 808 / SP 6(1)',
    sections: [
      SteelSection('ISLB 75', 6.1),
      SteelSection('ISLB 100', 8.0),
      SteelSection('ISLB 125', 11.9),
      SteelSection('ISLB 150', 14.2),
      SteelSection('ISLB 175', 16.7),
      SteelSection('ISLB 200', 19.8),
      SteelSection('ISLB 225', 23.5),
      SteelSection('ISLB 250', 27.9),
      SteelSection('ISLB 275', 33.0),
      SteelSection('ISLB 300', 37.7),
      SteelSection('ISLB 325', 43.1),
      SteelSection('ISLB 350', 49.5),
      SteelSection('ISLB 400', 56.9),
      SteelSection('ISLB 450', 65.3),
      SteelSection('ISLB 500', 75.0),
      SteelSection('ISLB 550', 86.3),
      SteelSection('ISLB 600', 99.5),
    ],
  );

  static const isjb = SteelFamily(
    code: 'ISJB',
    name: 'Junior beam',
    standard: 'IS 808 / SP 6(1)',
    sections: [
      SteelSection('ISJB 150', 7.1),
      SteelSection('ISJB 175', 8.1),
      SteelSection('ISJB 200', 9.9),
      SteelSection('ISJB 225', 12.8),
    ],
  );

  static const iswb = SteelFamily(
    code: 'ISWB',
    name: 'Wide flange beam',
    standard: 'IS 808 / SP 6(1)',
    sections: [
      SteelSection('ISWB 150', 17.0),
      SteelSection('ISWB 175', 22.1),
      SteelSection('ISWB 200', 28.8),
      SteelSection('ISWB 225', 33.9),
      SteelSection('ISWB 250', 40.9),
      SteelSection('ISWB 300', 48.1),
      SteelSection('ISWB 350', 56.9),
      SteelSection('ISWB 400', 66.7),
      SteelSection('ISWB 450', 79.4),
      SteelSection('ISWB 500', 95.2),
      SteelSection('ISWB 550', 112.5),
      SteelSection('ISWB 600 @ 133.7', 133.7),
      SteelSection('ISWB 600 @ 145.1', 145.1),
    ],
  );

  static const ishb = SteelFamily(
    code: 'ISHB',
    name: 'Column / heavy beam',
    standard: 'IS 808 / SP 6(1)',
    sections: [
      SteelSection('ISHB 150 @ 27.1', 27.1),
      SteelSection('ISHB 150 @ 30.6', 30.6),
      SteelSection('ISHB 150 @ 34.6', 34.6),
      SteelSection('ISHB 200 @ 37.3', 37.3),
      SteelSection('ISHB 200 @ 40.0', 40.0),
      SteelSection('ISHB 225 @ 43.1', 43.1),
      SteelSection('ISHB 225 @ 46.8', 46.8),
      SteelSection('ISHB 250 @ 51.0', 51.0),
      SteelSection('ISHB 250 @ 54.7', 54.7),
      SteelSection('ISHB 300 @ 58.8', 58.8),
      SteelSection('ISHB 300 @ 63.0', 63.0),
      SteelSection('ISHB 350 @ 67.4', 67.4),
      SteelSection('ISHB 350 @ 72.4', 72.4),
      SteelSection('ISHB 400 @ 77.4', 77.4),
      SteelSection('ISHB 400 @ 82.2', 82.2),
      SteelSection('ISHB 450 @ 87.2', 87.2),
      SteelSection('ISHB 450 @ 92.5', 92.5),
    ],
  );

  // ------------------------------------------------------------- channels
  static const ismc = SteelFamily(
    code: 'ISMC',
    name: 'Medium weight channel',
    standard: 'IS 808 / SP 6(1)',
    sections: [
      SteelSection('ISMC 75', 6.8, 'h 75 × b 40 × tw 4.4 × tf 7.3'),
      SteelSection('ISMC 100', 9.2, 'h 100 × b 50 × tw 4.7 × tf 7.5'),
      SteelSection('ISMC 125', 12.7, 'h 125 × b 65 × tw 5.0 × tf 8.1'),
      SteelSection('ISMC 150', 16.4, 'h 150 × b 75 × tw 5.4 × tf 9.0'),
      SteelSection('ISMC 175', 19.1, 'h 175 × b 75 × tw 5.7 × tf 10.2'),
      SteelSection('ISMC 200', 22.1, 'h 200 × b 75 × tw 6.1 × tf 11.4'),
      SteelSection('ISMC 225', 25.9, 'h 225 × b 80 × tw 6.4 × tf 12.4'),
      SteelSection('ISMC 250', 30.4, 'h 250 × b 80 × tw 7.1 × tf 14.1'),
      SteelSection('ISMC 300', 35.8, 'h 300 × b 90 × tw 7.6 × tf 13.6'),
      SteelSection('ISMC 350', 42.1, 'h 350 × b 100 × tw 8.1 × tf 13.5'),
      SteelSection('ISMC 400', 49.4, 'h 400 × b 100 × tw 8.6 × tf 15.3'),
    ],
  );

  static const islc = SteelFamily(
    code: 'ISLC',
    name: 'Light weight channel',
    standard: 'IS 808 / SP 6(1)',
    sections: [
      SteelSection('ISLC 75', 5.7),
      SteelSection('ISLC 100', 7.9),
      SteelSection('ISLC 125', 10.7),
      SteelSection('ISLC 150', 14.4),
      SteelSection('ISLC 175', 17.6),
      SteelSection('ISLC 200', 20.6),
      SteelSection('ISLC 225', 24.0),
      SteelSection('ISLC 250', 28.0),
      SteelSection('ISLC 300', 33.1),
      SteelSection('ISLC 350', 38.8),
      SteelSection('ISLC 400', 45.7),
    ],
  );

  static const isjc = SteelFamily(
    code: 'ISJC',
    name: 'Junior channel',
    standard: 'IS 808 / SP 6(1)',
    sections: [
      SteelSection('ISJC 100', 5.8),
      SteelSection('ISJC 125', 7.9),
      SteelSection('ISJC 150', 9.9),
      SteelSection('ISJC 175', 11.2),
      SteelSection('ISJC 200', 13.9),
    ],
  );

  // --------------------------------------------------------------- angles
  static const isaEqual = SteelFamily(
    code: 'ISA',
    name: 'Equal angle',
    standard: 'IS 808 / SP 6(1)',
    sections: [
      SteelSection('ISA 20×20×3', 0.9),
      SteelSection('ISA 20×20×4', 1.1),
      SteelSection('ISA 25×25×3', 1.1),
      SteelSection('ISA 25×25×4', 1.4),
      SteelSection('ISA 25×25×5', 1.8),
      SteelSection('ISA 30×30×3', 1.4),
      SteelSection('ISA 30×30×4', 1.8),
      SteelSection('ISA 30×30×5', 2.2),
      SteelSection('ISA 35×35×3', 1.6),
      SteelSection('ISA 35×35×4', 2.1),
      SteelSection('ISA 35×35×5', 2.6),
      SteelSection('ISA 35×35×6', 3.0),
      SteelSection('ISA 40×40×3', 1.8),
      SteelSection('ISA 40×40×4', 2.4),
      SteelSection('ISA 40×40×5', 3.0),
      SteelSection('ISA 40×40×6', 3.5),
      SteelSection('ISA 45×45×3', 2.1),
      SteelSection('ISA 45×45×4', 2.7),
      SteelSection('ISA 45×45×5', 3.4),
      SteelSection('ISA 45×45×6', 4.0),
      SteelSection('ISA 50×50×3', 2.3),
      SteelSection('ISA 50×50×4', 3.0),
      SteelSection('ISA 50×50×5', 3.8),
      SteelSection('ISA 50×50×6', 4.5),
      SteelSection('ISA 55×55×5', 4.1),
      SteelSection('ISA 55×55×6', 4.9),
      SteelSection('ISA 55×55×8', 6.4),
      SteelSection('ISA 55×55×10', 7.9),
      SteelSection('ISA 60×60×5', 4.5),
      SteelSection('ISA 60×60×6', 5.4),
      SteelSection('ISA 60×60×8', 7.0),
      SteelSection('ISA 60×60×10', 8.6),
      SteelSection('ISA 65×65×5', 4.9),
      SteelSection('ISA 65×65×6', 5.8),
      SteelSection('ISA 65×65×8', 7.7),
      SteelSection('ISA 65×65×10', 9.4),
      SteelSection('ISA 70×70×5', 5.3),
      SteelSection('ISA 70×70×6', 6.3),
      SteelSection('ISA 70×70×8', 8.3),
      SteelSection('ISA 70×70×10', 10.2),
      SteelSection('ISA 75×75×5', 5.7),
      SteelSection('ISA 75×75×6', 6.8),
      SteelSection('ISA 75×75×8', 8.9),
      SteelSection('ISA 75×75×10', 11.0),
      SteelSection('ISA 80×80×6', 7.3),
      SteelSection('ISA 80×80×8', 9.6),
      SteelSection('ISA 80×80×10', 11.8),
      SteelSection('ISA 80×80×12', 14.0),
      SteelSection('ISA 90×90×6', 8.2),
      SteelSection('ISA 90×90×8', 10.8),
      SteelSection('ISA 90×90×10', 13.4),
      SteelSection('ISA 90×90×12', 15.8),
      SteelSection('ISA 100×100×6', 9.2),
      SteelSection('ISA 100×100×8', 12.1),
      SteelSection('ISA 100×100×10', 14.9),
      SteelSection('ISA 100×100×12', 17.7),
      SteelSection('ISA 110×110×8', 13.4),
      SteelSection('ISA 110×110×10', 16.6),
      SteelSection('ISA 110×110×12', 19.7),
      SteelSection('ISA 110×110×15', 24.4),
      SteelSection('ISA 130×130×8', 15.9),
      SteelSection('ISA 130×130×10', 19.7),
      SteelSection('ISA 130×130×12', 23.5),
      SteelSection('ISA 130×130×15', 29.1),
      SteelSection('ISA 150×150×10', 22.9),
      SteelSection('ISA 150×150×12', 27.3),
      SteelSection('ISA 150×150×15', 33.8),
      SteelSection('ISA 150×150×18', 40.1),
      SteelSection('ISA 200×200×12', 36.9),
      SteelSection('ISA 200×200×15', 45.6),
      SteelSection('ISA 200×200×18', 54.2),
      SteelSection('ISA 200×200×25', 73.9),
    ],
  );

  static const isaUnequal = SteelFamily(
    code: 'ISA (U)',
    name: 'Unequal angle',
    standard: 'IS 808 / SP 6(1)',
    sections: [
      SteelSection('ISA 30×20×3', 1.1),
      SteelSection('ISA 30×20×4', 1.4),
      SteelSection('ISA 30×20×5', 1.8),
      SteelSection('ISA 40×25×3', 1.5),
      SteelSection('ISA 40×25×4', 1.9),
      SteelSection('ISA 40×25×5', 2.4),
      SteelSection('ISA 45×30×3', 1.7),
      SteelSection('ISA 45×30×4', 2.2),
      SteelSection('ISA 45×30×5', 2.8),
      SteelSection('ISA 50×30×3', 1.8),
      SteelSection('ISA 50×30×4', 2.4),
      SteelSection('ISA 50×30×5', 2.9),
      SteelSection('ISA 60×40×5', 3.7),
      SteelSection('ISA 60×40×6', 4.4),
      SteelSection('ISA 60×40×8', 5.8),
      SteelSection('ISA 65×45×5', 4.1),
      SteelSection('ISA 65×45×6', 4.9),
      SteelSection('ISA 65×45×8', 6.4),
      SteelSection('ISA 70×45×5', 4.3),
      SteelSection('ISA 70×45×6', 5.2),
      SteelSection('ISA 70×45×8', 6.8),
      SteelSection('ISA 75×50×5', 4.7),
      SteelSection('ISA 75×50×6', 5.6),
      SteelSection('ISA 75×50×8', 7.4),
      SteelSection('ISA 80×50×5', 4.9),
      SteelSection('ISA 80×50×6', 5.9),
      SteelSection('ISA 80×50×8', 7.7),
      SteelSection('ISA 90×60×6', 6.8),
      SteelSection('ISA 90×60×8', 8.9),
      SteelSection('ISA 90×60×10', 11.0),
      SteelSection('ISA 100×65×6', 7.5),
      SteelSection('ISA 100×65×8', 9.9),
      SteelSection('ISA 100×65×10', 12.2),
      SteelSection('ISA 100×75×6', 8.0),
      SteelSection('ISA 100×75×8', 10.6),
      SteelSection('ISA 100×75×10', 13.0),
      SteelSection('ISA 125×75×6', 9.2),
      SteelSection('ISA 125×75×8', 12.1),
      SteelSection('ISA 125×75×10', 14.9),
      SteelSection('ISA 150×75×8', 13.7),
      SteelSection('ISA 150×75×10', 17.0),
      SteelSection('ISA 150×75×12', 20.2),
      SteelSection('ISA 200×100×10', 22.9),
      SteelSection('ISA 200×100×12', 27.3),
      SteelSection('ISA 200×100×15', 33.7),
    ],
  );

  // ---------------------------------------------------- UB / UC (BS 4-1)
  static final ub = SteelFamily(
    code: 'UB',
    name: 'Universal beam',
    standard: 'BS 4-1 (mass in the designation)',
    sections: _universal('UB', const [
      '127×76×13', '152×89×16', '178×102×19', '203×102×23', '203×133×25', '203×133×30',
      '254×102×22', '254×102×25', '254×102×28', '254×146×31', '254×146×37', '254×146×43',
      '305×102×25', '305×102×28', '305×102×33', '305×127×37', '305×127×42', '305×127×48',
      '305×165×40', '305×165×46', '305×165×54', '356×171×45', '356×171×51', '356×171×57',
      '356×171×67', '406×178×54', '406×178×60', '406×178×67', '406×178×74', '457×191×67',
      '457×191×74', '457×191×82', '457×191×89', '457×191×98', '533×210×82', '533×210×92',
      '533×210×101', '533×210×109', '533×210×122', '610×229×101', '610×229×113', '610×229×125',
      '610×229×140', '686×254×125', '686×254×140', '686×254×152', '686×254×170', '762×267×134',
      '762×267×147', '762×267×173', '762×267×197', '838×292×176', '838×292×194', '838×292×226',
      '914×305×201', '914×305×224', '914×305×253', '914×305×289',
    ]),
  );

  static final uc = SteelFamily(
    code: 'UC',
    name: 'Universal column',
    standard: 'BS 4-1 (mass in the designation)',
    sections: _universal('UC', const [
      '152×152×23', '152×152×30', '152×152×37', '203×203×46', '203×203×52', '203×203×60',
      '203×203×71', '203×203×86', '254×254×73', '254×254×89', '254×254×107', '254×254×132',
      '254×254×167', '305×305×97', '305×305×118', '305×305×137', '305×305×158', '305×305×198',
      '305×305×240', '305×305×283', '356×368×129', '356×368×153', '356×368×177', '356×368×202',
    ]),
  );

  static List<SteelSection> _universal(String prefix, List<String> sizes) => [
        for (final s in sizes) SteelSection('$prefix $s', double.parse(s.split('×').last)),
      ];

  // ------------------------------------------------ plates, flats, bars
  static final plate = SteelFamily(
    code: 'Plate',
    name: 'Steel plate (width × thickness)',
    standard: 'IS 2062 / IS 1730',
    shape: SteelShape.flat,
    sections: _flats('PL', const [
      (1000, 5), (1000, 6), (1000, 8), (1000, 10), (1000, 12), (1000, 16), (1000, 20),
      (1000, 25), (1000, 32), (1000, 40), (1250, 6), (1250, 8), (1500, 10), (1500, 12),
      (2000, 16), (2000, 20), (2500, 25),
    ]),
  );

  static final flat = SteelFamily(
    code: 'Flat',
    name: 'Flat bar',
    standard: 'IS 2062 / IS 1731',
    shape: SteelShape.flat,
    sections: _flats('FLT', const [
      (20, 3), (20, 5), (25, 3), (25, 5), (25, 6), (32, 5), (32, 6), (40, 5), (40, 6),
      (40, 8), (40, 10), (50, 5), (50, 6), (50, 8), (50, 10), (50, 12), (65, 6), (65, 8),
      (65, 10), (75, 6), (75, 8), (75, 10), (75, 12), (100, 6), (100, 8), (100, 10),
      (100, 12), (100, 16), (125, 10), (125, 12), (150, 10), (150, 12), (150, 16),
      (200, 10), (200, 12), (200, 16), (200, 20),
    ]),
  );

  static final roundBar = SteelFamily(
    code: 'Round',
    name: 'Round bar (MS)',
    standard: 'IS 2062 / IS 1732',
    shape: SteelShape.round,
    sections: _bars('RD', const [6, 8, 10, 12, 16, 20, 22, 25, 28, 32, 36, 40, 45, 50, 56, 63, 75, 80, 90, 100], SteelCalc.round),
  );

  static final squareBar = SteelFamily(
    code: 'Square',
    name: 'Square bar',
    standard: 'IS 2062 / IS 1732',
    shape: SteelShape.square,
    sections: _bars('SQ', const [10, 12, 16, 20, 25, 32, 40, 50], SteelCalc.square),
  );

  /// Nominal mass of reinforcement bars, IS 1786 Table 1.
  static const tmt = SteelFamily(
    code: 'TMT',
    name: 'TMT / HYSD rebar',
    standard: 'IS 1786 (nominal mass)',
    shape: SteelShape.round,
    sections: [
      SteelSection('TMT 6', 0.222),
      SteelSection('TMT 8', 0.395),
      SteelSection('TMT 10', 0.617),
      SteelSection('TMT 12', 0.888),
      SteelSection('TMT 16', 1.58),
      SteelSection('TMT 20', 2.47),
      SteelSection('TMT 25', 3.85),
      SteelSection('TMT 28', 4.83),
      SteelSection('TMT 32', 6.31),
      SteelSection('TMT 36', 7.99),
      SteelSection('TMT 40', 9.86),
    ],
  );

  // ------------------------------------------------- pipes and hollows
  static final pipeLight = SteelFamily(
    code: 'Pipe L',
    name: 'MS pipe – Light (A)',
    standard: 'IS 1239 (Pt 1)',
    shape: SteelShape.chs,
    sections: _pipes(const [
      ('15', 21.3, 2.0), ('20', 26.9, 2.3), ('25', 33.7, 2.6), ('32', 42.4, 2.6),
      ('40', 48.3, 2.9), ('50', 60.3, 2.9), ('65', 76.1, 3.2), ('80', 88.9, 3.2),
      ('100', 114.3, 3.6),
    ]),
  );

  static final pipeMedium = SteelFamily(
    code: 'Pipe M',
    name: 'MS pipe – Medium (B)',
    standard: 'IS 1239 (Pt 1)',
    shape: SteelShape.chs,
    sections: _pipes(const [
      ('15', 21.3, 2.6), ('20', 26.9, 2.6), ('25', 33.7, 3.2), ('32', 42.4, 3.2),
      ('40', 48.3, 3.2), ('50', 60.3, 3.6), ('65', 76.1, 3.6), ('80', 88.9, 4.0),
      ('100', 114.3, 4.5), ('125', 139.7, 4.8), ('150', 165.1, 4.8),
    ]),
  );

  static final pipeHeavy = SteelFamily(
    code: 'Pipe H',
    name: 'MS pipe – Heavy (C)',
    standard: 'IS 1239 (Pt 1)',
    shape: SteelShape.chs,
    sections: _pipes(const [
      ('15', 21.3, 3.2), ('20', 26.9, 3.2), ('25', 33.7, 4.0), ('32', 42.4, 4.0),
      ('40', 48.3, 4.0), ('50', 60.3, 4.5), ('65', 76.1, 4.5), ('80', 88.9, 4.8),
      ('100', 114.3, 5.4), ('125', 139.7, 5.4), ('150', 165.1, 5.4),
    ]),
  );

  static final shs = SteelFamily(
    code: 'SHS',
    name: 'Square hollow section',
    standard: 'IS 4923',
    shape: SteelShape.rhs,
    sections: _rhs('SHS', const [
      (25, 25, 2.0), (25, 25, 2.6), (32, 32, 2.6), (40, 40, 2.6), (40, 40, 3.2),
      (50, 50, 2.9), (50, 50, 3.6), (60, 60, 3.2), (60, 60, 4.0), (72, 72, 3.2),
      (72, 72, 4.8), (91.5, 91.5, 3.6), (91.5, 91.5, 4.5), (100, 100, 4.0),
      (100, 100, 5.0), (113.5, 113.5, 4.8), (132, 132, 4.8), (132, 132, 6.0),
      (150, 150, 6.0), (180, 180, 8.0),
    ]),
  );

  static final rhsFamily = SteelFamily(
    code: 'RHS',
    name: 'Rectangular hollow section',
    standard: 'IS 4923',
    shape: SteelShape.rhs,
    sections: _rhs('RHS', const [
      (50, 25, 2.6), (60, 40, 2.9), (66, 33, 2.9), (80, 40, 3.2), (96, 48, 3.2),
      (96, 48, 4.8), (100, 50, 4.0), (122, 61, 3.6), (122, 61, 5.4), (145, 82, 4.8),
      (172, 92, 4.8), (200, 100, 6.0),
    ]),
  );

  /// Every family, in the order shown in the app.
  static final List<SteelFamily> families = [
    plate, flat, roundBar, squareBar, tmt,
    isaEqual, isaUnequal,
    ismc, islc, isjc,
    ismb, islb, isjb, iswb, ishb,
    ub, uc,
    pipeLight, pipeMedium, pipeHeavy, shs, rhsFamily,
  ];

  static SteelFamily? byCode(String code) {
    for (final f in families) {
      if (f.code == code) return f;
    }
    return null;
  }
}
