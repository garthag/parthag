import 'package:flutter_test/flutter_test.dart';
import 'package:intl/date_symbol_data_local.dart';
import 'package:site_dpr/data/local_store.dart';
import 'package:site_dpr/data/sample_data.dart';
import 'package:site_dpr/models/models.dart';
import 'package:site_dpr/services/pdf_service.dart';
import 'package:site_dpr/services/report_text.dart';
import 'package:site_dpr/state/app_state.dart';

DailyReport sample(String projectId) => DailyReport(
      projectId: projectId,
      reportNo: 1,
      date: DateTime(2026, 10, 27),
      manpower: [
        ManpowerEntry(category: 'Mason', male: 6, female: 0, hours: 8),
        ManpowerEntry(category: 'Beldar / Mazdoor', skill: 'Unskilled', male: 10, female: 4, hours: 8),
      ],
      machinery: [MachineryEntry(name: 'Transit mixer', count: 2, workingHrs: 6)],
      activities: [
        WorkActivity(description: 'RCC slab', location: 'Tower A - 2nd', unit: 'cum', totalScope: 200, previousQty: 50, todayQty: 30),
      ],
      materials: [MaterialEntry(name: 'Cement OPC 53 Grade', unit: 'bags', opening: 100, received: 400, consumed: 320)],
      quality: [QualityEntry(testType: 'Cube casting (7 / 28 day)', grade: 'M25', samples: 6)],
      safety: SafetyInfo(toolboxTalk: true, toolboxTopic: 'Housekeeping', incidents: [SafetyIncident(type: 'Near miss', description: 'Loose plank')]),
      hindrances: [Hindrance(category: 'Power cut', hoursLost: 2)],
      media: [MediaItem(type: MediaType.photo, localPath: '/nope.jpg', tags: ['Slab'], latitude: 19.07, longitude: 72.87)],
      tomorrowPlan: ['Deshuttering of 1st slab'],
    );

void main() {
  setUpAll(() => initializeDateFormatting('en_IN'));

  test('derived quantities', () {
    final r = sample('p');
    expect(r.totalManpower, 20);
    expect(r.totalManHours, 160);
    expect(r.activities.first.cumulativeQty, 80);
    expect(r.activities.first.percentComplete, 40);
    expect(r.materials.first.closing, 180);
    expect(r.safety.isSafeDay, isTrue);
    r.safety.incidents.add(SafetyIncident(type: 'Lost time injury (LTI)'));
    expect(r.safety.isSafeDay, isFalse);
  });

  test('json round trip keeps everything', () {
    final r = sample('p');
    final back = DailyReport.fromJson(r.toJson());
    expect(back.toJson(), r.toJson());
  });

  test('carry forward rolls cumulative qty and stock', () {
    final next = sample('p').carryForward(reportNo: 2, date: DateTime(2026, 10, 28));
    expect(next.reportNo, 2);
    expect(next.activities.first.previousQty, 80);
    expect(next.activities.first.todayQty, 0);
    expect(next.materials.first.opening, 180);
    expect(next.materials.first.received, 0);
    expect(next.manpower.length, 2);
    expect(next.machinery.first.workingHrs, 0);
    expect(next.media, isEmpty);
    expect(next.hindrances, isEmpty);
  });

  test('app state creates carried-forward reports and summarises', () async {
    final state = AppState(MemoryStore());
    await state.load();
    final p = Project(name: 'Test Tower', startDate: DateTime(2026, 1, 1));
    await state.saveProject(p);
    final first = sample(p.id);
    await state.saveReport(first);

    final second = await state.createReport(p.id, DateTime(2026, 10, 28));
    expect(second.reportNo, 2);
    expect(second.activities.first.previousQty, 80);
    second.activities.first.todayQty = 20;
    await state.saveReport(second);

    expect(state.reportOn(p.id, DateTime(2026, 10, 28))?.id, second.id);
    final s = state.summary(p.id);
    expect(s.reportCount, 2);
    expect(s.activities.single.cumulativeQty, 100);
    expect(s.overallProgress, 50);
    expect(s.photos, 1);

    // Persisted and reloadable.
    final reloaded = AppState(state.store);
    await reloaded.load();
    expect(reloaded.reports.length, 2);
    expect(reloaded.projects.single.name, 'Test Tower');

    await state.deleteProject(p.id);
    expect(state.reports, isEmpty);
  });

  test('whatsapp summary contains key lines', () {
    final text = ReportText.summary(Project(name: 'Test Tower'), sample('p'));
    expect(text, contains('DPR-001 | 27-10-2026'));
    expect(text, contains('*Manpower: 20*'));
    expect(text, contains('RCC slab @ Tower A - 2nd: 30 cum (Cum. 80 - 40.0%)'));
    expect(text, contains('Power cut'));
  });

  test('pdf builds', () async {
    final bytes = await PdfService.build(Project(name: 'Test Tower', client: 'CPWD'), sample('p'));
    expect(bytes.length, greaterThan(1000));
    expect(String.fromCharCodes(bytes.take(5)), '%PDF-');
  });

  test('sample project is consistent', () async {
    final state = AppState(MemoryStore());
    await state.load();
    final p = await createSampleProject(state);
    final reports = state.reportsFor(p.id);
    expect(reports.map((r) => r.reportNo), [3, 2, 1]);
    // Cumulative of report 3 = initial + all three days.
    final slab = reports.first.activities.first;
    expect(slab.cumulativeQty, 812 + 96);
    // Stock chains: opening of each day equals closing of the previous one.
    expect(reports[0].materials[0].opening, reports[1].materials[0].closing);
    expect(state.summary(p.id).incidents, 1);
  });
}
