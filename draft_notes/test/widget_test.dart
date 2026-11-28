import 'package:draft_notes/cad/canvas_controller.dart';
import 'package:draft_notes/data/notes_store.dart';
import 'package:draft_notes/data/storage.dart';
import 'package:draft_notes/main.dart';
import 'package:draft_notes/models/drawing.dart';
import 'package:draft_notes/models/note.dart';
import 'package:draft_notes/ui/canvas_screen.dart';
import 'package:draft_notes/ui/common.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';

Future<NotesStore> _store() async {
  final s = NotesStore(MemoryStorage());
  await s.load();
  return s;
}

void _size(WidgetTester tester, Size size) {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
}

void main() {
  testWidgets('desktop shows folders, list and editor side by side', (tester) async {
    _size(tester, const Size(1400, 900));
    final store = await tester.runAsync(_store);
    await tester.pumpWidget(DraftNotesApp(store: store!));
    await tester.pumpAndSettle();

    expect(find.text('Draft Notes'), findsOneWidget);
    expect(find.text('Sketches & Drawings'), findsOneWidget);
    expect(find.text('Site work'), findsWidgets);
    expect(find.text('Pinned'), findsOneWidget);
    // The first note opens in the editor pane.
    expect(find.widgetWithText(TextField, 'Welcome to Draft Notes'), findsOneWidget);

    // Folder filter.
    await tester.tap(find.text('Personal').first);
    await tester.pumpAndSettle();
    expect(find.text('Groceries'), findsWidgets);
    expect(find.text('Client meeting'), findsNothing);

    // Compose a new note and type a title and a list.
    await tester.tap(find.byTooltip('New note'));
    await tester.pumpAndSettle();
    final title = find.widgetWithText(TextField, 'Title');
    expect(title, findsOneWidget);
    await tester.enterText(title, 'Shopping\nBread');
    await tester.pumpAndSettle();
    final created = store.notes.last;
    expect(created.title, 'Shopping');
    expect(created.blocks[1].text, 'Bread');
    expect(created.blocks[1].type, BlockType.body);
    expect(created.folderId, store.folders.last.id);

    await tester.pump(const Duration(seconds: 1));
  });

  testWidgets('phone opens a note full screen and ticks a checklist item', (tester) async {
    _size(tester, const Size(390, 844));
    final store = await tester.runAsync(_store);
    await tester.pumpWidget(DraftNotesApp(store: store!));
    await tester.pumpAndSettle();
    expect(find.text('All Notes'), findsWidgets);

    await tester.tap(find.text('Client meeting'));
    await tester.pumpAndSettle();
    final note = store.notes.firstWhere((n) => n.title == 'Client meeting');
    final item = note.blocks.firstWhere((b) => b.type == BlockType.checklist && !b.checked);
    expect(find.widgetWithText(TextField, item.text), findsOneWidget);
    // Tap the round checkbox to the left of the item.
    final field = tester.getTopLeft(find.widgetWithText(TextField, item.text));
    await tester.tapAt(field + const Offset(-20, 12));
    await tester.pumpAndSettle();
    expect(item.checked, isTrue);

    await tester.pageBack();
    await tester.pumpAndSettle();
    expect(find.text('Groceries'), findsOneWidget);
    await tester.pump(const Duration(seconds: 1));
  });

  testWidgets('canvas: pick LINE and tap two points to draw a line', (tester) async {
    _size(tester, const Size(1200, 800));
    final d = Drawing.cad()..entities.clear();
    var changes = 0;
    await tester.pumpWidget(MaterialApp(
      theme: buildTheme(Brightness.dark),
      home: CanvasScreen(drawing: d, title: 'Test', onChanged: () => changes++, onClose: () {}),
    ));
    await tester.pumpAndSettle();
    expect(find.text('GRID'), findsOneWidget);
    expect(find.text('OSNAP'), findsOneWidget);

    await tester.tap(find.byTooltip('Line  (LINE)'));
    await tester.pumpAndSettle();
    expect(find.textContaining('Specify first point'), findsWidgets);

    await tester.tapAt(const Offset(400, 300));
    await tester.pump();
    await tester.tapAt(const Offset(600, 300));
    await tester.pumpAndSettle();
    expect(d.entities.whereType<LineEnt>(), hasLength(1));
    expect(changes, greaterThan(0));

    // Typing on the canvas goes to the command line.
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.enterText(find.byType(TextField), 'C');
    await tester.testTextInput.receiveAction(TextInputAction.send);
    await tester.pumpAndSettle();
    await tester.enterText(find.byType(TextField), '0,0');
    await tester.testTextInput.receiveAction(TextInputAction.send);
    await tester.enterText(find.byType(TextField), '500');
    await tester.testTextInput.receiveAction(TextInputAction.send);
    await tester.pumpAndSettle();
    expect(d.entities.whereType<CircleEnt>().single.r, 500);
  });

  testWidgets('canvas: pen strokes on handwriting paper', (tester) async {
    _size(tester, const Size(800, 1000));
    final d = Drawing.ink();
    await tester.pumpWidget(MaterialApp(
      home: CanvasScreen(drawing: d, title: 'Ink', onChanged: () {}, onClose: () {}),
    ));
    await tester.pumpAndSettle();
    final gesture = await tester.startGesture(const Offset(200, 300), kind: PointerDeviceKind.stylus);
    for (var i = 1; i <= 10; i++) {
      await gesture.moveTo(Offset(200 + i * 15.0, 300 + (i.isEven ? 10 : -10)));
    }
    await gesture.up();
    await tester.pumpAndSettle();
    final stroke = d.entities.whereType<InkStroke>().single;
    expect(stroke.points.length, greaterThan(5));
    expect(stroke.pen, InkPen.pen);

    // Eraser by area splits the stroke in two.
    await tester.tap(find.byTooltip('Eraser'));
    await tester.pumpAndSettle();
    await tester.tapAt(const Offset(275, 300));
    await tester.pumpAndSettle();
    expect(d.entities.whereType<InkStroke>().length, 2);
  });

  test('tool metadata', () {
    expect(Tool.line.isCad, isTrue);
    expect(Tool.pen.isInk, isTrue);
    expect(Tool.forCommand('DIMALIGNED'), Tool.dimension);
  });
}
