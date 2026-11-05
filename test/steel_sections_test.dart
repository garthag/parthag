import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/date_symbol_data_local.dart';
import 'package:site_dpr/data/local_store.dart';
import 'package:site_dpr/data/sample_data.dart';
import 'package:site_dpr/data/steel_sections.dart';
import 'package:site_dpr/models/models.dart';
import 'package:site_dpr/models/steel_takeoff.dart';
import 'package:site_dpr/services/steel_text.dart';
import 'package:site_dpr/state/app_state.dart';
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

  test('search: words, compact text, x/× and IS code', () {
    expect(SteelLibrary.search(query: 'ismb300').single.designation, 'ISMB 300');
    expect(SteelLibrary.search(query: 'ISMB 300').first.designation, 'ISMB 300');
    final angles = SteelLibrary.search(query: '50x50');
    expect(angles, isNotEmpty);
    expect(angles.map((r) => r.designation), contains('ISA 50×50×6'));
    expect(angles.every((r) => r.designation.contains('50×50')), isTrue);
    expect(SteelLibrary.search(query: 'IS 1239').every((r) => r.family.standard.contains('1239')), isTrue);
    expect(SteelLibrary.search(query: 'tmt 12').single.kgPerM, 0.888);
  });

  test('filter by category, family and kg/m range; sort', () {
    final channels = SteelLibrary.search(category: SteelCategory.channels);
    expect(channels.map((r) => r.family.code).toSet(), {'ISMC', 'ISLC', 'ISJC'});
    final ismc = SteelLibrary.search(familyCode: 'ISMC', minKgPerM: 10, maxKgPerM: 20);
    expect(ismc.map((r) => r.designation), ['ISMC 125', 'ISMC 150', 'ISMC 175']);
    final heavy = SteelLibrary.search(category: SteelCategory.beamsColumns, sort: SteelSort.weightDesc);
    for (var i = 1; i < heavy.length; i++) {
      expect(heavy[i - 1].kgPerM, greaterThanOrEqualTo(heavy[i].kgPerM));
    }
    final byName = SteelLibrary.search(familyCode: 'ISA', sort: SteelSort.name);
    expect(byName.first.designation, 'ISA 20×20×3');
    expect(byName.last.designation, 'ISA 200×200×25');
  });

  test('hierarchy grouping keeps category → family → size order', () {
    final g = SteelLibrary.group(SteelLibrary.all);
    expect(g.keys.toList(), SteelCategory.values);
    expect(g[SteelCategory.beamsColumns]!.keys.first.code, 'ISMB');
    expect(SteelLibrary.all.length, greaterThan(350));
  });

  test('relations: neighbouring sizes and equal-weight alternatives', () {
    final r = SteelLibrary.find('ISMB', 'ISMB 300')!;
    final n = SteelLibrary.neighbours(r);
    expect(n.smaller!.designation, 'ISMB 250');
    expect(n.larger!.designation, 'ISMB 350');
    final alts = SteelLibrary.alternatives(r);
    expect(alts, isNotEmpty);
    expect(alts, isNot(contains(r)));
    for (final a in alts) {
      expect(a.category, SteelCategory.beamsColumns);
      expect((a.kgPerM - 44.2).abs(), lessThanOrEqualTo(4.42 + 1e-9));
    }
    expect(SteelLibrary.neighbours(SteelLibrary.find('ISMB', 'ISMB 100')!).smaller, isNull);
  });

  test('take-off totals, groups and json round trip', () {
    final t = SteelTakeoff(projectId: 'p', title: 'Roof', wastagePercent: 5, items: [
      SteelTakeoffItem.fromRef(SteelLibrary.find('ISMC', 'ISMC 100')!, lengthM: 6, nos: 10), // 552 kg
      SteelTakeoffItem.fromRef(SteelLibrary.find('ISMB', 'ISMB 200')!, lengthM: 5, nos: 4), // 508 kg
      SteelTakeoffItem(familyCode: 'Custom', designation: 'Grating', kgPerM: 20, lengthM: 1, nos: 2), // 40 kg
    ]);
    expect(t.netKg, closeTo(1100, 1e-6));
    expect(t.grossKg, closeTo(1155, 1e-6));
    expect(t.groups.map((g) => g.familyCode), ['ISMC', 'ISMB', 'Custom']);
    expect(t.kgByCategory[SteelCategory.channels], closeTo(552, 1e-6));
    expect(t.items.first.ref!.family.standard, contains('IS 808'));
    final back = SteelTakeoff.fromJson(t.toJson());
    expect(back.toJson(), t.toJson());
  });

  test('posting a take-off links it to the DPR (work done + materials)', () async {
    final state = AppState(MemoryStore());
    await state.load();
    final p = Project(name: 'Shed');
    await state.saveProject(p);
    final r = await state.createReport(p.id, DateTime(2026, 11, 1));
    final t = SteelTakeoff(projectId: p.id, title: 'Portal frames', items: [
      SteelTakeoffItem.fromRef(SteelLibrary.find('ISMB', 'ISMB 300')!, lengthM: 10, nos: 10), // 4.42 MT
      SteelTakeoffItem.fromRef(SteelLibrary.find('ISA', 'ISA 50×50×6')!, lengthM: 10, nos: 10), // 0.45 MT
    ]);
    await state.saveTakeoff(t);

    await state.postTakeoffToReport(t, r, mt: 2.435, asWork: true, asMaterial: true);
    final work = r.activities.singleWhere((a) => a.description == 'Structural steel erection');
    expect(work.unit, 'MT');
    expect(work.todayQty, closeTo(2.435, 1e-9));
    expect(work.totalScope, closeTo(4.87, 1e-9));
    final ismb = r.materials.singleWhere((m) => m.name.startsWith('Steel – ISMB'));
    expect(ismb.unit, 'MT');
    expect(ismb.received, closeTo(2.21, 1e-9)); // half of 4.42
    expect(state.takeoffsPostedTo(r.id), [t]);

    // Posting again adds to the same lines.
    await state.postTakeoffToReport(t, r, mt: 2.435, asWork: true);
    expect(r.activities.where((a) => a.description == 'Structural steel erection').single.todayQty, closeTo(4.87, 1e-9));

    // Data survives a reload.
    final again = AppState(state.store);
    await again.load();
    expect(again.takeoffsFor(p.id).single.postings.length, 2);

    // Deleting the DPR removes the link; deleting the project removes take-offs.
    await state.deleteReport(r.id);
    expect(state.takeoff(t.id)!.postings, isEmpty);
    await state.deleteProject(p.id);
    expect(state.takeoffs, isEmpty);

    // Approved DPRs are locked.
    final p2 = Project(name: 'Locked');
    await state.saveProject(p2);
    final r2 = await state.createReport(p2.id, DateTime(2026, 11, 2));
    r2.status = ReportStatus.approved;
    await expectLater(state.postTakeoffToReport(t, r2, mt: 1), throwsStateError);
  });

  test('sample project includes a linked steel take-off', () async {
    final state = AppState(MemoryStore());
    await state.load();
    final p = await createSampleProject(state);
    final t = state.takeoffsFor(p.id).single;
    expect(t.items.every((i) => i.ref != null), isTrue);
    expect(t.netMt, greaterThan(1));
    expect(SteelText.takeoff(t, p), contains('Net weight'));
  });

  testWidgets('calculator adds to the list', (tester) async {
    await tester.pumpWidget(const MaterialApp(home: SteelWeightScreen()));
    // Plate 1000 × 5 mm is the first size: 39.25 kg/m.
    expect(find.text('Unit weight: 39.25 kg/m'), findsOneWidget);
    final add = find.text('Add to list');
    await tester.ensureVisible(add);
    await tester.tap(add);
    await tester.pumpAndSettle();
    final scrollable = find
        .descendant(of: find.byKey(const PageStorageKey('steel-calculator')), matching: find.byType(Scrollable))
        .first;
    final total = find.text('Total: 39.25 kg (0.039 MT)');
    await tester.scrollUntilVisible(total, 200, scrollable: scrollable);
    expect(total, findsOneWidget);
  });

  testWidgets('library search → detail sheet → use in calculator', (tester) async {
    await tester.pumpWidget(const MaterialApp(home: SteelWeightScreen()));
    await tester.tap(find.text('IS steel library'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextField, 'Search: ISMB 300, 50x50, TMT 12, IS 1239…'), 'ismb 200');
    await tester.pumpAndSettle();
    expect(find.text('1 sizes'), findsOneWidget);
    expect(find.text('25.4 kg/m'), findsOneWidget);
    await tester.tap(find.text('ISMB 200'));
    await tester.pumpAndSettle();
    expect(find.textContaining('Next size in ISMB'), findsOneWidget);
    final use = find.text('Use in calculator');
    await tester.ensureVisible(use);
    await tester.tap(use);
    await tester.pumpAndSettle();
    expect(find.text('Unit weight: 25.4 kg/m'), findsOneWidget);
  });
}
