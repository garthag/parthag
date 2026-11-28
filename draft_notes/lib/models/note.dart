import 'drawing.dart';

/// Paragraph styles, following Apple Notes' format menu plus a quote style.
enum BlockType {
  title('Title'),
  heading('Heading'),
  subheading('Subheading'),
  body('Body'),
  mono('Monostyled'),
  bullet('Bulleted list'),
  dashed('Dashed list'),
  numbered('Numbered list'),
  checklist('Checklist'),
  quote('Block quote'),
  sketch('Sketch');

  const BlockType(this.label);
  final String label;

  bool get isList =>
      this == BlockType.bullet || this == BlockType.dashed || this == BlockType.numbered || this == BlockType.checklist;
  bool get isText => this != BlockType.sketch;
}

class NoteBlock {
  NoteBlock({String? id, this.type = BlockType.body, this.text = '', this.checked = false, this.indent = 0, this.drawing})
      : id = id ?? newId();

  factory NoteBlock.sketch(Drawing d) => NoteBlock(type: BlockType.sketch, drawing: d);

  final String id;
  BlockType type;
  String text;
  bool checked;
  int indent;
  Drawing? drawing;

  Map<String, dynamic> toJson() => {
        'id': id,
        'type': type.name,
        if (text.isNotEmpty) 'text': text,
        if (checked) 'checked': true,
        if (indent != 0) 'indent': indent,
        if (drawing != null) 'drawing': drawing!.toJson(),
      };

  factory NoteBlock.fromJson(Map<String, dynamic> j) => NoteBlock(
        id: j['id'] as String?,
        type: BlockType.values.asNameMap()[j['type']] ?? BlockType.body,
        text: (j['text'] as String?) ?? '',
        checked: j['checked'] == true,
        indent: (j['indent'] as int?) ?? 0,
        drawing: j['drawing'] == null ? null : Drawing.fromJson((j['drawing'] as Map).cast()),
      );
}

final tagPattern = RegExp(r'(?<![\w#])#([\p{L}\p{N}_\-]+)', unicode: true);

/// Finds `#tags` in text, like Apple Notes tags.
Iterable<String> findTags(String text) => tagPattern.allMatches(text).map((m) => m.group(1)!.toLowerCase());

/// Background tints for notes (Samsung Notes style note colours).
const noteColors = <int>[0, 0xFFFFF3C4, 0xFFFFE0D6, 0xFFE3F2D9, 0xFFD9ECFA, 0xFFEADCF8, 0xFFE9E9EB];

class Note {
  Note({
    String? id,
    this.folderId,
    List<NoteBlock>? blocks,
    this.pinned = false,
    this.color = 0,
    DateTime? created,
    DateTime? modified,
    this.deletedAt,
  })  : id = id ?? newId(),
        blocks = blocks ?? [NoteBlock(type: BlockType.title)],
        created = created ?? DateTime.now(),
        modified = modified ?? created ?? DateTime.now();

  final String id;
  String? folderId;
  List<NoteBlock> blocks;
  bool pinned;
  int color;
  DateTime created;
  DateTime modified;
  DateTime? deletedAt;

  bool get isDeleted => deletedAt != null;

  Iterable<NoteBlock> get _textBlocks => blocks.where((b) => b.type.isText && b.text.trim().isNotEmpty);

  /// The first line of text is the title, as in Apple Notes.
  String get title {
    final first = _textBlocks.firstOrNull;
    if (first != null) return first.text.trim().split('\n').first;
    return hasSketch ? 'Sketch' : 'New Note';
  }

  /// Text after the title, flattened to one line for list previews.
  String get snippet {
    final rest = _textBlocks.skip(1).map((b) => b.text.trim().replaceAll('\n', ' ')).join('  ');
    if (rest.isNotEmpty) return rest;
    final sketches = blocks.where((b) => b.type == BlockType.sketch).length;
    if (sketches > 0) return sketches == 1 ? '1 sketch' : '$sketches sketches';
    return 'No additional text';
  }

  String get plainText => blocks.where((b) => b.type.isText).map((b) => b.text).join('\n');

  bool get hasSketch => blocks.any((b) => b.type == BlockType.sketch);
  bool get hasChecklist => blocks.any((b) => b.type == BlockType.checklist);

  Drawing? get firstDrawing => blocks.where((b) => b.drawing != null).firstOrNull?.drawing;

  (int, int) get checklistProgress {
    final items = blocks.where((b) => b.type == BlockType.checklist && b.text.trim().isNotEmpty);
    return (items.where((b) => b.checked).length, items.length);
  }

  Set<String> get tags => {for (final b in blocks) ...findTags(b.text)};

  bool matches(String query) {
    final q = query.toLowerCase();
    if (plainText.toLowerCase().contains(q)) return true;
    for (final b in blocks) {
      for (final e in b.drawing?.entities ?? const <Entity>[]) {
        if (e is TextEnt && e.text.toLowerCase().contains(q)) return true;
      }
    }
    return false;
  }

  /// Markdown version for sharing and copying.
  String toMarkdown() {
    final out = StringBuffer();
    var number = 0;
    for (final b in blocks) {
      final pad = '  ' * b.indent;
      number = b.type == BlockType.numbered ? number + 1 : 0;
      switch (b.type) {
        case BlockType.title:
          out.writeln('# ${b.text}');
        case BlockType.heading:
          out.writeln('## ${b.text}');
        case BlockType.subheading:
          out.writeln('### ${b.text}');
        case BlockType.body:
          out.writeln(b.text);
        case BlockType.mono:
          out.writeln('```\n${b.text}\n```');
        case BlockType.bullet:
        case BlockType.dashed:
          out.writeln('$pad- ${b.text}');
        case BlockType.numbered:
          out.writeln('$pad$number. ${b.text}');
        case BlockType.checklist:
          out.writeln('$pad- [${b.checked ? 'x' : ' '}] ${b.text}');
        case BlockType.quote:
          out.writeln('> ${b.text}');
        case BlockType.sketch:
          final d = b.drawing;
          out.writeln('[Sketch: ${d?.entities.length ?? 0} objects]');
      }
    }
    return out.toString().trimRight();
  }

  Map<String, dynamic> toJson() => {
        'id': id,
        if (folderId != null) 'folder': folderId,
        'blocks': [for (final b in blocks) b.toJson()],
        if (pinned) 'pinned': true,
        if (color != 0) 'color': color,
        'created': created.toIso8601String(),
        'modified': modified.toIso8601String(),
        if (deletedAt != null) 'deleted': deletedAt!.toIso8601String(),
      };

  factory Note.fromJson(Map<String, dynamic> j) {
    final blocks = [for (final b in (j['blocks'] as List?) ?? const []) NoteBlock.fromJson((b as Map).cast())];
    return Note(
      id: j['id'] as String?,
      folderId: j['folder'] as String?,
      blocks: blocks.isEmpty ? null : blocks,
      pinned: j['pinned'] == true,
      color: (j['color'] as int?) ?? 0,
      created: DateTime.tryParse((j['created'] as String?) ?? ''),
      modified: DateTime.tryParse((j['modified'] as String?) ?? ''),
      deletedAt: j['deleted'] == null ? null : DateTime.tryParse(j['deleted'] as String),
    );
  }
}

class Folder {
  Folder({String? id, required this.name}) : id = id ?? newId();

  final String id;
  String name;

  Map<String, dynamic> toJson() => {'id': id, 'name': name};

  factory Folder.fromJson(Map<String, dynamic> j) => Folder(id: j['id'] as String?, name: (j['name'] as String?) ?? 'Folder');
}
