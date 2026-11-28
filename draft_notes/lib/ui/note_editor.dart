import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/services.dart';
import 'package:material_ui/material_ui.dart';
import 'package:provider/provider.dart';

import '../data/notes_store.dart';
import '../models/drawing.dart';
import '../models/note.dart';
import 'canvas_screen.dart';
import 'common.dart';
import 'note_actions.dart';

/// Colours `#tags` inside a text field, like Apple Notes.
class TagTextController extends TextEditingController {
  TagTextController({super.text});

  Color tagColor = kAccent;

  @override
  TextSpan buildTextSpan({required BuildContext context, TextStyle? style, required bool withComposing}) {
    final composing = withComposing && value.isComposingRangeValid && !value.composing.isCollapsed;
    if (!text.contains('#') || composing) {
      return super.buildTextSpan(context: context, style: style, withComposing: withComposing);
    }
    final children = <TextSpan>[];
    var last = 0;
    for (final m in tagPattern.allMatches(text)) {
      if (m.start > last) children.add(TextSpan(text: text.substring(last, m.start)));
      children.add(TextSpan(text: m.group(0), style: TextStyle(color: tagColor, fontWeight: FontWeight.w600)));
      last = m.end;
    }
    if (last < text.length) children.add(TextSpan(text: text.substring(last)));
    return TextSpan(style: style, children: children);
  }
}

/// Block editor: one text field per paragraph, with Apple Notes paragraph
/// styles, checklists, and sketches that open the drawing canvas.
class NoteEditor extends StatefulWidget {
  const NoteEditor({super.key, required this.note, this.showBack = false, this.onRemoved});

  final Note note;
  final bool showBack;

  /// Called when the note is deleted or moved out of view.
  final VoidCallback? onRemoved;

  @override
  State<NoteEditor> createState() => _NoteEditorState();
}

class _NoteEditorState extends State<NoteEditor> {
  final _ctrls = <String, TagTextController>{};
  final _nodes = <String, FocusNode>{};
  String? _focused;
  bool _hasFocus = false;
  late final NotesStore _store;

  Note get note => widget.note;
  bool get _readOnly => note.isDeleted;

  @override
  void initState() {
    super.initState();
    _store = context.read<NotesStore>();
    final blank = note.blocks.length == 1 && note.blocks.first.text.isEmpty;
    if (blank && !_readOnly) {
      WidgetsBinding.instance.addPostFrameCallback((_) => _focus(note.blocks.first, 0));
    }
  }

  @override
  void dispose() {
    for (final c in _ctrls.values) {
      c.dispose();
    }
    for (final n in _nodes.values) {
      n.dispose();
    }
    final store = _store;
    final n = note;
    scheduleMicrotask(() => store.discardIfEmpty(n));
    super.dispose();
  }

  TagTextController _ctrl(NoteBlock b) {
    final c = _ctrls.putIfAbsent(b.id, () => TagTextController(text: b.text));
    c.tagColor = Theme.of(context).colorScheme.primary;
    return c;
  }

  FocusNode _node(NoteBlock b) => _nodes.putIfAbsent(b.id, () {
        final node = FocusNode(onKeyEvent: (_, e) => _onKey(b, e));
        node.addListener(() {
          if (!mounted) return;
          final any = _nodes.values.any((n) => n.hasFocus);
          if (node.hasFocus || any != _hasFocus) {
            setState(() {
              if (node.hasFocus) _focused = b.id;
              _hasFocus = any;
            });
          }
        });
        return node;
      });

  void _focus(NoteBlock b, int offset) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      final n = _nodes[b.id];
      final c = _ctrls[b.id];
      if (n == null || c == null) return;
      n.requestFocus();
      c.selection = TextSelection.collapsed(offset: math.min(offset, c.text.length));
    });
  }

  void _touch() => _store.touch(note);

  void _forget(Iterable<String> ids) {
    final ctrls = [for (final id in ids) ?_ctrls.remove(id)];
    final nodes = [for (final id in ids) ?_nodes.remove(id)];
    WidgetsBinding.instance.addPostFrameCallback((_) {
      for (final c in ctrls) {
        c.dispose();
      }
      for (final n in nodes) {
        n.dispose();
      }
    });
  }

  NoteBlock? get _target {
    final f = note.blocks.where((b) => b.id == _focused && b.type.isText).firstOrNull;
    return f ?? note.blocks.lastWhere((b) => b.type.isText, orElse: () => note.blocks.first);
  }

  // -- Editing ----------------------------------------------------------------

  void _changed(NoteBlock b, String v) {
    if (v.contains('\n')) {
      _split(b, v);
      return;
    }
    b.text = v;
    _touch();
  }

  /// Return key: splits the paragraph. Return on an empty list item ends the
  /// list, as in Apple Notes.
  void _split(NoteBlock b, String v) {
    final ctrl = _ctrls[b.id]!;
    final lines = v.split('\n');
    if (b.type.isList && lines.length == 2 && lines.every((l) => l.isEmpty)) {
      b
        ..type = BlockType.body
        ..indent = 0
        ..text = '';
      ctrl.value = const TextEditingValue(text: '', selection: TextSelection.collapsed(offset: 0));
      setState(() {});
      _touch();
      return;
    }
    final cursor = ctrl.selection.baseOffset.clamp(0, v.length);
    final lineOfCursor = '\n'.allMatches(v.substring(0, cursor)).length;
    final column = cursor - (v.lastIndexOf('\n', cursor - 1) + 1);
    final nextType = switch (b.type) {
      BlockType.title || BlockType.heading || BlockType.subheading || BlockType.quote => BlockType.body,
      _ => b.type,
    };
    b.text = lines.first;
    ctrl.value = TextEditingValue(text: b.text, selection: TextSelection.collapsed(offset: b.text.length));
    final index = note.blocks.indexOf(b);
    final added = [
      for (final l in lines.skip(1)) NoteBlock(type: nextType, text: l, indent: nextType.isList ? b.indent : 0),
    ];
    note.blocks.insertAll(index + 1, added);
    setState(() {});
    _touch();
    final target = lineOfCursor == 0 ? b : added[lineOfCursor - 1];
    _focus(target, lineOfCursor == 0 ? cursor : column);
  }

  KeyEventResult _onKey(NoteBlock b, KeyEvent e) {
    if (e is! KeyDownEvent && e is! KeyRepeatEvent) return KeyEventResult.ignored;
    final c = _ctrls[b.id];
    if (c == null || _readOnly) return KeyEventResult.ignored;
    final k = e.logicalKey;
    if (k == LogicalKeyboardKey.backspace && c.selection.isCollapsed && c.selection.baseOffset == 0) {
      return _backspaceAtStart(b) ? KeyEventResult.handled : KeyEventResult.ignored;
    }
    if (k == LogicalKeyboardKey.tab && b.type.isList) {
      _indent(b, HardwareKeyboard.instance.isShiftPressed ? -1 : 1);
      return KeyEventResult.handled;
    }
    return KeyEventResult.ignored;
  }

  bool _backspaceAtStart(NoteBlock b) {
    final i = note.blocks.indexOf(b);
    if (b.type.isList || b.type == BlockType.quote || b.type == BlockType.mono) {
      setState(() {
        if (b.indent > 0) {
          b.indent--;
        } else {
          b.type = BlockType.body;
        }
      });
      _touch();
      return true;
    }
    if (i <= 0) return false;
    final prev = note.blocks[i - 1];
    if (prev.type == BlockType.sketch) {
      if (b.text.isNotEmpty) return false;
      note.blocks.removeAt(i);
      _forget([b.id]);
      setState(() {});
      _touch();
      final before = note.blocks.take(i - 1).lastWhere((x) => x.type.isText, orElse: () => note.blocks.first);
      if (before.type.isText) _focus(before, before.text.length);
      return true;
    }
    final join = prev.text.length;
    prev.text += b.text;
    _ctrls[prev.id]?.text = prev.text;
    note.blocks.removeAt(i);
    _forget([b.id]);
    setState(() {});
    _touch();
    _focus(prev, join);
    return true;
  }

  void _indent(NoteBlock b, int delta) {
    setState(() => b.indent = (b.indent + delta).clamp(0, 4));
    _touch();
  }

  void _applyType(BlockType t) {
    final b = _target;
    if (b == null) return;
    setState(() {
      if (b.type == t && t.isList) {
        b.type = BlockType.body;
      } else {
        b.type = t;
      }
      if (!b.type.isList) b.indent = 0;
    });
    _touch();
    _focus(b, _ctrls[b.id]?.selection.baseOffset ?? b.text.length);
  }

  void _toggleCheck(NoteBlock b) {
    setState(() => b.checked = !b.checked);
    _touch();
  }

  Future<void> _insertSketch(Drawing d) async {
    final t = _target;
    var at = t == null ? note.blocks.length : note.blocks.indexOf(t) + 1;
    // An empty paragraph is replaced, so the sketch sits where the cursor is.
    if (t != null && t.text.isEmpty && t.type == BlockType.body && note.blocks.indexOf(t) > 0) {
      at = note.blocks.indexOf(t);
      note.blocks.remove(t);
      _forget([t.id]);
    }
    final block = NoteBlock.sketch(d);
    note.blocks.insert(at, block);
    if (at + 1 >= note.blocks.length || !note.blocks[at + 1].type.isText) {
      note.blocks.insert(at + 1, NoteBlock());
    }
    setState(() {});
    _touch();
    await _openSketch(block);
  }

  Future<void> _openSketch(NoteBlock b) async {
    FocusScope.of(context).unfocus();
    final store = _store;
    await Navigator.of(context).push(MaterialPageRoute<void>(
      builder: (_) => CanvasScreen(
        drawing: b.drawing!,
        title: note.title,
        onChanged: () => store.touch(note),
        onClose: () => scheduleMicrotask(store.changed),
      ),
    ));
    if (mounted) setState(() {});
  }

  void _deleteBlock(NoteBlock b) {
    note.blocks.remove(b);
    if (note.blocks.isEmpty) note.blocks.add(NoteBlock(type: BlockType.title));
    _forget([b.id]);
    setState(() {});
    _touch();
  }

  // -- Build ------------------------------------------------------------------

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final wide = MediaQuery.sizeOf(context).width >= 700;
    final bg = note.color == 0
        ? scheme.surface
        : (theme.brightness == Brightness.light
            ? Color(note.color)
            : Color.alphaBlend(Color(note.color).withValues(alpha: .14), scheme.surface));
    return Scaffold(
      backgroundColor: bg,
      appBar: AppBar(
        backgroundColor: bg,
        automaticallyImplyLeading: widget.showBack,
        toolbarHeight: 50,
        actions: [
          if (note.pinned) Icon(Icons.push_pin, size: 18, color: scheme.primary),
          IconButton(
            tooltip: 'Share',
            icon: const Icon(Icons.ios_share),
            onPressed: () => shareText(context, note.toMarkdown(), subject: note.title),
          ),
          PopupMenuButton<String>(
            tooltip: 'More',
            icon: const Icon(Icons.more_horiz),
            itemBuilder: (_) => noteMenuEntries(note),
            onSelected: (v) => runNoteAction(context, note, v, onRemoved: widget.onRemoved).then((_) {
              if (mounted) setState(() {});
            }),
          ),
          if (_hasFocus && !wide)
            TextButton(
              onPressed: () => FocusScope.of(context).unfocus(),
              child: const Text('Done', style: TextStyle(fontWeight: FontWeight.w700)),
            ),
          const SizedBox(width: 6),
        ],
      ),
      body: Column(
        children: [
          if (_readOnly) _deletedBanner(context),
          if (wide && !_readOnly) _toolbar(context, wide),
          Expanded(
            child: GestureDetector(
              behavior: HitTestBehavior.translucent,
              onTap: _readOnly ? null : _tapBelow,
              child: SingleChildScrollView(
                padding: EdgeInsets.fromLTRB(wide ? 40 : 20, 0, wide ? 40 : 20, 160),
                child: Center(
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 820),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Padding(
                          padding: const EdgeInsets.only(bottom: 10),
                          child: Text(
                            noteFullDate(note.modified),
                            textAlign: TextAlign.center,
                            style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 12.5),
                          ),
                        ),
                        for (var i = 0; i < note.blocks.length; i++) _block(context, i, note.blocks[i]),
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ),
          if (!wide && !_readOnly) _toolbar(context, wide),
        ],
      ),
    );
  }

  void _tapBelow() {
    final last = note.blocks.last;
    if (last.type.isText) {
      _focus(last, last.text.length);
    } else {
      final b = NoteBlock();
      setState(() => note.blocks.add(b));
      _focus(b, 0);
    }
  }

  Widget _deletedBanner(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Material(
      color: scheme.surfaceContainerHigh,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 8, 8, 8),
        child: Row(children: [
          const Expanded(child: Text('This note is in Recently Deleted. Recover it to edit.')),
          TextButton(
            onPressed: () {
              _store.restore(note);
              setState(() {});
            },
            child: const Text('Recover'),
          ),
        ]),
      ),
    );
  }

  Widget _toolbar(BuildContext context, bool wide) {
    final scheme = Theme.of(context).colorScheme;
    final current = _target?.type;
    final buttons = <Widget>[
      PopupMenuButton<BlockType>(
        tooltip: 'Paragraph style',
        onSelected: _applyType,
        itemBuilder: (_) => [
          for (final t in BlockType.values.where((t) => t.isText))
            CheckedPopupMenuItem(value: t, checked: t == current, child: Text(t.label, style: _menuStyle(t))),
        ],
        child: const Padding(
          padding: EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          child: Text('Aa', style: TextStyle(fontSize: 19, fontWeight: FontWeight.w600)),
        ),
      ),
      IconButton(tooltip: 'Checklist', icon: const Icon(Icons.checklist), onPressed: () => _applyType(BlockType.checklist)),
      IconButton(tooltip: 'Bulleted list', icon: const Icon(Icons.format_list_bulleted), onPressed: () => _applyType(BlockType.bullet)),
      if (wide) ...[
        IconButton(tooltip: 'Numbered list', icon: const Icon(Icons.format_list_numbered), onPressed: () => _applyType(BlockType.numbered)),
        IconButton(
          tooltip: 'Decrease indent',
          icon: const Icon(Icons.format_indent_decrease),
          onPressed: () => _target == null ? null : _indent(_target!, -1),
        ),
        IconButton(
          tooltip: 'Increase indent',
          icon: const Icon(Icons.format_indent_increase),
          onPressed: () => _target == null ? null : _indent(_target!, 1),
        ),
      ],
      IconButton(
        tooltip: 'Handwriting sketch',
        icon: const Icon(Icons.draw_outlined),
        onPressed: () => _insertSketch(Drawing.ink()),
      ),
      IconButton(
        tooltip: 'CAD drawing',
        icon: const Icon(Icons.architecture),
        onPressed: () => _insertSketch(Drawing.cad()),
      ),
      if (!wide && _hasFocus)
        IconButton(
          tooltip: 'Hide keyboard',
          icon: const Icon(Icons.keyboard_hide_outlined),
          onPressed: () => FocusScope.of(context).unfocus(),
        ),
    ];
    return Container(
      height: 48,
      decoration: BoxDecoration(
        border: wide ? Border(bottom: BorderSide(color: scheme.outlineVariant)) : Border(top: BorderSide(color: scheme.outlineVariant)),
      ),
      child: wide
          ? Row(children: [const SizedBox(width: 12), ...buttons])
          : Row(mainAxisAlignment: MainAxisAlignment.spaceEvenly, children: buttons),
    );
  }

  TextStyle _menuStyle(BlockType t) => switch (t) {
        BlockType.title => const TextStyle(fontSize: 20, fontWeight: FontWeight.w800),
        BlockType.heading => const TextStyle(fontSize: 17, fontWeight: FontWeight.w700),
        BlockType.subheading => const TextStyle(fontSize: 15, fontWeight: FontWeight.w600),
        BlockType.mono => const TextStyle(fontFamily: 'monospace', fontFamilyFallback: ['Consolas', 'Menlo', 'Courier New']),
        BlockType.quote => const TextStyle(fontStyle: FontStyle.italic),
        _ => const TextStyle(),
      };

  TextStyle _style(NoteBlock b, ColorScheme scheme) {
    final base = TextStyle(fontSize: 16.5, height: 1.4, color: scheme.onSurface);
    return switch (b.type) {
      BlockType.title => base.copyWith(fontSize: 28, fontWeight: FontWeight.w800, height: 1.25),
      BlockType.heading => base.copyWith(fontSize: 22, fontWeight: FontWeight.w700, height: 1.3),
      BlockType.subheading => base.copyWith(fontSize: 18.5, fontWeight: FontWeight.w600),
      BlockType.mono => base.copyWith(
          fontSize: 14.5,
          fontFamily: 'monospace',
          fontFamilyFallback: const ['Consolas', 'Menlo', 'Courier New'],
        ),
      BlockType.quote => base.copyWith(fontStyle: FontStyle.italic, color: scheme.onSurfaceVariant),
      BlockType.checklist when b.checked => base.copyWith(
          color: scheme.onSurfaceVariant,
          decoration: TextDecoration.lineThrough,
          decorationColor: scheme.onSurfaceVariant,
        ),
      _ => base,
    };
  }

  int _number(int i) {
    final b = note.blocks[i];
    var n = 1;
    for (var j = i - 1; j >= 0; j--) {
      final p = note.blocks[j];
      if (p.type != BlockType.numbered || p.indent < b.indent) break;
      if (p.indent == b.indent) n++;
    }
    return n;
  }

  Widget _block(BuildContext context, int i, NoteBlock b) {
    if (b.type == BlockType.sketch) return _sketch(context, b);
    final scheme = Theme.of(context).colorScheme;
    final style = _style(b, scheme);
    final hint = switch (b.type) {
      _ when i == 0 => 'Title',
      BlockType.body when note.blocks.length <= 2 => 'Start writing… use #tags to organise',
      BlockType.checklist || BlockType.bullet || BlockType.dashed || BlockType.numbered => 'List item',
      _ => null,
    };
    Widget? lead;
    switch (b.type) {
      case BlockType.bullet:
        lead = Text('•', style: style.copyWith(fontWeight: FontWeight.w900));
      case BlockType.dashed:
        lead = Text('–', style: style);
      case BlockType.numbered:
        lead = Text('${_number(i)}.', style: style.copyWith(fontFeatures: const [FontFeature.tabularFigures()]));
      case BlockType.checklist:
        lead = GestureDetector(
          onTap: _readOnly ? null : () => _toggleCheck(b),
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 150),
            width: 22,
            height: 22,
            margin: const EdgeInsets.only(top: 1),
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              color: b.checked ? scheme.primary : Colors.transparent,
              border: Border.all(color: b.checked ? scheme.primary : scheme.onSurfaceVariant.withValues(alpha: .6), width: 1.6),
            ),
            child: b.checked ? Icon(Icons.check, size: 15, color: scheme.onPrimary) : null,
          ),
        );
      default:
        break;
    }
    final field = TextField(
      controller: _ctrl(b),
      focusNode: _node(b),
      readOnly: _readOnly,
      maxLines: null,
      keyboardType: TextInputType.multiline,
      textCapitalization: TextCapitalization.sentences,
      style: style,
      cursorColor: scheme.primary,
      decoration: InputDecoration.collapsed(hintText: hint, hintStyle: style.copyWith(color: scheme.onSurfaceVariant.withValues(alpha: .6))),
      onChanged: (v) => _changed(b, v),
    );
    Widget row = Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (b.indent > 0) SizedBox(width: 26.0 * b.indent),
        if (lead != null) SizedBox(width: b.type == BlockType.checklist ? 32 : 26, child: Align(alignment: Alignment.topLeft, child: lead)),
        Expanded(child: field),
      ],
    );
    if (b.type == BlockType.quote) {
      row = Container(
        padding: const EdgeInsets.only(left: 12),
        decoration: BoxDecoration(border: Border(left: BorderSide(color: scheme.primary, width: 3))),
        child: row,
      );
    } else if (b.type == BlockType.mono) {
      row = Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        decoration: BoxDecoration(color: scheme.onSurface.withValues(alpha: .05), borderRadius: BorderRadius.circular(6)),
        child: row,
      );
    }
    final top = switch (b.type) {
      BlockType.heading => 14.0,
      BlockType.subheading => 10.0,
      BlockType.checklist => 5.0,
      _ => 3.0,
    };
    return Padding(padding: EdgeInsets.only(top: top, bottom: 1), child: row);
  }

  Widget _sketch(BuildContext context, NoteBlock b) {
    final scheme = Theme.of(context).colorScheme;
    final d = b.drawing!;
    final wide = MediaQuery.sizeOf(context).width >= 700;
    final count = d.entities.length;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 10),
      child: SizedBox(
        height: wide ? 320 : 230,
        child: Material(
          color: d.paper.background,
          borderRadius: BorderRadius.circular(12),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: () => _openSketch(b),
            child: Stack(children: [
              Positioned.fill(child: DrawingPreview(drawing: d, radius: 12)),
              Positioned.fill(
                child: IgnorePointer(
                  child: DecoratedBox(
                    decoration: BoxDecoration(
                      border: Border.all(color: scheme.outlineVariant),
                      borderRadius: BorderRadius.circular(12),
                    ),
                  ),
                ),
              ),
              if (d.isEmpty)
                Center(
                  child: Text(
                    _readOnly ? 'Empty sketch' : 'Tap to draw',
                    style: TextStyle(color: d.paper.foreground.withValues(alpha: .5), fontSize: 15),
                  ),
                ),
              Positioned(
                left: 10,
                bottom: 10,
                child: Container(
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                  decoration: BoxDecoration(color: Colors.black.withValues(alpha: .55), borderRadius: BorderRadius.circular(20)),
                  child: Row(mainAxisSize: MainAxisSize.min, children: [
                    Icon(d.paper.isDark ? Icons.architecture : Icons.draw_outlined, size: 14, color: Colors.white),
                    const SizedBox(width: 4),
                    Text(
                      '${d.paper.isDark ? 'CAD drawing' : 'Handwriting'} · $count object${count == 1 ? '' : 's'}',
                      style: const TextStyle(color: Colors.white, fontSize: 11.5),
                    ),
                  ]),
                ),
              ),
              if (!_readOnly)
                Positioned(
                  right: 4,
                  top: 4,
                  child: PopupMenuButton<String>(
                    tooltip: 'Sketch options',
                    icon: Container(
                      padding: const EdgeInsets.all(3),
                      decoration: BoxDecoration(color: Colors.black.withValues(alpha: .45), shape: BoxShape.circle),
                      child: const Icon(Icons.more_horiz, size: 18, color: Colors.white),
                    ),
                    onSelected: (v) {
                      if (v == 'open') _openSketch(b);
                      if (v == 'delete') _deleteBlock(b);
                    },
                    itemBuilder: (_) => const [
                      PopupMenuItem(value: 'open', child: Text('Open')),
                      PopupMenuItem(value: 'delete', child: Text('Delete sketch')),
                    ],
                  ),
                ),
            ]),
          ),
        ),
      ),
    );
  }
}
