import 'dart:math' as math;
import 'dart:ui';

import '../models/drawing.dart';
import '../models/note.dart';

/// Content shown on first launch so every feature has something to try.
(List<Folder>, List<Note>) sampleContent({DateTime? now}) {
  now ??= DateTime.now();
  final work = Folder(name: 'Site work');
  final personal = Folder(name: 'Personal');

  final welcome = Note(
    pinned: true,
    created: now,
    modified: now,
    blocks: [
      NoteBlock(type: BlockType.title, text: 'Welcome to Draft Notes'),
      NoteBlock(text: 'Notes, handwriting and precise CAD sketches in one place. #getting-started'),
      NoteBlock(type: BlockType.heading, text: 'Try these'),
      NoteBlock(type: BlockType.checklist, text: 'Tap a circle to tick a checklist item', checked: true),
      NoteBlock(type: BlockType.checklist, text: 'Press Return in a list to add the next item'),
      NoteBlock(type: BlockType.checklist, text: 'Use Aa to change a paragraph to a heading or list'),
      NoteBlock(type: BlockType.checklist, text: 'Open "Ground floor plan" and type L in the command line'),
      NoteBlock(type: BlockType.checklist, text: 'Write with the pen on the sketch below'),
      NoteBlock(type: BlockType.heading, text: 'Three ways to write'),
      NoteBlock(type: BlockType.numbered, text: 'Text: titles, headings, lists, checklists and tags.'),
      NoteBlock(type: BlockType.numbered, text: 'Handwriting: pen, pencil, highlighter, eraser and lasso on paper templates.'),
      NoteBlock(type: BlockType.numbered, text: 'CAD: line, polyline, circle, arc, dimensions, layers, object snaps and a command line.'),
      NoteBlock.sketch(_doodle()),
      NoteBlock(type: BlockType.quote, text: 'Tip: on a tablet, turn on "Draw with stylus only" so your finger scrolls and pans.'),
    ],
  );

  final plan = Note(
    folderId: work.id,
    created: now.subtract(const Duration(hours: 3)),
    modified: now.subtract(const Duration(hours: 2)),
    blocks: [
      NoteBlock(type: BlockType.title, text: 'Ground floor plan'),
      NoteBlock(text: 'Living room and bedroom for the Kothrud site. All sizes in mm. #site #drawings'),
      NoteBlock.sketch(_floorPlan()),
      NoteBlock(type: BlockType.subheading, text: 'Check on next visit'),
      NoteBlock(type: BlockType.bullet, text: 'Door D1 opening is 900 clear'),
      NoteBlock(type: BlockType.bullet, text: 'Partition wall 115 thick, brick'),
      NoteBlock(type: BlockType.bullet, text: 'Column C1 at grid B-2'),
    ],
  );

  final meeting = Note(
    folderId: work.id,
    created: now.subtract(const Duration(days: 1, hours: 4)),
    modified: now.subtract(const Duration(days: 1, hours: 3)),
    blocks: [
      NoteBlock(type: BlockType.title, text: 'Client meeting'),
      NoteBlock(type: BlockType.subheading, text: 'Agenda'),
      NoteBlock(type: BlockType.numbered, text: 'Revised kitchen layout'),
      NoteBlock(type: BlockType.numbered, text: 'Tile selection for bathrooms'),
      NoteBlock(type: BlockType.numbered, text: 'Payment schedule'),
      NoteBlock(type: BlockType.subheading, text: 'Actions'),
      NoteBlock(type: BlockType.checklist, text: 'Send revised plan by Friday #site', checked: true),
      NoteBlock(type: BlockType.checklist, text: 'Get three tile quotes'),
      NoteBlock(type: BlockType.mono, text: 'Kitchen counter: 2400 x 600 x 850'),
    ],
  );

  final shopping = Note(
    folderId: personal.id,
    color: noteColors[1],
    created: now.subtract(const Duration(days: 5)),
    modified: now.subtract(const Duration(days: 5)),
    blocks: [
      NoteBlock(type: BlockType.title, text: 'Groceries'),
      NoteBlock(type: BlockType.checklist, text: 'Milk', checked: true),
      NoteBlock(type: BlockType.checklist, text: 'Bread'),
      NoteBlock(type: BlockType.checklist, text: 'Tomatoes'),
      NoteBlock(type: BlockType.checklist, text: 'Coffee beans'),
    ],
  );

  return ([work, personal], [welcome, plan, meeting, shopping]);
}

/// A small handwritten page: a wavy underline, a box, an arrow and a star.
Drawing _doodle() {
  final d = Drawing.ink(paper: PaperStyle.dotted);
  d.viewCenter = null;
  d.viewZoom = null;
  final layer = d.layers.first.id;
  final rnd = math.Random(7);
  List<Offset> wobble(List<Offset> pts, {int steps = 12, double amp = 1.2}) {
    final out = <Offset>[];
    for (var i = 0; i + 1 < pts.length; i++) {
      for (var s = 0; s < steps; s++) {
        final t = s / steps;
        final p = Offset.lerp(pts[i], pts[i + 1], t)!;
        out.add(p + Offset(rnd.nextDouble() - .5, rnd.nextDouble() - .5) * amp);
      }
    }
    out.add(pts.last);
    return out;
  }

  // Highlighter band behind the heading text.
  d.entities.add(InkStroke(
    layerId: layer,
    color: 0xFFFFE066,
    weight: 22,
    pen: InkPen.highlighter,
    points: wobble([const Offset(20, -40), const Offset(250, -42)], amp: .6),
  ));
  d.entities.add(TextEnt(layerId: layer, color: 0xFF1B1B1F, pos: const Offset(28, -50), text: 'Idea board', height: 26));
  // Wavy underline.
  d.entities.add(InkStroke(
    layerId: layer,
    color: 0xFF2F6FED,
    weight: 3,
    points: [for (var x = 20.0; x <= 260; x += 6) Offset(x, -66 + math.sin(x / 9) * 3)],
  ));
  // Box with an arrow to a star.
  d.entities.add(InkStroke(
    layerId: layer,
    color: 0xFF1B1B1F,
    weight: 2.6,
    points: wobble(const [Offset(30, -100), Offset(170, -98), Offset(172, -170), Offset(28, -172), Offset(30, -100)]),
  ));
  d.entities.add(TextEnt(layerId: layer, color: 0xFF1B1B1F, pos: const Offset(48, -145), text: 'Sketch', height: 22));
  d.entities.add(InkStroke(
    layerId: layer,
    color: 0xFFE5484D,
    weight: 3,
    points: wobble(const [Offset(185, -135), Offset(280, -135)], steps: 16),
  ));
  d.entities.add(InkStroke(
    layerId: layer,
    color: 0xFFE5484D,
    weight: 3,
    points: wobble(const [Offset(265, -122), Offset(282, -135), Offset(265, -148)], steps: 6),
  ));
  final star = <Offset>[];
  for (var i = 0; i <= 10; i++) {
    final r = i.isEven ? 38.0 : 16.0;
    final a = math.pi / 2 + i * math.pi / 5;
    star.add(Offset(340 + r * math.cos(a), -135 + r * math.sin(a)));
  }
  d.entities.add(InkStroke(layerId: layer, color: 0xFFF5A524, weight: 3.2, points: wobble(star, steps: 4, amp: .8)));
  return d;
}

/// An 8 m × 5 m two-room plan with walls, a door swing, dimensions and labels.
Drawing _floorPlan() {
  final d = Drawing.cad();
  d.viewCenter = null;
  d.viewZoom = null;
  final walls = d.layers[1].id;
  final dims = d.layers[2].id;
  final notes = d.layers[3].id;
  PolyEnt rect(double x1, double y1, double x2, double y2) => PolyEnt(
        layerId: walls,
        closed: true,
        weight: 2.2,
        points: [Offset(x1, y1), Offset(x2, y1), Offset(x2, y2), Offset(x1, y2)],
      );
  d.entities.addAll([
    rect(0, 0, 8000, 5000),
    rect(230, 230, 7770, 4770),
    LineEnt(layerId: walls, weight: 2.2, a: const Offset(4800, 230), b: const Offset(4800, 4770)),
    LineEnt(layerId: walls, weight: 2.2, a: const Offset(4915, 230), b: const Offset(4915, 4770)),
    // Door D1 in the bottom wall.
    LineEnt(layerId: '0', a: const Offset(1200, 230), b: const Offset(1200, 1130)),
    ArcEnt(layerId: '0', c: const Offset(1200, 230), r: 900, start: 0, sweep: math.pi / 2),
    // Window W1 in the top wall.
    LineEnt(layerId: '0', a: const Offset(5800, 4885), b: const Offset(7000, 4885)),
    // Column C1 and a round table.
    CircleEnt(layerId: '0', c: const Offset(2600, 3000), r: 600),
    DimEnt(layerId: dims, a: const Offset(0, 0), b: const Offset(8000, 0), loc: const Offset(4000, -700)),
    DimEnt(layerId: dims, a: const Offset(8000, 0), b: const Offset(8000, 5000), loc: const Offset(8700, 2500)),
    DimEnt(layerId: dims, a: const Offset(230, 4770), b: const Offset(4800, 4770), loc: const Offset(2500, 5600)),
    TextEnt(layerId: notes, pos: const Offset(900, 1800), text: 'LIVING', height: 260),
    TextEnt(layerId: notes, pos: const Offset(5600, 2400), text: 'BEDROOM', height: 260),
    TextEnt(layerId: notes, pos: const Offset(1350, 600), text: 'D1', height: 180),
    TextEnt(layerId: notes, pos: const Offset(6150, 4350), text: 'W1', height: 180),
  ]);
  return d;
}
