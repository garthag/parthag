import 'package:material_ui/material_ui.dart';

import '../cad/canvas_controller.dart';
import '../models/drawing.dart';
import 'common.dart';

/// AutoCAD style layer manager: current layer, visibility, lock and colour.
class LayersPanel extends StatelessWidget {
  const LayersPanel({super.key, required this.c});

  final CanvasController c;

  @override
  Widget build(BuildContext context) {
    final d = c.drawing;
    final scheme = Theme.of(context).colorScheme;
    return ListenableBuilder(
      listenable: c,
      builder: (context, _) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _PanelHeader(
            title: 'Layers',
            action: IconButton(
              tooltip: 'New layer',
              icon: const Icon(Icons.add),
              onPressed: () async {
                final name = await promptText(context, title: 'New layer', hint: 'Layer name', confirm: 'Add');
                if (name != null && name.isNotEmpty) {
                  c.addLayer(name, cadColors[(d.layers.length) % cadColors.length]);
                }
              },
            ),
          ),
          for (final l in d.layers)
            Material(
              color: l.id == d.currentLayerId ? scheme.primary.withValues(alpha: .14) : Colors.transparent,
              child: InkWell(
                onTap: () => c.setCurrentLayer(l),
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                  child: Row(
                    children: [
                      SizedBox(
                        width: 20,
                        child: l.id == d.currentLayerId ? Icon(Icons.check, size: 18, color: scheme.primary) : null,
                      ),
                      IconButton(
                        visualDensity: VisualDensity.compact,
                        tooltip: l.visible ? 'Hide' : 'Show',
                        icon: Icon(l.visible ? Icons.visibility_outlined : Icons.visibility_off_outlined, size: 19),
                        onPressed: () => c.updateLayer(l, (l) => l.visible = !l.visible),
                      ),
                      IconButton(
                        visualDensity: VisualDensity.compact,
                        tooltip: l.locked ? 'Unlock' : 'Lock',
                        icon: Icon(l.locked ? Icons.lock_outline : Icons.lock_open, size: 19),
                        onPressed: () => c.updateLayer(l, (l) => l.locked = !l.locked),
                      ),
                      PopupMenuButton<int>(
                        tooltip: 'Layer colour',
                        onSelected: (v) => c.updateLayer(l, (l) => l.color = v),
                        itemBuilder: (_) => [
                          for (final col in cadColors)
                            PopupMenuItem(
                              value: col,
                              child: Row(children: [
                                ColorDot(color: col, size: 20, paper: d.paper),
                                const SizedBox(width: 8),
                                Text(col == kAdaptiveColor ? 'White / black' : '#${(col & 0xFFFFFF).toRadixString(16).toUpperCase()}'),
                              ]),
                            ),
                        ],
                        child: ColorDot(color: l.color, size: 18, paper: d.paper),
                      ),
                      const SizedBox(width: 6),
                      Expanded(
                        child: Text(
                          l.name,
                          overflow: TextOverflow.ellipsis,
                          style: TextStyle(color: l.visible ? null : scheme.onSurfaceVariant),
                        ),
                      ),
                      Text(
                        '${d.entities.where((e) => e.layerId == l.id).length}',
                        style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 12),
                      ),
                      PopupMenuButton<String>(
                        icon: const Icon(Icons.more_horiz, size: 18),
                        onSelected: (v) async {
                          if (v == 'rename') {
                            final name = await promptText(context, title: 'Rename layer', initial: l.name, confirm: 'Rename');
                            if (name != null && name.isNotEmpty) c.updateLayer(l, (l) => l.name = name);
                          } else if (v == 'delete') {
                            c.deleteLayer(l);
                          } else if (v == 'select') {
                            c.selection
                              ..clear()
                              ..addAll(d.entities.where((e) => e.layerId == l.id && c.editable(e)).map((e) => e.id));
                            c.refresh();
                          }
                        },
                        itemBuilder: (_) => [
                          const PopupMenuItem(value: 'rename', child: Text('Rename')),
                          const PopupMenuItem(value: 'select', child: Text('Select objects on layer')),
                          if (l.id != d.layers.first.id)
                            const PopupMenuItem(value: 'delete', child: Text('Delete (objects move to 0)')),
                        ],
                      ),
                    ],
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

/// Properties palette: layer, colour, line weight and measurements of the
/// selection, or drawing settings when nothing is selected.
class PropertiesPanel extends StatelessWidget {
  const PropertiesPanel({super.key, required this.c});

  final CanvasController c;

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: c,
      builder: (context, _) {
        final d = c.drawing;
        final scheme = Theme.of(context).colorScheme;
        final sel = c.selectedEntities;
        final label = TextStyle(color: scheme.onSurfaceVariant, fontSize: 12);
        String len(double v) => d.formatLength(v, withUnits: true);
        if (sel.isEmpty) {
          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const _PanelHeader(title: 'Properties'),
              Padding(
                padding: const EdgeInsets.all(12),
                child: Text('Nothing selected.\nTap an object, or drag a window to select.', style: label),
              ),
              _row('Objects', '${d.entities.length}', label),
              _row('Layers', '${d.layers.length}', label),
              _row('Paper', d.paper.label, label),
              _row('Units', d.units == DrawUnits.none ? 'Unitless' : d.units.suffix, label),
              _row('Grid', d.formatLength(d.gridSize), label),
              if (d.extents case final r?) ...[
                _row('Extents W', d.formatLength(r.width), label),
                _row('Extents H', d.formatLength(r.height), label),
              ],
            ],
          );
        }
        final first = sel.first;
        final sameType = sel.every((e) => e.type == first.type);
        final layers = {for (final e in sel) e.layerId};
        final colors = {for (final e in sel) e.color};
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            _PanelHeader(title: sel.length == 1 ? first.label : '${sel.length} objects${sameType ? ' (${first.label})' : ''}'),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
              child: Row(children: [
                SizedBox(width: 72, child: Text('Layer', style: label)),
                Expanded(
                  child: DropdownButton<String>(
                    isExpanded: true,
                    isDense: true,
                    value: layers.length == 1 ? layers.first : null,
                    hint: const Text('Varies'),
                    items: [for (final l in d.layers) DropdownMenuItem(value: l.id, child: Text(l.name))],
                    onChanged: (v) => v == null ? null : c.editSelection((e) => e.layerId = v),
                  ),
                ),
              ]),
            ),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
              child: Row(children: [
                SizedBox(width: 72, child: Text('Colour', style: label)),
                Expanded(
                  child: Wrap(children: [
                    if (first is! InkStroke)
                      _ByLayerChip(
                        selected: colors.length == 1 && colors.first == null,
                        onTap: () => c.editSelection((e) {
                          if (e is! InkStroke) e.color = null;
                        }),
                      ),
                    for (final col in [...cadColors.skip(1), ...penColors.take(1)])
                      ColorDot(
                        color: col,
                        size: 20,
                        paper: d.paper,
                        selected: colors.length == 1 && colors.first == col,
                        onTap: () => c.editSelection((e) => e.color = col),
                      ),
                  ]),
                ),
              ]),
            ),
            if (sel.every((e) => e is! InkStroke && e is! TextEnt && e is! DimEnt))
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 12),
                child: Row(children: [
                  SizedBox(width: 72, child: Text('Weight', style: label)),
                  Expanded(
                    child: Slider(
                      value: first.weight.clamp(.5, 6),
                      min: .5,
                      max: 6,
                      divisions: 11,
                      label: first.weight.toStringAsFixed(1),
                      onChanged: (v) {
                        for (final e in sel) {
                          e.weight = v;
                        }
                        c.refresh();
                      },
                      onChangeEnd: (v) => c.editSelection((e) => e.weight = v),
                    ),
                  ),
                ]),
              ),
            if (sel.length == 1 && first is TextEnt)
              Padding(
                padding: const EdgeInsets.all(12),
                child: OutlinedButton.icon(
                  icon: const Icon(Icons.edit_outlined, size: 18),
                  label: const Text('Edit text'),
                  onPressed: () async {
                    final r = await c.askText(first.text, first.height);
                    if (r != null && r.$1.trim().isNotEmpty) {
                      c.editSelection((e) {
                        if (e is TextEnt) {
                          e.text = r.$1;
                          e.height = r.$2;
                        }
                      });
                    }
                  },
                ),
              ),
            if (sel.length == 1) for (final (k, v) in first.describe(len)) _row(k, v, label),
            if (sel.length > 1 && sameType && first is PolyEnt)
              _row('Total length', len(sel.whereType<PolyEnt>().fold(0.0, (s, e) => s + e.segments.fold(0.0, (t, g) => t + (g.$2 - g.$1).distance))), label),
            if (sel.length > 1 && sameType && first is LineEnt)
              _row('Total length', len(sel.whereType<LineEnt>().fold(0.0, (s, e) => s + e.length)), label),
          ],
        );
      },
    );
  }

  Widget _row(String k, String v, TextStyle label) => Padding(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 5),
        child: Row(children: [
          SizedBox(width: 96, child: Text(k, style: label)),
          Expanded(child: SelectableText(v, style: const TextStyle(fontFeatures: [FontFeature.tabularFigures()]))),
        ]),
      );
}

class _ByLayerChip extends StatelessWidget {
  const _ByLayerChip({required this.selected, required this.onTap});

  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(right: 4),
        child: ChoiceChip(
          label: const Text('ByLayer', style: TextStyle(fontSize: 11)),
          visualDensity: VisualDensity.compact,
          selected: selected,
          onSelected: (_) => onTap(),
        ),
      );
}

class _PanelHeader extends StatelessWidget {
  const _PanelHeader({required this.title, this.action});

  final String title;
  final Widget? action;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.fromLTRB(12, 8, 4, 4),
        child: Row(children: [
          Expanded(child: Text(title, style: Theme.of(context).textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w700))),
          ?action,
        ]),
      );
}

/// Samsung Notes style pen settings: colour swatches and thickness.
class PenOptions extends StatelessWidget {
  const PenOptions({super.key, required this.c});

  final CanvasController c;

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: c,
      builder: (context, _) {
        final t = c.tool;
        final scheme = Theme.of(context).colorScheme;
        final children = <Widget>[];
        if (t.isInk) {
          final pen = c.pens[t]!;
          final colors = t == Tool.highlighter ? highlighterColors : penColors;
          children.addAll([
            Text(t.label, style: const TextStyle(fontWeight: FontWeight.w700)),
            const SizedBox(height: 6),
            Wrap(children: [
              for (final col in colors)
                ColorDot(
                  color: col,
                  size: 28,
                  selected: pen.color == col,
                  onTap: () {
                    pen.color = col;
                    c.refresh();
                  },
                ),
            ]),
            Row(children: [
              const Text('Size'),
              Expanded(
                child: Slider(
                  value: pen.width,
                  min: t == Tool.highlighter ? 6 : .5,
                  max: t == Tool.highlighter ? 48 : 24,
                  onChanged: (v) {
                    pen.width = v;
                    c.refresh();
                  },
                ),
              ),
              SizedBox(
                width: 40,
                height: 40,
                child: Center(
                  child: Container(
                    width: pen.width.clamp(2, 36),
                    height: pen.width.clamp(2, 36),
                    decoration: BoxDecoration(
                      color: Color(pen.color).withValues(alpha: t == Tool.highlighter ? .5 : 1),
                      shape: BoxShape.circle,
                      border: Border.all(color: scheme.outlineVariant),
                    ),
                  ),
                ),
              ),
            ]),
          ]);
        } else if (t == Tool.eraser) {
          children.addAll([
            const Text('Eraser', style: TextStyle(fontWeight: FontWeight.w700)),
            const SizedBox(height: 6),
            SegmentedButton<bool>(
              segments: const [
                ButtonSegment(value: false, label: Text('Erase by area')),
                ButtonSegment(value: true, label: Text('Erase by stroke')),
              ],
              selected: {c.eraseWholeStrokes},
              onSelectionChanged: (s) {
                c.eraseWholeStrokes = s.first;
                c.refresh();
              },
            ),
            Row(children: [
              const Text('Size'),
              Expanded(
                child: Slider(
                  value: c.eraserSize,
                  min: 8,
                  max: 80,
                  onChanged: (v) {
                    c.eraserSize = v;
                    c.refresh();
                  },
                ),
              ),
            ]),
            TextButton.icon(
              icon: const Icon(Icons.delete_sweep_outlined),
              label: const Text('Erase all handwriting'),
              onPressed: () {
                c.selection
                  ..clear()
                  ..addAll(c.drawing.entities.whereType<InkStroke>().where(c.editable).map((e) => e.id));
                c.deleteSelection();
              },
            ),
          ]);
        }
        children.add(SwitchListTile(
          contentPadding: EdgeInsets.zero,
          dense: true,
          title: const Text('Draw with stylus only'),
          subtitle: const Text('Fingers scroll and zoom'),
          value: c.stylusOnly,
          onChanged: (v) {
            c.stylusOnly = v;
            c.refresh();
          },
        ));
        return Padding(
          padding: const EdgeInsets.fromLTRB(16, 12, 16, 8),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: children),
        );
      },
    );
  }
}

/// Paper, units, precision, grid and text height.
Future<void> showDrawingSettings(BuildContext context, CanvasController c) {
  final d = c.drawing;
  final grid = TextEditingController(text: d.formatLength(d.gridSize));
  final textH = TextEditingController(text: d.formatLength(d.textHeight));
  return showDialog<void>(
    context: context,
    builder: (ctx) => StatefulBuilder(
      builder: (ctx, set) => AlertDialog(
        title: const Text('Drawing settings'),
        content: SizedBox(
          width: 380,
          child: SingleChildScrollView(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                const Text('Paper'),
                const SizedBox(height: 6),
                Wrap(
                  spacing: 6,
                  runSpacing: 6,
                  children: [
                    for (final p in PaperStyle.values)
                      ChoiceChip(
                        avatar: Container(
                          width: 14,
                          height: 14,
                          decoration: BoxDecoration(color: p.background, border: Border.all(color: Colors.grey), shape: BoxShape.circle),
                        ),
                        label: Text(p.label),
                        selected: d.paper == p,
                        onSelected: (_) {
                          c.setPaper(p);
                          set(() {});
                        },
                      ),
                  ],
                ),
                const SizedBox(height: 16),
                Row(children: [
                  Expanded(
                    child: DropdownButtonFormField<DrawUnits>(
                      initialValue: d.units,
                      decoration: const InputDecoration(labelText: 'Units'),
                      items: [
                        for (final u in DrawUnits.values)
                          DropdownMenuItem(value: u, child: Text(u == DrawUnits.none ? 'Unitless' : u.suffix)),
                      ],
                      onChanged: (u) => d.units = u ?? d.units,
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: DropdownButtonFormField<int>(
                      initialValue: d.precision,
                      decoration: const InputDecoration(labelText: 'Precision'),
                      items: [for (var i = 0; i <= 4; i++) DropdownMenuItem(value: i, child: Text(i == 0 ? '0' : '0.${'0' * i}'))],
                      onChanged: (v) => d.precision = v ?? d.precision,
                    ),
                  ),
                ]),
                const SizedBox(height: 12),
                Row(children: [
                  Expanded(
                    child: TextField(
                      controller: grid,
                      keyboardType: const TextInputType.numberWithOptions(decimal: true),
                      decoration: const InputDecoration(labelText: 'Grid and snap spacing'),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: TextField(
                      controller: textH,
                      keyboardType: const TextInputType.numberWithOptions(decimal: true),
                      decoration: const InputDecoration(labelText: 'Text height'),
                    ),
                  ),
                ]),
              ],
            ),
          ),
        ),
        actions: [
          FilledButton(
            onPressed: () {
              final g = double.tryParse(grid.text);
              if (g != null && g > 0) d.gridSize = g;
              final h = double.tryParse(textH.text);
              if (h != null && h > 0) d.textHeight = h;
              c.refresh();
              c.onChanged?.call();
              Navigator.pop(ctx);
            },
            child: const Text('Done'),
          ),
        ],
      ),
    ),
  );
}

/// Text entry for TEXT objects, with the height in drawing units.
Future<(String, double)?> showTextEntry(BuildContext context, String text, double height) {
  final t = TextEditingController(text: text);
  final h = TextEditingController(text: _trim(height));
  return showDialog<(String, double)>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: Text(text.isEmpty ? 'Add text' : 'Edit text'),
      content: SizedBox(
        width: 360,
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          TextField(
            controller: t,
            autofocus: true,
            minLines: 1,
            maxLines: 5,
            decoration: const InputDecoration(labelText: 'Text'),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: h,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: const InputDecoration(labelText: 'Height (drawing units)'),
          ),
        ]),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Cancel')),
        FilledButton(
          onPressed: () => Navigator.pop(ctx, (t.text, double.tryParse(h.text) ?? height)),
          child: const Text('OK'),
        ),
      ],
    ),
  );
}

String _trim(double v) => v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(2);
