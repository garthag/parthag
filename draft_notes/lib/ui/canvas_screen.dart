import 'dart:convert';
import 'dart:math' as math;

import 'package:flutter/services.dart';
import 'package:material_ui/material_ui.dart';

import '../cad/canvas_controller.dart';
import '../cad/exporters.dart';
import '../cad/painter.dart';
import '../models/drawing.dart';
import 'canvas_panels.dart';
import 'common.dart';

enum _Side { none, layers, properties }

/// Full-screen sketch editor: handwriting tools and a CAD workspace with a
/// command line, status bar toggles, layers and properties.
class CanvasScreen extends StatefulWidget {
  const CanvasScreen({
    super.key,
    required this.drawing,
    required this.title,
    required this.onChanged,
    required this.onClose,
  });

  final Drawing drawing;
  final String title;

  /// Called after each change to the drawing.
  final VoidCallback onChanged;

  /// Called when the screen closes, to save the view.
  final VoidCallback onClose;

  @override
  State<CanvasScreen> createState() => _CanvasScreenState();
}

class _CanvasScreenState extends State<CanvasScreen> {
  late final CanvasController c;
  final _cmd = TextEditingController();
  final _cmdFocus = FocusNode();
  final _canvasFocus = FocusNode(debugLabel: 'canvas');
  _Side _side = _Side.none;
  bool _penPanel = false;
  bool _logOpen = false;

  static const _groups = [
    [Tool.select, Tool.lasso, Tool.pan],
    [Tool.pen, Tool.pencil, Tool.highlighter, Tool.eraser],
    [Tool.line, Tool.polyline, Tool.rect, Tool.circle, Tool.arc, Tool.text, Tool.dimension],
    [Tool.measure, Tool.area],
  ];

  @override
  void initState() {
    super.initState();
    c = CanvasController(widget.drawing, onChanged: widget.onChanged)
      ..textPrompt = ((t, h) => showTextEntry(context, t, h))
      ..onPanel = _openPanel;
    c.addListener(_onController);
  }

  void _onController() {
    if (mounted) setState(() {});
  }

  @override
  void dispose() {
    c.removeListener(_onController);
    widget.onClose();
    c.dispose();
    _cmd.dispose();
    _cmdFocus.dispose();
    _canvasFocus.dispose();
    super.dispose();
  }

  bool get _wide => MediaQuery.sizeOf(context).width >= 760;

  void _openPanel(String name) {
    switch (name) {
      case 'LAYER':
        _toggleSide(_Side.layers);
      case 'PROPERTIES':
        _toggleSide(_Side.properties);
      case 'UNITS':
        showDrawingSettings(context, c);
    }
  }

  void _toggleSide(_Side s) {
    if (_wide) {
      setState(() => _side = _side == s ? _Side.none : s);
      return;
    }
    final theme = Theme.of(context);
    showModalBottomSheet<void>(
      context: context,
      showDragHandle: true,
      isScrollControlled: true,
      builder: (_) => Theme(
        data: theme,
        child: ConstrainedBox(
          constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(context).height * .6),
          child: SafeArea(
            child: SingleChildScrollView(child: s == _Side.layers ? LayersPanel(c: c) : PropertiesPanel(c: c)),
          ),
        ),
      ),
    );
  }

  void _pickTool(Tool t) {
    if (c.tool == t && (t.isInk || t == Tool.eraser)) {
      setState(() => _penPanel = !_penPanel);
      return;
    }
    c.setTool(t);
    _penPanel = false;
    if (t.isCad) c.say('${c.command?.name}: ${c.command?.prompt}');
  }

  void _submit(String v) {
    c.submit(v);
    _cmd.clear();
    if (_wide) _cmdFocus.requestFocus();
  }

  KeyEventResult _onCanvasKey(FocusNode node, KeyEvent e) {
    if (c.handleKey(e)) return KeyEventResult.handled;
    final kb = HardwareKeyboard.instance;
    if (e is KeyDownEvent && !kb.isControlPressed && !kb.isMetaPressed && !kb.isAltPressed) {
      final ch = e.character;
      if (ch != null && ch.length == 1 && RegExp(r'[A-Za-z0-9@#<,.\-?]').hasMatch(ch)) {
        _cmdFocus.requestFocus();
        _cmd.text += ch;
        _cmd.selection = TextSelection.collapsed(offset: _cmd.text.length);
        return KeyEventResult.handled;
      }
    }
    return KeyEventResult.ignored;
  }

  Future<void> _export(String kind) async {
    final name = widget.title.replaceAll(RegExp(r'[^\w\- ]'), '').trim().replaceAll(' ', '_');
    final file = name.isEmpty ? 'sketch' : name;
    switch (kind) {
      case 'png':
        final bytes = await drawingToPng(c.drawing);
        if (mounted) await shareBytes(context, bytes, '$file.png', 'image/png');
      case 'svg':
        await shareBytes(context, utf8.encode(drawingToSvg(c.drawing)), '$file.svg', 'image/svg+xml');
      case 'dxf':
        await shareBytes(context, utf8.encode(drawingToDxf(c.drawing)), '$file.dxf', 'application/dxf');
    }
  }

  @override
  Widget build(BuildContext context) {
    final dark = c.drawing.paper.isDark;
    return Theme(
      data: buildTheme(dark ? Brightness.dark : Brightness.light),
      child: Builder(builder: _build),
    );
  }

  Widget _build(BuildContext context) {
    final wide = _wide;
    final scheme = Theme.of(context).colorScheme;
    return Scaffold(
      backgroundColor: scheme.surfaceContainer,
      appBar: AppBar(
        backgroundColor: scheme.surfaceContainer,
        toolbarHeight: 48,
        titleSpacing: 0,
        title: Text(widget.title, overflow: TextOverflow.ellipsis, style: Theme.of(context).textTheme.titleMedium),
        actions: [
          IconButton(tooltip: 'Undo (Ctrl+Z)', icon: const Icon(Icons.undo), onPressed: c.canUndo ? c.undo : null),
          IconButton(tooltip: 'Redo (Ctrl+Y)', icon: const Icon(Icons.redo), onPressed: c.canRedo ? c.redo : null),
          if (wide) IconButton(tooltip: 'Zoom extents', icon: const Icon(Icons.fit_screen_outlined), onPressed: c.zoomExtents),
          IconButton(
            tooltip: 'Layers (LA)',
            isSelected: _side == _Side.layers,
            icon: const Icon(Icons.layers_outlined),
            onPressed: () => _toggleSide(_Side.layers),
          ),
          if (wide)
            IconButton(
              tooltip: 'Properties (PR)',
              isSelected: _side == _Side.properties,
              icon: const Icon(Icons.info_outline),
              onPressed: () => _toggleSide(_Side.properties),
            ),
          PopupMenuButton<String>(
            tooltip: 'More',
            icon: const Icon(Icons.more_vert),
            onSelected: (v) {
              switch (v) {
                case 'settings':
                  showDrawingSettings(context, c);
                case 'extents':
                  c.zoomExtents();
                case 'props':
                  _toggleSide(_Side.properties);
                case 'all':
                  c.selectAll();
                default:
                  _export(v);
              }
            },
            itemBuilder: (_) => [
              const PopupMenuItem(value: 'settings', child: ListTile(leading: Icon(Icons.tune), title: Text('Paper, units & grid'))),
              if (!wide) ...[
                const PopupMenuItem(value: 'extents', child: ListTile(leading: Icon(Icons.fit_screen_outlined), title: Text('Zoom extents'))),
                const PopupMenuItem(value: 'props', child: ListTile(leading: Icon(Icons.info_outline), title: Text('Properties'))),
              ],
              const PopupMenuItem(value: 'all', child: ListTile(leading: Icon(Icons.select_all), title: Text('Select all'))),
              const PopupMenuDivider(),
              const PopupMenuItem(value: 'png', child: ListTile(leading: Icon(Icons.image_outlined), title: Text('Share as PNG image'))),
              const PopupMenuItem(value: 'svg', child: ListTile(leading: Icon(Icons.polyline_outlined), title: Text('Share as SVG'))),
              const PopupMenuItem(value: 'dxf', child: ListTile(leading: Icon(Icons.architecture), title: Text('Share as DXF (AutoCAD)'))),
            ],
          ),
          const SizedBox(width: 4),
        ],
      ),
      body: SafeArea(
        top: false,
        child: Column(
          children: [
            Expanded(
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  if (wide) _toolRail(vertical: true),
                  Expanded(
                    child: Stack(
                      children: [
                        Positioned.fill(child: _canvas()),
                        if (_penPanel && (c.tool.isInk || c.tool == Tool.eraser))
                          Positioned(
                            top: 8,
                            left: 8,
                            child: Card(
                              elevation: 4,
                              child: SizedBox(width: math.min(320, MediaQuery.sizeOf(context).width - 16), child: PenOptions(c: c)),
                            ),
                          ),
                        if (c.selection.isNotEmpty && c.command == null)
                          Positioned(top: 8, left: 8, right: 8, child: Center(child: _selectionBar(context))),
                      ],
                    ),
                  ),
                  if (wide && _side != _Side.none)
                    Container(
                      width: 300,
                      decoration: BoxDecoration(
                        color: scheme.surfaceContainer,
                        border: Border(left: BorderSide(color: scheme.outlineVariant)),
                      ),
                      child: SingleChildScrollView(
                        child: _side == _Side.layers ? LayersPanel(c: c) : PropertiesPanel(c: c),
                      ),
                    ),
                ],
              ),
            ),
            _commandLine(context, wide),
            _statusBar(context, wide),
            if (!wide) _toolRail(vertical: false),
          ],
        ),
      ),
    );
  }

  MouseCursor get _mouseCursor {
    if (c.tool == Tool.pan) return SystemMouseCursors.grab;
    if (c.tool.isInk && c.command == null) return SystemMouseCursors.precise;
    return SystemMouseCursors.none;
  }

  Widget _canvas() {
    return LayoutBuilder(builder: (context, cons) {
      c.setSize(cons.biggest);
      return Focus(
        focusNode: _canvasFocus,
        autofocus: true,
        onKeyEvent: _onCanvasKey,
        child: MouseRegion(
          cursor: _mouseCursor,
          onExit: (_) => c.onPointerExit(),
          child: Listener(
            behavior: HitTestBehavior.opaque,
            onPointerDown: (e) {
              if (!_canvasFocus.hasFocus) _canvasFocus.requestFocus();
              if (_penPanel) setState(() => _penPanel = false);
              c.onPointerDown(e);
            },
            onPointerMove: c.onPointerMove,
            onPointerUp: c.onPointerUp,
            onPointerHover: c.onPointerHover,
            onPointerCancel: c.onPointerCancel,
            onPointerSignal: c.onPointerSignal,
            onPointerPanZoomStart: c.onPanZoomStart,
            onPointerPanZoomUpdate: c.onPanZoomUpdate,
            child: Stack(children: [
              Positioned.fill(child: RepaintBoundary(child: CustomPaint(painter: ScenePainter(c)))),
              Positioned.fill(
                child: RepaintBoundary(
                  child: CustomPaint(painter: OverlayPainter(c, accent: Theme.of(context).colorScheme.primary)),
                ),
              ),
            ]),
          ),
        ),
      );
    });
  }

  Widget _toolRail({required bool vertical}) {
    final scheme = Theme.of(context).colorScheme;
    final items = <Widget>[];
    for (var g = 0; g < _groups.length; g++) {
      if (g > 0) {
        items.add(vertical
            ? Divider(indent: 10, endIndent: 10, height: 10, color: scheme.outlineVariant)
            : VerticalDivider(indent: 12, endIndent: 12, width: 10, color: scheme.outlineVariant));
      }
      for (final t in _groups[g]) {
        items.add(_toolButton(t));
      }
    }
    final rail = vertical
        ? SingleChildScrollView(padding: const EdgeInsets.symmetric(vertical: 6), child: Column(children: items))
        : SingleChildScrollView(
            scrollDirection: Axis.horizontal,
            padding: const EdgeInsets.symmetric(horizontal: 6),
            child: Row(children: items),
          );
    return Container(
      width: vertical ? 52 : null,
      height: vertical ? null : 50,
      decoration: BoxDecoration(
        color: scheme.surfaceContainer,
        border: vertical ? Border(right: BorderSide(color: scheme.outlineVariant)) : Border(top: BorderSide(color: scheme.outlineVariant)),
      ),
      child: rail,
    );
  }

  Widget _toolButton(Tool t) {
    final scheme = Theme.of(context).colorScheme;
    final active = c.tool == t;
    final ink = t.isInk ? c.pens[t] : null;
    return Tooltip(
      message: t.isCad ? '${t.label}  (${t.command})' : t.label,
      child: Padding(
        padding: const EdgeInsets.all(2),
        child: Material(
          color: active ? scheme.primary.withValues(alpha: .18) : Colors.transparent,
          borderRadius: BorderRadius.circular(10),
          child: InkWell(
            borderRadius: BorderRadius.circular(10),
            onTap: () => _pickTool(t),
            child: SizedBox(
              width: 42,
              height: 40,
              child: Stack(alignment: Alignment.center, children: [
                ToolIcon(t, color: active ? scheme.primary : scheme.onSurface),
                if (ink != null)
                  Positioned(
                    bottom: 4,
                    child: Container(
                      width: 14,
                      height: 4,
                      decoration: BoxDecoration(color: Color(ink.color), borderRadius: BorderRadius.circular(2)),
                    ),
                  ),
              ]),
            ),
          ),
        ),
      ),
    );
  }

  Widget _selectionBar(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    Widget act(IconData icon, String label, VoidCallback onTap) => Tooltip(
          message: label,
          child: InkWell(
            borderRadius: BorderRadius.circular(10),
            onTap: onTap,
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
              child: Column(mainAxisSize: MainAxisSize.min, children: [
                Icon(icon, size: 20),
                Text(label, style: const TextStyle(fontSize: 10.5)),
              ]),
            ),
          ),
        );
    return Material(
      elevation: 4,
      color: scheme.surfaceContainerHigh,
      borderRadius: BorderRadius.circular(14),
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: Row(mainAxisSize: MainAxisSize.min, children: [
          Padding(
            padding: const EdgeInsets.only(left: 12, right: 6),
            child: Text('${c.selection.length} selected', style: const TextStyle(fontWeight: FontWeight.w600)),
          ),
          act(Icons.open_with, 'Move', () => c.startCommand('MOVE')),
          act(Icons.content_copy, 'Copy', () => c.startCommand('COPY')),
          act(Icons.rotate_right, 'Rotate', () => c.startCommand('ROTATE')),
          act(Icons.photo_size_select_small, 'Scale', () => c.startCommand('SCALE')),
          act(Icons.flip, 'Mirror', () => c.startCommand('MIRROR')),
          act(Icons.copy_all, 'Duplicate', c.duplicateSelection),
          PopupMenuButton<int>(
            tooltip: 'Colour',
            onSelected: (v) => c.editSelection((e) => e.color = v == -1 && e is! InkStroke ? null : (v == -1 ? e.color : v)),
            itemBuilder: (_) => [
              const PopupMenuItem(value: -1, child: Text('ByLayer')),
              for (final col in {...penColors, ...cadColors.skip(1)})
                PopupMenuItem(value: col, child: ColorDot(color: col, size: 22)),
            ],
            child: const Padding(
              padding: EdgeInsets.symmetric(horizontal: 8, vertical: 6),
              child: Column(mainAxisSize: MainAxisSize.min, children: [
                Icon(Icons.palette_outlined, size: 20),
                Text('Colour', style: TextStyle(fontSize: 10.5)),
              ]),
            ),
          ),
          act(Icons.info_outline, 'Properties', () => _toggleSide(_Side.properties)),
          act(Icons.delete_outline, 'Delete', c.deleteSelection),
          IconButton(tooltip: 'Clear selection', icon: const Icon(Icons.close, size: 20), onPressed: c.clearSelection),
        ]),
      ),
    );
  }

  Widget _commandLine(BuildContext context, bool wide) {
    final scheme = Theme.of(context).colorScheme;
    final dark = c.drawing.paper.isDark;
    final cmd = c.command;
    const mono = TextStyle(
      fontFamily: 'monospace',
      fontFamilyFallback: ['Consolas', 'Menlo', 'Roboto Mono', 'Courier New'],
      fontSize: 12.5,
    );
    final count = _logOpen ? 10 : (wide ? 2 : 1);
    final shown = c.log.sublist(math.max(0, c.log.length - count));
    // Fixed height, so the canvas doesn't resize as messages arrive.
    final history = [...List.filled(count - shown.length, ''), ...shown];
    final input = CallbackShortcuts(
      bindings: {
        const SingleActivator(LogicalKeyboardKey.escape): () {
          _cmd.clear();
          c.escape();
          _canvasFocus.requestFocus();
        },
        const SingleActivator(LogicalKeyboardKey.f3): c.toggleOsnap,
        const SingleActivator(LogicalKeyboardKey.f7): c.toggleGrid,
        const SingleActivator(LogicalKeyboardKey.f8): c.toggleOrtho,
        const SingleActivator(LogicalKeyboardKey.f9): c.toggleSnap,
      },
      child: TextField(
        controller: _cmd,
        focusNode: _cmdFocus,
        style: mono.copyWith(color: scheme.onSurface, fontSize: 14),
        autocorrect: false,
        enableSuggestions: false,
        textInputAction: TextInputAction.send,
        decoration: InputDecoration.collapsed(
          hintText: cmd == null ? 'Type a command: L, PL, REC, C, DIM, M, HELP…' : 'Point (x,y  @dx,dy  @d<a), number or option',
          hintStyle: mono.copyWith(color: scheme.onSurfaceVariant.withValues(alpha: .7)),
        ),
        onSubmitted: _submit,
        onChanged: (v) {
          // As in AutoCAD, Space works like Enter in the command line.
          if (v.endsWith(' ')) _submit(v);
        },
      ),
    );
    final prompt = cmd == null
        ? null
        : Text.rich(
            TextSpan(children: [
              TextSpan(text: '${cmd.name} ', style: TextStyle(color: scheme.primary, fontWeight: FontWeight.w700)),
              TextSpan(text: '${cmd.prompt}:'),
            ]),
            style: mono.copyWith(color: scheme.onSurface),
            overflow: TextOverflow.ellipsis,
          );
    final chips = <Widget>[
      if (cmd != null)
        for (final o in cmd.options)
          Padding(
            padding: const EdgeInsets.only(left: 6),
            child: ActionChip(
              visualDensity: VisualDensity.compact,
              label: Text(o.label, style: const TextStyle(fontSize: 12)),
              onPressed: () => c.submit(o.key),
            ),
          ),
      if (cmd != null) ...[
        Padding(
          padding: const EdgeInsets.only(left: 6),
          child: FilledButton.tonal(
            style: FilledButton.styleFrom(visualDensity: VisualDensity.compact, padding: const EdgeInsets.symmetric(horizontal: 12)),
            onPressed: c.enter,
            child: const Text('Done ↵'),
          ),
        ),
        IconButton(tooltip: 'Cancel (Esc)', visualDensity: VisualDensity.compact, icon: const Icon(Icons.close, size: 18), onPressed: c.escape),
      ],
    ];
    return Material(
      color: dark ? const Color(0xFF14181D) : scheme.surfaceContainerHigh,
      child: Container(
        decoration: BoxDecoration(border: Border(top: BorderSide(color: scheme.outlineVariant))),
        padding: const EdgeInsets.fromLTRB(10, 4, 4, 4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            InkWell(
              onTap: () => setState(() => _logOpen = !_logOpen),
              child: Row(children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      for (final line in history)
                        Text(line, maxLines: 1, overflow: TextOverflow.ellipsis, style: mono.copyWith(color: scheme.onSurfaceVariant)),
                    ],
                  ),
                ),
                Icon(_logOpen ? Icons.expand_more : Icons.expand_less, size: 16, color: scheme.onSurfaceVariant),
              ]),
            ),
            const SizedBox(height: 2),
            if (wide)
              Row(children: [
                Icon(Icons.chevron_right, size: 18, color: scheme.primary),
                if (prompt != null) Flexible(child: Padding(padding: const EdgeInsets.only(right: 8), child: prompt)),
                Expanded(child: input),
                ...chips,
              ])
            else ...[
              ?prompt,
              Row(children: [
                Icon(Icons.chevron_right, size: 18, color: scheme.primary),
                Expanded(child: input),
                Flexible(
                  child: SingleChildScrollView(scrollDirection: Axis.horizontal, reverse: true, child: Row(children: chips)),
                ),
              ]),
            ],
          ],
        ),
      ),
    );
  }

  Widget _statusBar(BuildContext context, bool wide) {
    final scheme = Theme.of(context).colorScheme;
    final d = c.drawing;
    const mono = TextStyle(
      fontFamily: 'monospace',
      fontFamilyFallback: ['Consolas', 'Menlo', 'Roboto Mono', 'Courier New'],
      fontSize: 11.5,
      fontFeatures: [FontFeature.tabularFigures()],
    );
    Widget toggle(String label, bool on, VoidCallback onTap, String tip) => Tooltip(
          message: tip,
          child: InkWell(
            onTap: onTap,
            borderRadius: BorderRadius.circular(6),
            child: Container(
              margin: const EdgeInsets.symmetric(horizontal: 2, vertical: 3),
              padding: const EdgeInsets.symmetric(horizontal: 7),
              alignment: Alignment.center,
              decoration: BoxDecoration(
                color: on ? scheme.primary.withValues(alpha: .22) : Colors.transparent,
                borderRadius: BorderRadius.circular(6),
              ),
              child: Text(
                label,
                style: TextStyle(
                  fontSize: 11,
                  fontWeight: FontWeight.w700,
                  letterSpacing: .4,
                  color: on ? scheme.primary : scheme.onSurfaceVariant,
                ),
              ),
            ),
          ),
        );
    return Container(
      height: 30,
      color: c.drawing.paper.isDark ? const Color(0xFF101317) : scheme.surfaceContainer,
      padding: const EdgeInsets.symmetric(horizontal: 8),
      child: Row(children: [
        SizedBox(
          width: wide ? 190 : 104,
          child: ListenableBuilder(
            listenable: c.overlay,
            builder: (_, _) {
              final w = c.cursorWorld;
              return Text(
                w == null ? '' : '${d.formatLength(w.dx)}, ${d.formatLength(w.dy)}',
                style: mono.copyWith(color: scheme.onSurfaceVariant),
                overflow: TextOverflow.fade,
                softWrap: false,
              );
            },
          ),
        ),
        Expanded(
          child: SingleChildScrollView(
            scrollDirection: Axis.horizontal,
            child: Row(children: [
              toggle('GRID', d.gridOn, c.toggleGrid, 'Grid display (F7)'),
              toggle('SNAP', d.snapOn, c.toggleSnap, 'Snap to grid (F9)'),
              toggle('ORTHO', d.orthoOn, c.toggleOrtho, 'Ortho mode (F8)'),
              toggle('OSNAP', d.osnapOn, c.toggleOsnap, 'Object snap (F3)'),
              const SizedBox(width: 6),
              InkWell(
                onTap: () => _toggleSide(_Side.layers),
                borderRadius: BorderRadius.circular(6),
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 4),
                  child: Row(children: [
                    ColorDot(color: d.currentLayer.color, size: 10, paper: d.paper),
                    const SizedBox(width: 2),
                    Text(d.currentLayer.name, style: TextStyle(fontSize: 11.5, color: scheme.onSurface)),
                  ]),
                ),
              ),
            ]),
          ),
        ),
        const SizedBox(width: 6),
        ListenableBuilder(
          listenable: c.scene,
          builder: (_, _) => Text(
            '${(c.zoom * 100).toStringAsFixed(c.zoom < .1 ? 2 : 0)}%',
            style: mono.copyWith(color: scheme.onSurfaceVariant),
          ),
        ),
        if (d.units != DrawUnits.none)
          Padding(
            padding: const EdgeInsets.only(left: 8),
            child: Text(d.units.suffix, style: mono.copyWith(color: scheme.onSurfaceVariant)),
          ),
      ]),
    );
  }
}
