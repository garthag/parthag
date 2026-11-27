import 'dart:convert';

import 'package:draft_notes/cad/exporters.dart';
import 'package:draft_notes/data/notes_store.dart';
import 'package:draft_notes/data/storage.dart';
import 'package:draft_notes/models/drawing.dart';
import 'package:draft_notes/models/note.dart';
import 'package:flutter_test/flutter_test.dart';

Drawing _sample() {
  final d = Drawing.cad();
  d.entities.addAll([
    InkStroke(layerId: '0', color: 0xFF2F6FED, points: const [Offset(0, 0), Offset(5, 5)], pressures: const [.2, .8]),
    LineEnt(layerId: '0', a: Offset.zero, b: const Offset(10, 0)),
    PolyEnt(layerId: '0', points: const [Offset(0, 0), Offset(4, 0), Offset(4, 3)], closed: true),
    CircleEnt(layerId: '0', c: const Offset(1, 2), r: 3, color: 0xFFFF0000),
    ArcEnt(layerId: '0', c: Offset.zero, r: 5, start: 0, sweep: 1.5),
    TextEnt(layerId: '0', pos: const Offset(2, 2), text: 'Room <1> & "2"', height: 2.5),
    DimEnt(layerId: '0', a: Offset.zero, b: const Offset(10, 0), loc: const Offset(5, -3)),
  ]);
  return d;
}

void main() {
  test('drawing survives a JSON round trip', () {
    final d = _sample();
    final back = Drawing.fromJson(jsonDecode(jsonEncode(d.toJson())) as Map<String, dynamic>);
    expect(back.entities.map((e) => e.type), d.entities.map((e) => e.type));
    expect(back.layers.map((l) => l.name), ['0', 'Walls', 'Dimensions', 'Notes']);
    expect((back.entities[0] as InkStroke).pressures, [.2, .8]);
    expect((back.entities[3] as CircleEnt).color, 0xFFFF0000);
    expect((back.entities[4] as ArcEnt).sweep, 1.5);
    expect((back.entities[6] as DimEnt).value, 10);
  });

  test('note title, snippet, tags and markdown', () {
    final n = Note(blocks: [
      NoteBlock(type: BlockType.title, text: 'Plan'),
      NoteBlock(text: 'Check the #site and #Drawings today'),
      NoteBlock(type: BlockType.checklist, text: 'Order cement', checked: true),
      NoteBlock(type: BlockType.numbered, text: 'One'),
      NoteBlock(type: BlockType.numbered, text: 'Two'),
    ]);
    expect(n.title, 'Plan');
    expect(n.snippet, startsWith('Check the'));
    expect(n.tags, {'site', 'drawings'});
    expect(n.checklistProgress, (1, 1));
    expect(n.toMarkdown(), contains('- [x] Order cement'));
    expect(n.toMarkdown(), contains('2. Two'));
    expect(findTags('email a#b and #ok'), ['ok']);
  });

  test('store seeds samples, saves and loads again', () async {
    final storage = MemoryStorage();
    final s = NotesStore(storage);
    await s.load();
    expect(s.notes, hasLength(4));
    expect(s.notesIn(Scope.sketches), hasLength(2));
    expect(s.tags, containsAll(['site', 'getting-started']));
    final f = s.addFolder('Ideas');
    final n = s.createNote(folderId: f.id);
    n.blocks.first.text = 'Fresh idea';
    s.deleteNote(s.notes.first);
    await s.save();

    final again = NotesStore(storage);
    await again.load();
    expect(again.notes, hasLength(5));
    expect(again.notesIn(Scope(ScopeKind.folder, f.id)).single.title, 'Fresh idea');
    expect(again.notesIn(Scope.trash), hasLength(1));
    expect(again.notesIn(Scope.all, query: 'fresh'), hasLength(1));
    // Text inside drawings is searchable too.
    expect(again.notesIn(Scope.all, query: 'bedroom').single.title, 'Ground floor plan');
  });

  test('date groups follow Apple Notes', () {
    final now = DateTime(2026, 11, 27, 12);
    Note at(DateTime d, {bool pinned = false}) => Note(modified: d, pinned: pinned);
    final groups = groupByDate([
      at(now, pinned: true),
      at(now),
      at(now.subtract(const Duration(days: 1))),
      at(now.subtract(const Duration(days: 5))),
      at(now.subtract(const Duration(days: 20))),
      at(DateTime(2026, 2, 1)),
      at(DateTime(2024, 5, 1)),
    ], now: now);
    expect(groups.map((g) => g.$1), ['Pinned', 'Today', 'Yesterday', 'Previous 7 Days', 'Previous 30 Days', 'February', '2024']);
  });

  test('DXF export is valid R12 with layers and entities', () {
    final dxf = drawingToDxf(_sample());
    expect(dxf, startsWith('  0\r\nSECTION'));
    expect(dxf, contains('AC1009'));
    expect(dxf.trimRight(), endsWith('EOF'));
    for (final t in ['LINE', 'POLYLINE', 'VERTEX', 'SEQEND', 'CIRCLE', 'ARC', 'TEXT', 'LAYER']) {
      expect(dxf, contains('\r\n$t\r\n'), reason: t);
    }
    expect(dxf, contains('\r\nWALLS\r\n'));
    final lines = dxf.split('\r\n');
    expect(lines.length.isOdd, isTrue, reason: 'group codes and values come in pairs');
  });

  test('SVG export escapes text', () {
    final svg = drawingToSvg(_sample());
    expect(svg, contains('<svg'));
    expect(svg, contains('Room &lt;1&gt; &amp; &quot;2&quot;'));
    expect(svg.trim(), endsWith('</svg>'));
  });

  test('ACI colour mapping', () {
    expect(aciFor(kAdaptiveColor), 7);
    expect(aciFor(0xFFFF0000), 1);
    expect(aciFor(0xFF0000FF), 5);
    expect(aciFor(0xFF111111), 7);
  });
}
