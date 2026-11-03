import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/date_symbol_data_local.dart';
import 'package:site_dpr/data/local_store.dart';
import 'package:site_dpr/main.dart';
import 'package:site_dpr/models/models.dart';
import 'package:site_dpr/services/cloud_sync.dart';
import 'package:site_dpr/services/media_service.dart';
import 'package:site_dpr/state/app_state.dart';

void main() {
  setUpAll(() => initializeDateFormatting('en_IN'));

  testWidgets('home shows empty state, then project, then report editor', (tester) async {
    final store = MemoryStore();
    final state = AppState(store);
    await state.load();
    await tester.pumpWidget(SiteDprApp(state: state, cloud: CloudSync(), media: MediaService(store)));
    expect(find.text('No projects yet'), findsOneWidget);

    final p = Project(name: 'Metro Depot', location: 'Pune');
    await tester.runAsync(() async {
      await state.saveProject(p);
      await state.createReport(p.id, DateTime(2026, 10, 28));
    });
    await tester.pumpAndSettle();
    expect(find.text('Metro Depot'), findsOneWidget);

    await tester.tap(find.text('Metro Depot'));
    await tester.pumpAndSettle();
    expect(find.text('Wed, 28 Oct 2026'), findsOneWidget);

    await tester.tap(find.text('Wed, 28 Oct 2026'));
    await tester.pumpAndSettle();
    expect(find.text('DPR-001'), findsWidgets);
    expect(find.text('Weather'), findsOneWidget);

    await tester.tap(find.text('Manpower'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Add trade'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextFormField, 'Trade / Category *'), 'Mason');
    await tester.enterText(find.widgetWithText(TextFormField, 'Male'), '5');
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();
    expect(state.reports.single.manpower.single.male, 5);
    expect(find.text('Mason'), findsOneWidget);
  });
}
