import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/date_symbol_data_local.dart';
import 'package:site_dpr/data/local_store.dart';
import 'package:site_dpr/data/sample_data.dart';
import 'package:site_dpr/main.dart';
import 'package:site_dpr/models/drawing.dart';
import 'package:site_dpr/models/models.dart';
import 'package:site_dpr/services/cloud_sync.dart';
import 'package:site_dpr/services/drawing_parser.dart';
import 'package:site_dpr/services/drawing_register_export.dart';
import 'package:site_dpr/services/drawing_service.dart';
import 'package:site_dpr/services/media_service.dart';
import 'package:site_dpr/state/app_state.dart';

Uint8List zipOf(Map<String, List<int>> files) {
  final a = Archive();
  files.forEach((name, data) => a.add(ArchiveFile.bytes(name, data)));
  return Uint8List.fromList(ZipEncoder().encodeBytes(a));
}

void main() {
  setUpAll(() => initializeDateFormatting('en_IN'));

  group('parseDrawingPath', () {
    test('plant numbering with area code, discipline and revision', () {
      final d = parseDrawingPath('RF-100-ST-GA-0001_R2.pdf', defaultPlant: 'Refinery');
      expect(d.number, 'RF-100-ST-GA-0001');
      expect(d.revision, '2');
      expect(d.unit, 'Unit 100');
      expect(d.type, DrawingType.structural);
      expect(d.kind, 'General Arrangement');
      expect(d.plant, 'Refinery');
      expect(d.status, DrawingStatus.forConstruction);
    });

    test('plant and unit from folders, lettered revision', () {
      final d = parseDrawingPath('Plant-2/Unit 300/Piping/ISO-300-1023 Rev B.pdf');
      expect(d.plant, 'Plant 2');
      expect(d.unit, 'Unit 300');
      expect(d.type, DrawingType.piping);
      expect(d.revision, 'B');
      expect(d.number, 'ISO-300-1023');
      expect(d.status, DrawingStatus.forApproval);
    });

    test('U-prefixed unit, status word and title', () {
      final d = parseDrawingPath('U200-EL-SLD-001-R0 Main SLD (IFC).pdf');
      expect(d.unit, 'Unit 200');
      expect(d.type, DrawingType.electrical);
      expect(d.revision, '0');
      expect(d.status, DrawingStatus.forConstruction);
      expect(d.number, 'U200-EL-SLD-001');
      expect(d.title, 'Main SLD');
      expect(d.kind, 'Single Line Diagram');
    });

    test('type from numbered folder, no drawing number', () {
      final d = parseDrawingPath('02_Structural/Foundation Plan.dwg');
      expect(d.type, DrawingType.structural);
      expect(d.number, 'Foundation Plan');
      expect(d.revision, '0');
    });

    test('type from keywords such as P&ID', () {
      final d = parseDrawingPath('CDU-PID-0004 Rev 3 Crude Charge P&ID.pdf');
      expect(d.type, DrawingType.process);
      expect(d.revision, '3');
    });

    test('plain folder names become plant and unit', () {
      final d = parseDrawingPath('Refinery/CDU/Piping/ISO-1023_R1.pdf');
      expect(d.plant, 'Refinery');
      expect(d.unit, 'CDU');
      expect(d.type, DrawingType.piping);
      final e = parseDrawingPath('External Works/SR-EX-CV-DRN-001_R0.pdf', defaultPlant: 'Site');
      expect(e.plant, 'Site');
      expect(e.unit, 'External Works');
      expect(e.type, DrawingType.civil);
    });

    test('unclassified falls into Other', () {
      expect(parseDrawingPath('scan001.pdf').type, DrawingType.other);
    });

    test('junk files are ignored', () {
      expect(isDrawingFile('__MACOSX/._a.pdf'), isFalse);
      expect(isDrawingFile('pkg/Thumbs.db'), isFalse);
      expect(isDrawingFile('pkg/A-100-ST-001.pdf'), isTrue);
    });
  });

  test('revision order: letters before numbers', () {
    final revs = ['1', 'A', '0', 'C', '10', '2']..sort(compareRevisions);
    expect(revs, ['A', 'C', '0', '1', '2', '10']);
  });

  test('organise groups by plant, unit, type order and natural number order', () {
    Drawing d(String no, String rev, String unit, String type) =>
        Drawing(projectId: 'p', number: no, revision: rev, plant: 'P1', unit: unit, typeCode: type);
    final groups = organiseDrawings([
      d('X-10', '0', 'Unit 200', 'EL'),
      d('X-2', '0', 'Unit 100', 'PP'),
      d('X-1', '1', 'Unit 100', 'ST'),
      d('X-1', '0', 'Unit 100', 'ST'),
      d('X-10', '0', 'Unit 100', 'ST'),
      d('X-3', 'A', 'Unit 100', 'CV'),
    ]);
    expect(groups.single.units.map((u) => u.unit), ['Unit 100', 'Unit 200']);
    final u100 = groups.single.units.first;
    expect(u100.types.map((t) => t.$1.code), ['CV', 'ST', 'PP']);
    final st = u100.types[1].$2;
    expect(st.map((e) => e.current.number), ['X-1', 'X-10']);
    expect(st.first.current.revision, '1');
    expect(st.first.superseded.single.revision, '0');
  });

  test('import ZIP: unzip, nested zip, wrapper folder, duplicates, export', () async {
    final inner = zipOf({'Unit-300/ISO-300-1023 Rev B.pdf': [1, 2, 3]});
    final zip = zipOf({
      'Package/RF-100-ST-GA-0001_R0.pdf': [1],
      'Package/RF-100-ST-GA-0001_R1.pdf': [2],
      'Package/RF-100-PP-ISO-0007_R0.pdf': [3],
      'Package/Plant-2/U200-EL-SLD-001-R0.png': [4],
      'Package/Piping/isos.zip': inner,
      'Package/Thumbs.db': [0],
      '__MACOSX/Package/._x.pdf': [0],
    });
    final store = MemoryStore();
    final service = DrawingService(store);
    final r = await service.importZipBytes(zip, projectId: 'p', defaultPlant: 'Refinery');
    expect(r.errors, isEmpty);
    expect(r.added.length, 5);
    expect(r.junkSkipped, 2);

    final iso = r.added.firstWhere((d) => d.number == 'ISO-300-1023');
    expect(iso.unit, 'Unit 300');
    expect(iso.type, DrawingType.piping);
    expect(iso.plant, 'Refinery');
    expect(r.added.firstWhere((d) => d.number == 'U200-EL-SLD-001').plant, 'Plant 2');
    expect(await service.readBytes(r.added.first), isNotNull);

    final again = await service.importZipBytes(zip, projectId: 'p', defaultPlant: 'Refinery', existing: r.added);
    expect(again.added, isEmpty);
    expect(again.duplicates.length, 5);

    final groups = organiseDrawings(r.added);
    expect(groups.map((g) => g.plant), ['Plant 2', 'Refinery']);

    final project = Project(name: 'Refinery Expansion');
    final pdf = await DrawingRegisterExport.pdf(project, r.added);
    expect(pdf.length, greaterThan(1000));
    final csv = DrawingRegisterExport.csv(r.added);
    expect(csv, contains('RF-100-ST-GA-0001'));
    expect(csv, contains('Superseded'));
  });

  test('sample project ships with an organised drawing set', () async {
    final state = AppState(MemoryStore());
    await state.load();
    final p = await createSampleProject(state);
    final all = state.drawingsFor(p.id);
    expect(all.length, 13);
    final groups = organiseDrawings(all);
    expect(groups.single.plant, 'Sunrise Residency');
    expect(groups.single.units.map((u) => u.unit), ['Block A', 'Block B', 'External Works', '']);
    final blockA = groups.single.units.first;
    expect(blockA.types.map((t) => t.$1.code), ['ST', 'AR', 'EL', 'FP', 'PL']);
    final fdn = blockA.types.first.$2.firstWhere((e) => e.current.number == 'SR-A-ST-FDN-001');
    expect(fdn.current.number, 'SR-A-ST-FDN-001');
    expect(fdn.current.revision, '1');
    expect(fdn.superseded.single.revision, '0');
    expect(groups.single.units[2].types.single.$1, DrawingType.civil);
    expect(groups.single.units.last.types.single.$1, DrawingType.general);
  });

  test('drawings persist with the project and are removed with it', () async {
    final store = MemoryStore();
    final state = AppState(store);
    await state.load();
    final p = Project(name: 'X');
    await state.saveProject(p);
    await state.addDrawings([Drawing(projectId: p.id, number: 'A-1', revision: '0')]);
    final reloaded = AppState(store);
    await reloaded.load();
    expect(reloaded.drawingsFor(p.id).single.number, 'A-1');
    await reloaded.deleteProject(p.id);
    expect(reloaded.drawings, isEmpty);
  });

  testWidgets('drawing register tab shows plant, unit and type groups', (tester) async {
    final store = MemoryStore();
    final state = AppState(store);
    final service = DrawingService(store);
    await state.load();
    final p = Project(name: 'Refinery Expansion');
    await tester.runAsync(() async {
      await state.saveProject(p);
      final zip = zipOf({
        'RF-100-ST-GA-0001_R0.png': [1],
        'RF-100-ST-GA-0001_R1.png': [2],
        'RF-100-EL-SLD-0001_R0.png': [3],
      });
      final r = await service.importZipBytes(zip, projectId: p.id, defaultPlant: 'Plant A');
      await state.addDrawings(r.added);
    });
    await tester.binding.setSurfaceSize(const Size(420, 900));
    await tester.pumpWidget(
        SiteDprApp(state: state, cloud: CloudSync(), media: MediaService(store), drawings: service));
    await tester.tap(find.text('Refinery Expansion'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Drawings'));
    await tester.pumpAndSettle();

    expect(find.text('Import ZIP'), findsOneWidget);
    expect(find.text('Plant A'), findsOneWidget);
    expect(find.text('Unit 100'), findsOneWidget);
    expect(find.text('Structural'), findsOneWidget);
    expect(find.text('Electrical'), findsOneWidget);
    expect(find.text('RF-100-ST-GA-0001'), findsOneWidget);
    expect(find.text('R1'), findsOneWidget);

    await tester.tap(find.text('RF-100-ST-GA-0001'));
    await tester.pumpAndSettle();
    expect(find.textContaining('Rev 1 · IFC · ST'), findsOneWidget);
    expect(find.text('1 of 2  ·  General Arrangement – Structural'), findsOneWidget);

    // Double tap zooms in, double tap again resets.
    final viewer = find.byType(InteractiveViewer);
    Matrix4 matrix() => tester.widget<InteractiveViewer>(viewer).transformationController!.value;
    await tester.tap(viewer);
    await tester.pump(const Duration(milliseconds: 50));
    await tester.tap(viewer);
    await tester.pumpAndSettle();
    expect(matrix().getMaxScaleOnAxis(), closeTo(3, .01));
    await tester.tap(viewer);
    await tester.pump(const Duration(milliseconds: 50));
    await tester.tap(viewer);
    await tester.pumpAndSettle();
    expect(matrix().getMaxScaleOnAxis(), closeTo(1, .01));

    // Older revision shows the superseded warning and links to the latest.
    final old = state.drawings.firstWhere((d) => d.number == 'RF-100-ST-GA-0001' && d.revision == '0');
    await tester.tap(find.byTooltip('Details & revisions'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Superseded'));
    await tester.pumpAndSettle();
    expect(find.textContaining('SUPERSEDED – Rev 0'), findsOneWidget);
    expect(find.textContaining('Rev ${old.revision} · IFC'), findsOneWidget);
    await tester.tap(find.text('Open Rev 1'));
    await tester.pumpAndSettle();
    expect(find.textContaining('SUPERSEDED'), findsNothing);
    await tester.binding.setSurfaceSize(null);
  });
}
