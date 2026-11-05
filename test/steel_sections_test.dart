import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/date_symbol_data_local.dart';
import 'package:site_dpr/data/steel_sections.dart';
import 'package:site_dpr/screens/steel_weight_screen.dart';

void main() {
  setUpAll(() => initializeDateFormatting('en_IN'));

  test('unit weight formulas (7850 kg/m³)', () {
    expect(SteelCalc.flat(50, 6), closeTo(2.355, 1e-9));
    expect(SteelCalc.plateKgPerSqm(10), closeTo(78.5, 1e-9));
    expect(SteelCalc.round(16), closeTo(1.578, 0.001)); // d²/162.2
    expect(SteelCalc.square(20), closeTo(3.14, 1e-9));
    expect(SteelCalc.chs(21.3, 2.6), closeTo(1.20, 0.01)); // 15 NB medium
    expect(SteelCalc.rhs(40, 40, 2.6), closeTo(2.92, 0.02));
    expect(SteelCalc.chs(10, 6), 0); // invalid size
  });

  test('family codes are unique and every family has sizes', () {
    final codes = SteelTables.families.map((f) => f.code).toList();
    expect(codes.toSet().length, codes.length);
    for (final f in SteelTables.families) {
      expect(f.sections, isNotEmpty, reason: f.code);
      for (final s in f.sections) {
        expect(s.kgPerM, greaterThan(0), reason: s.designation);
      }
    }
  });

  test('angle table agrees with t(a + b − t) × 0.00785', () {
    for (final f in [SteelTables.isaEqual, SteelTables.isaUnequal]) {
      for (final s in f.sections) {
        final p = s.designation.substring(4).split('×').map(double.parse).toList();
        final calc = p[2] * (p[0] + p[1] - p[2]) * 0.00785;
        expect(s.kgPerM / calc, inInclusiveRange(0.96, 1.10), reason: s.designation);
      }
    }
  });

  test('TMT nominal mass matches d²/162', () {
    for (final s in SteelTables.tmt.sections) {
      final d = double.parse(s.designation.split(' ').last);
      expect(s.kgPerM / SteelCalc.round(d), closeTo(1, 0.01), reason: s.designation);
    }
  });

  test('UB / UC mass is taken from the designation', () {
    expect(SteelTables.ub.sections.first.kgPerM, 13);
    expect(SteelTables.uc.sections.last.kgPerM, 202);
  });

  testWidgets('calculator adds to the take-off', (tester) async {
    await tester.pumpWidget(const MaterialApp(home: SteelWeightScreen()));
    // Plate 1000 × 5 mm is the first size: 39.25 kg/m.
    expect(find.text('Unit weight: 39.25 kg/m'), findsOneWidget);
    final add = find.text('Add to take-off');
    await tester.ensureVisible(add);
    await tester.tap(add);
    await tester.pumpAndSettle();
    expect(find.textContaining('Total: 39.25 kg'), findsOneWidget);

    await tester.tap(find.text('IS steel table'));
    await tester.pumpAndSettle();
    expect(find.text('ISMB 200'), findsOneWidget);
    expect(find.text('25.4 kg/m'), findsOneWidget);
    await tester.tap(find.text('ISMB 200'));
    await tester.pumpAndSettle();
    expect(find.text('Unit weight: 25.4 kg/m'), findsOneWidget);
  });
}
