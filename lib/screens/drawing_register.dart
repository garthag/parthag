import 'package:file_picker/file_picker.dart' show PlatformFile;
import 'package:flutter/material.dart';
import 'package:printing/printing.dart';
import 'package:provider/provider.dart';
import 'package:share_plus/share_plus.dart';

import '../models/drawing.dart';
import '../models/models.dart';
import '../services/drawing_register_export.dart';
import '../services/drawing_service.dart';
import '../state/app_state.dart';
import '../widgets/common.dart';
import 'drawing_viewer.dart';

String plantTitle(String plant) => DrawingRegisterExport.plantLabel(plant);
String unitTitle(String unit) => DrawingRegisterExport.unitLabel(unit);

/// Colour used for each drawing type chip / header so a type is recognisable
/// at a glance across plants and units.
Color typeColor(DrawingType t) => switch (t.code) {
      'GN' => const Color(0xFF607D8B),
      'PR' => const Color(0xFF00897B),
      'CV' => const Color(0xFF8D6E63),
      'ST' => const Color(0xFF5E35B1),
      'AR' => const Color(0xFFD81B60),
      'ME' => const Color(0xFF3949AB),
      'PP' => const Color(0xFF1E88E5),
      'EL' => const Color(0xFFF9A825),
      'IN' => const Color(0xFF43A047),
      'HV' => const Color(0xFF00ACC1),
      'FP' => const Color(0xFFE53935),
      'PL' => const Color(0xFF6D4C41),
      _ => const Color(0xFF9E9E9E),
    };

IconData formatIcon(DrawingFormat f) => switch (f) {
      DrawingFormat.pdf => Icons.picture_as_pdf_outlined,
      DrawingFormat.image => Icons.image_outlined,
      DrawingFormat.cad => Icons.architecture,
      DrawingFormat.other => Icons.insert_drive_file_outlined,
    };

/// The "Drawings" tab of a project: Plant → Unit → Type → Drawing.
class DrawingRegisterTab extends StatefulWidget {
  const DrawingRegisterTab({super.key, required this.project});

  final Project project;

  @override
  State<DrawingRegisterTab> createState() => _DrawingRegisterTabState();
}

class _DrawingRegisterTabState extends State<DrawingRegisterTab> {
  String _query = '';
  String? _typeFilter;
  final Set<String> _collapsed = {};

  bool _matches(Drawing d) {
    if (_typeFilter != null && d.typeCode != _typeFilter) return false;
    if (_query.isEmpty) return true;
    final q = _query.toLowerCase();
    return [d.number, d.title, d.kind, d.plant, d.unit, d.fileName, d.type.name, d.revision]
        .any((s) => s.toLowerCase().contains(q));
  }

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final all = state.drawingsFor(widget.project.id);
    if (all.isEmpty) {
      return EmptyState(
        icon: Icons.folder_zip_outlined,
        title: 'No drawings yet',
        message: 'Import the drawing ZIP received from the consultant. The app unzips it and files every '
            'drawing by Plant → Unit → Drawing type, keeping the latest revision on top.\n\n'
            'Works best with numbers like RF-100-ST-GA-0001_R2.pdf or folders like Plant-1/Unit-100/Piping/.',
        action: FilledButton.icon(
          onPressed: () => importDrawings(context, widget.project),
          icon: const Icon(Icons.unarchive_outlined),
          label: const Text('Import drawing ZIP'),
        ),
      );
    }

    final allGroups = organiseDrawings(all);
    final currentCount = allGroups.fold(0, (s, g) => s + g.count);
    final typeCounts = <String, int>{};
    for (final g in allGroups) {
      for (final u in g.units) {
        for (final (t, e) in u.types) {
          typeCounts[t.code] = (typeCounts[t.code] ?? 0) + e.length;
        }
      }
    }
    final filtered = all.where(_matches).toList();
    // Group the matches but keep each drawing's full revision history.
    final keys = filtered.map((d) => d.registerKey).toSet();
    final groups = organiseDrawings(all.where((d) => keys.contains(d.registerKey)));
    final rows = _flatten(groups);
    final sequence = [
      for (final r in rows)
        if (r is _DrawingRow) r.entry.current.id,
    ];

    final t = Theme.of(context);
    return CustomScrollView(
      slivers: [
        SliverToBoxAdapter(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(12, 12, 12, 0),
            child: Card(
              child: Padding(
                padding: const EdgeInsets.all(12),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Expanded(child: Text('Drawing register', style: t.textTheme.titleMedium)),
                        PopupMenuButton<String>(
                          tooltip: 'Export register',
                          icon: const Icon(Icons.ios_share),
                          onSelected: (v) => _export(context, v, all),
                          itemBuilder: (_) => const [
                            PopupMenuItem(
                                value: 'pdf',
                                child: ListTile(
                                    leading: Icon(Icons.picture_as_pdf_outlined),
                                    title: Text('Register PDF (print / share)'))),
                            PopupMenuItem(
                                value: 'csv',
                                child: ListTile(
                                    leading: Icon(Icons.table_chart_outlined), title: Text('Register CSV (Excel)'))),
                          ],
                        ),
                      ],
                    ),
                    const SizedBox(height: 4),
                    Wrap(
                      spacing: 16,
                      runSpacing: 4,
                      children: [
                        _stat(context, '$currentCount', 'drawings'),
                        _stat(context, '${allGroups.length}', 'plants'),
                        _stat(context, '${allGroups.fold(0, (s, g) => s + g.units.length)}', 'units'),
                        _stat(context, '${typeCounts.length}', 'types'),
                        _stat(context, '${all.length - currentCount}', 'superseded revs'),
                      ],
                    ),
                    const SizedBox(height: 12),
                    TextField(
                      decoration: const InputDecoration(
                        prefixIcon: Icon(Icons.search),
                        hintText: 'Search drawing no., title, unit, revision…',
                      ),
                      onChanged: (v) => setState(() => _query = v.trim()),
                    ),
                    const SizedBox(height: 8),
                    SingleChildScrollView(
                      scrollDirection: Axis.horizontal,
                      child: Row(
                        children: [
                          ChoiceChip(
                            label: Text('All $currentCount'),
                            selected: _typeFilter == null,
                            onSelected: (_) => setState(() => _typeFilter = null),
                          ),
                          for (final type in DrawingType.all)
                            if (typeCounts[type.code] != null)
                              Padding(
                                padding: const EdgeInsets.only(left: 6),
                                child: ChoiceChip(
                                  avatar: CircleAvatar(backgroundColor: typeColor(type), radius: 6),
                                  label: Text('${type.code} ${typeCounts[type.code]}'),
                                  tooltip: type.name,
                                  selected: _typeFilter == type.code,
                                  onSelected: (s) => setState(() => _typeFilter = s ? type.code : null),
                                ),
                              ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
        if (rows.isEmpty)
          const SliverToBoxAdapter(
            child: Padding(padding: EdgeInsets.all(32), child: Center(child: Text('No drawing matches the filter.'))),
          ),
        SliverPadding(
          padding: const EdgeInsets.fromLTRB(12, 8, 12, 96),
          sliver: SliverList.builder(
            itemCount: rows.length,
            itemBuilder: (context, i) => rows[i].build(context, this, sequence),
          ),
        ),
      ],
    );
  }

  Widget _stat(BuildContext context, String value, String label) {
    final t = Theme.of(context);
    if (value == '1') label = label.replaceFirst(RegExp(r's$'), '');
    return Text.rich(TextSpan(children: [
      TextSpan(text: value, style: t.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.bold)),
      TextSpan(text: ' $label', style: t.textTheme.bodySmall),
    ]));
  }

  void _toggle(String key) => setState(() => _collapsed.contains(key) ? _collapsed.remove(key) : _collapsed.add(key));

  List<_Row> _flatten(List<PlantGroup> groups) {
    final rows = <_Row>[];
    for (final g in groups) {
      final pk = 'p|${g.plant}';
      rows.add(_PlantRow(g, pk, _collapsed.contains(pk)));
      if (_collapsed.contains(pk)) continue;
      for (final u in g.units) {
        final uk = '$pk|u|${u.unit}';
        rows.add(_UnitRow(u, uk, _collapsed.contains(uk)));
        if (_collapsed.contains(uk)) continue;
        for (final (type, entries) in u.types) {
          final tk = '$uk|t|${type.code}';
          rows.add(_TypeRow(type, entries.length, tk, _collapsed.contains(tk)));
          if (_collapsed.contains(tk)) continue;
          for (final e in entries) {
            rows.add(_DrawingRow(e));
          }
        }
      }
    }
    return rows;
  }

  Future<void> _export(BuildContext context, String kind, List<Drawing> all) async {
    final p = widget.project;
    final safe = p.name.replaceAll(RegExp(r'[^A-Za-z0-9]+'), '_');
    final messenger = ScaffoldMessenger.of(context);
    try {
      if (kind == 'pdf') {
        final bytes = await DrawingRegisterExport.pdf(p, all);
        await Printing.sharePdf(bytes: bytes, filename: 'Drawing_Register_$safe.pdf');
      } else {
        final bytes = DrawingRegisterExport.csvBytes(all);
        await SharePlus.instance.share(ShareParams(
          files: [XFile.fromData(bytes, mimeType: 'text/csv', name: 'Drawing_Register_$safe.csv')],
          fileNameOverrides: ['Drawing_Register_$safe.csv'],
          subject: 'Drawing register – ${p.name}',
        ));
      }
    } catch (e) {
      messenger.showSnackBar(SnackBar(content: Text('Export failed: $e')));
    }
  }
}

abstract class _Row {
  Widget build(BuildContext context, _DrawingRegisterTabState s, List<String> sequence);
}

class _PlantRow extends _Row {
  _PlantRow(this.g, this.k, this.collapsed);
  final PlantGroup g;
  final String k;
  final bool collapsed;

  @override
  Widget build(BuildContext context, _DrawingRegisterTabState s, List<String> sequence) {
    final t = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.only(top: 8),
      child: Material(
        color: t.colorScheme.primary,
        borderRadius: BorderRadius.circular(10),
        child: InkWell(
          borderRadius: BorderRadius.circular(10),
          onTap: () => s._toggle(k),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
            child: Row(
              children: [
                Icon(Icons.factory_outlined, color: t.colorScheme.onPrimary),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(plantTitle(g.plant),
                      style: t.textTheme.titleMedium
                          ?.copyWith(color: t.colorScheme.onPrimary, fontWeight: FontWeight.bold)),
                ),
                Text('${g.units.length} units · ${g.count}',
                    style: t.textTheme.bodySmall?.copyWith(color: t.colorScheme.onPrimary)),
                Icon(collapsed ? Icons.expand_more : Icons.expand_less, color: t.colorScheme.onPrimary),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _UnitRow extends _Row {
  _UnitRow(this.u, this.k, this.collapsed);
  final UnitGroup u;
  final String k;
  final bool collapsed;

  @override
  Widget build(BuildContext context, _DrawingRegisterTabState s, List<String> sequence) {
    final t = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.only(top: 6, left: 8),
      child: Material(
        color: t.colorScheme.primaryContainer,
        borderRadius: BorderRadius.circular(8),
        child: InkWell(
          borderRadius: BorderRadius.circular(8),
          onTap: () => s._toggle(k),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
            child: Row(
              children: [
                Icon(Icons.account_tree_outlined, size: 20, color: t.colorScheme.onPrimaryContainer),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(unitTitle(u.unit),
                      style: t.textTheme.titleSmall?.copyWith(
                          color: t.colorScheme.onPrimaryContainer, fontWeight: FontWeight.bold)),
                ),
                Text('${u.types.length} types · ${u.count}',
                    style: t.textTheme.bodySmall?.copyWith(color: t.colorScheme.onPrimaryContainer)),
                Icon(collapsed ? Icons.expand_more : Icons.expand_less,
                    size: 20, color: t.colorScheme.onPrimaryContainer),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _TypeRow extends _Row {
  _TypeRow(this.type, this.count, this.k, this.collapsed);
  final DrawingType type;
  final int count;
  final String k;
  final bool collapsed;

  @override
  Widget build(BuildContext context, _DrawingRegisterTabState s, List<String> sequence) {
    final t = Theme.of(context);
    final c = typeColor(type);
    return Padding(
      padding: const EdgeInsets.only(top: 6, left: 16),
      child: InkWell(
        onTap: () => s._toggle(k),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
          decoration: BoxDecoration(
            border: Border(left: BorderSide(color: c, width: 4)),
            color: c.withValues(alpha: .08),
          ),
          child: Row(
            children: [
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                decoration: BoxDecoration(color: c, borderRadius: BorderRadius.circular(4)),
                child: Text(type.code,
                    style: t.textTheme.labelSmall?.copyWith(color: Colors.white, fontWeight: FontWeight.bold)),
              ),
              const SizedBox(width: 8),
              Expanded(child: Text(type.name, style: t.textTheme.labelLarge)),
              Text('$count', style: t.textTheme.labelLarge),
              Icon(collapsed ? Icons.expand_more : Icons.expand_less, size: 18),
            ],
          ),
        ),
      ),
    );
  }
}

class _DrawingRow extends _Row {
  _DrawingRow(this.entry);
  final DrawingEntry entry;

  @override
  Widget build(BuildContext context, _DrawingRegisterTabState s, List<String> sequence) {
    final d = entry.current;
    final t = Theme.of(context);
    final c = typeColor(d.type);
    return Padding(
      padding: const EdgeInsets.only(left: 16),
      child: Card(
        margin: const EdgeInsets.only(top: 4),
        child: ListTile(
          dense: true,
          contentPadding: const EdgeInsets.only(left: 10, right: 4),
          leading: Icon(formatIcon(d.format), color: c),
          minLeadingWidth: 24,
          title: Text(d.number,
              style: t.textTheme.bodyMedium?.copyWith(
                  fontWeight: FontWeight.bold, fontFeatures: const [FontFeature.tabularFigures()])),
          subtitle: Text(
            [d.displayTitle, if (entry.superseded.isNotEmpty) 'earlier: ${entry.superseded.map((x) => revLabel(x.revision)).join(', ')}']
                .join('\n'),
            maxLines: 3,
            overflow: TextOverflow.ellipsis,
          ),
          trailing: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              RevBadge(d),
              PopupMenuButton<String>(
                onSelected: (v) => drawingAction(context, d, v),
                itemBuilder: (_) => const [
                  PopupMenuItem(value: 'view', child: Text('View drawing')),
                  PopupMenuItem(value: 'edit', child: Text('Edit details')),
                  PopupMenuItem(value: 'share', child: Text('Share file')),
                  PopupMenuItem(value: 'delete', child: Text('Delete (all revisions)')),
                ],
              ),
            ],
          ),
          onTap: () => openDrawing(context, d.id, sequence: sequence),
        ),
      ),
    );
  }
}

/// Revision + status badge, e.g. "R2 · IFC".
class RevBadge extends StatelessWidget {
  const RevBadge(this.d, {super.key, this.superseded = false});
  final Drawing d;
  final bool superseded;

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    final color = superseded
        ? Colors.grey
        : switch (d.status) {
            DrawingStatus.forConstruction => Colors.green.shade700,
            DrawingStatus.asBuilt => Colors.blue.shade700,
            DrawingStatus.forApproval || DrawingStatus.forReview => Colors.orange.shade800,
            DrawingStatus.forInformation => Colors.blueGrey,
          };
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 3),
      decoration: BoxDecoration(
        border: Border.all(color: color),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(revLabel(d.revision),
              style: t.textTheme.labelMedium?.copyWith(color: color, fontWeight: FontWeight.bold)),
          Text(superseded ? 'OLD' : d.status.code, style: t.textTheme.labelSmall?.copyWith(color: color, fontSize: 9)),
        ],
      ),
    );
  }
}

/// "R2" for numbered revisions, "Rev A" for lettered ones.
String revLabel(String rev) => int.tryParse(rev) != null ? 'R$rev' : 'Rev $rev';

void openDrawing(BuildContext context, String id, {List<String> sequence = const []}) => Navigator.push(
    context, MaterialPageRoute(builder: (_) => DrawingViewerScreen(drawingId: id, sequence: sequence)));

Future<void> drawingAction(BuildContext context, Drawing d, String action) async {
  final state = context.read<AppState>();
  switch (action) {
    case 'view':
      openDrawing(context, d.id);
    case 'edit':
      await editDrawing(context, d);
    case 'share':
      await shareDrawing(context, d);
    case 'delete':
      final revs = state.revisionsOf(d);
      final ok = await confirm(context, 'Delete ${d.number}?',
          'This removes ${revs.length} revision file(s) of this drawing from the register and this device.');
      if (ok) await state.deleteDrawings(revs);
    case 'delete-one':
      final ok = await confirm(context, 'Delete Rev ${d.revision}?',
          'Removes only revision ${d.revision} of ${d.number} from this device.');
      if (ok) await state.deleteDrawings([d]);
  }
}

Future<void> shareDrawing(BuildContext context, Drawing d) async {
  final messenger = ScaffoldMessenger.of(context);
  final bytes = await context.read<DrawingService>().readBytes(d);
  if (bytes == null) {
    messenger.showSnackBar(const SnackBar(content: Text('File is not available on this device.')));
    return;
  }
  await SharePlus.instance.share(ShareParams(
    files: [XFile.fromData(bytes, name: d.fileName)],
    fileNameOverrides: [d.fileName],
    text: '${d.number} Rev ${d.revision} – ${d.displayTitle}',
  ));
}

/// Unzips and files the chosen package(s), then reports what was found.
Future<void> importDrawings(BuildContext context, Project project) async {
  final service = context.read<DrawingService>();
  final state = context.read<AppState>();
  final messenger = ScaffoldMessenger.of(context);

  late final List<PlatformFile> files;
  try {
    files = await service.pick();
  } catch (e) {
    messenger.showSnackBar(SnackBar(content: Text('Could not open file picker: $e')));
    return;
  }
  if (files.isEmpty || !context.mounted) return;

  final zip = files.where((f) => f.name.toLowerCase().endsWith('.zip')).firstOrNull;
  final plantCtrl = TextEditingController(text: zip == null ? '' : plantNameFromZip(zip.name));
  final go = await showDialog<bool>(
    context: context,
    builder: (c) => AlertDialog(
      title: Text('Import ${files.length} file(s)'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Text('Drawings are filed by Plant → Unit → Drawing type → Drawing no. Plant and unit are read '
              'from folder names (Plant-1, Unit-100 …) and drawing numbers (…-100-ST-…, U200-…).'),
          const SizedBox(height: 12),
          TextField(
            controller: plantCtrl,
            decoration: const InputDecoration(
              labelText: 'Plant name (when not in folder / number)',
              hintText: 'e.g. Crude Distillation Plant',
            ),
          ),
        ],
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Cancel')),
        FilledButton.icon(
            onPressed: () => Navigator.pop(c, true),
            icon: const Icon(Icons.unarchive_outlined),
            label: const Text('Unzip & file')),
      ],
    ),
  );
  if (go != true || !context.mounted) return;

  final progress = ValueNotifier<(int, int, String)>((0, 0, 'Opening…'));
  final nav = Navigator.of(context);
  showDialog<void>(
    context: context,
    barrierDismissible: false,
    builder: (c) => PopScope(
      canPop: false,
      child: AlertDialog(
        title: const Text('Unzipping drawings'),
        content: ValueListenableBuilder(
          valueListenable: progress,
          builder: (_, v, _) => Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              LinearProgressIndicator(value: v.$2 == 0 ? null : v.$1 / v.$2),
              const SizedBox(height: 8),
              Text(v.$2 == 0 ? 'Reading archive…' : '${v.$1} / ${v.$2}'),
              Text(v.$3, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12)),
            ],
          ),
        ),
      ),
    ),
  );

  DrawingImportResult? result;
  Object? error;
  try {
    // Let the dialog paint before the archive is read.
    await Future<void>.delayed(const Duration(milliseconds: 50));
    result = await service.importPicked(
      files,
      projectId: project.id,
      defaultPlant: plantCtrl.text.trim(),
      existing: state.drawingsFor(project.id),
      onProgress: (d, t, n) => progress.value = (d, t, n),
    );
    await state.addDrawings(result.added);
  } catch (e) {
    error = e;
  }
  nav.pop();
  if (!context.mounted) return;
  if (error != null || result == null) {
    messenger.showSnackBar(SnackBar(content: Text('Import failed: $error')));
    return;
  }
  final r = result;
  await showDialog<void>(
    context: context,
    builder: (c) => AlertDialog(
      icon: Icon(r.added.isEmpty ? Icons.info_outline : Icons.check_circle_outline,
          color: r.added.isEmpty ? Colors.orange : Colors.green),
      title: Text(r.added.isEmpty ? 'Nothing new imported' : '${r.added.length} drawing files filed'),
      content: SingleChildScrollView(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            if (r.added.isNotEmpty) ...[
              Text('${r.plants.length} plant(s) · ${r.units.length} unit(s) · ${r.types.length} drawing type(s)'),
              const SizedBox(height: 8),
              for (final t in DrawingType.all)
                if (r.types.contains(t.code))
                  Text('• ${t.label}: ${r.added.where((d) => d.typeCode == t.code).length}'),
            ],
            if (r.unclassified > 0) ...[
              const SizedBox(height: 8),
              Text('${r.unclassified} file(s) could not be classified – they are under "OT – Other". '
                  'Use "Edit details" to set the type.'),
            ],
            if (r.duplicates.isNotEmpty) ...[
              const SizedBox(height: 8),
              Text('${r.duplicates.length} file(s) already in the register were skipped.'),
            ],
            if (r.junkSkipped > 0) Text('${r.junkSkipped} system file(s) ignored.'),
            if (r.errors.isNotEmpty) ...[
              const SizedBox(height: 8),
              Text('${r.errors.length} error(s):', style: const TextStyle(color: Colors.red)),
              for (final e in r.errors.take(5)) Text(e, style: const TextStyle(fontSize: 12)),
            ],
          ],
        ),
      ),
      actions: [FilledButton(onPressed: () => Navigator.pop(c), child: const Text('OK'))],
    ),
  );
}

/// Edits the register details of one revision.
Future<void> editDrawing(BuildContext context, Drawing d) async {
  final state = context.read<AppState>();
  final project = state.drawingsFor(d.projectId);
  final plants = project.map((x) => x.plant).where((x) => x.isNotEmpty).toSet().toList()..sort(naturalCompare);
  final units = project.map((x) => x.unit).where((x) => x.isNotEmpty).toSet().toList()..sort(naturalCompare);

  final number = TextEditingController(text: d.number);
  final title = TextEditingController(text: d.title);
  final rev = TextEditingController(text: d.revision);
  final plant = TextEditingController(text: d.plant);
  final unit = TextEditingController(text: d.unit);
  final kind = TextEditingController(text: d.kind);
  final remarks = TextEditingController(text: d.remarks);
  var type = d.typeCode;
  var status = d.status;
  var applyToAll = true;
  final siblings = state.revisionsOf(d);

  Widget suggest(TextEditingController c, List<String> options, void Function(void Function()) set) =>
      options.isEmpty
          ? const SizedBox()
          : Wrap(spacing: 4, children: [
              for (final o in options.take(8))
                ActionChip(
                  label: Text(o, style: const TextStyle(fontSize: 11)),
                  visualDensity: VisualDensity.compact,
                  onPressed: () => set(() => c.text = o),
                ),
            ]);

  final saved = await showDialog<bool>(
    context: context,
    builder: (c) => StatefulBuilder(
      builder: (c, set) => AlertDialog(
        title: const Text('Drawing details'),
        content: SizedBox(
          width: 480,
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                TextField(controller: number, decoration: const InputDecoration(labelText: 'Drawing no.')),
                const SizedBox(height: 10),
                TextField(controller: title, decoration: const InputDecoration(labelText: 'Title')),
                const SizedBox(height: 10),
                Row(children: [
                  Expanded(
                      child: TextField(controller: rev, decoration: const InputDecoration(labelText: 'Revision'))),
                  const SizedBox(width: 8),
                  Expanded(
                    flex: 2,
                    child: DropdownButtonFormField<DrawingStatus>(
                      initialValue: status,
                      isExpanded: true,
                      decoration: const InputDecoration(labelText: 'Status'),
                      items: [
                        for (final s in DrawingStatus.values)
                          DropdownMenuItem(value: s, child: Text('${s.code} – ${s.label}', overflow: TextOverflow.ellipsis)),
                      ],
                      onChanged: (v) => set(() => status = v ?? status),
                    ),
                  ),
                ]),
                const SizedBox(height: 10),
                DropdownButtonFormField<String>(
                  initialValue: type,
                  isExpanded: true,
                  decoration: const InputDecoration(labelText: 'Drawing type'),
                  items: [
                    for (final t in DrawingType.all) DropdownMenuItem(value: t.code, child: Text(t.label)),
                  ],
                  onChanged: (v) => set(() => type = v ?? type),
                ),
                const SizedBox(height: 10),
                TextField(controller: plant, decoration: const InputDecoration(labelText: 'Plant')),
                suggest(plant, plants, set),
                const SizedBox(height: 10),
                TextField(controller: unit, decoration: const InputDecoration(labelText: 'Unit / Area')),
                suggest(unit, units, set),
                const SizedBox(height: 10),
                TextField(
                    controller: kind,
                    decoration: const InputDecoration(labelText: 'Sheet kind (GA, Isometric, SLD…)')),
                const SizedBox(height: 10),
                TextField(controller: remarks, decoration: const InputDecoration(labelText: 'Remarks'), maxLines: 2),
                if (siblings.length > 1)
                  CheckboxListTile(
                    contentPadding: EdgeInsets.zero,
                    value: applyToAll,
                    onChanged: (v) => set(() => applyToAll = v ?? true),
                    title: Text('Apply number, plant, unit and type to all ${siblings.length} revisions'),
                  ),
                Align(
                  alignment: Alignment.centerLeft,
                  child: Text('File: ${d.fileName}${d.sourcePath.isEmpty ? '' : '\nZIP folder: ${d.sourcePath}'}',
                      style: Theme.of(c).textTheme.bodySmall),
                ),
              ],
            ),
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: const Text('Save')),
        ],
      ),
    ),
  );
  if (saved != true) return;

  final targets = applyToAll ? siblings : [d];
  for (final x in targets) {
    x
      ..number = number.text.trim().isEmpty ? x.number : number.text.trim()
      ..plant = plant.text.trim()
      ..unit = unit.text.trim()
      ..typeCode = type;
  }
  d
    ..title = title.text.trim()
    ..revision = rev.text.trim().isEmpty ? d.revision : rev.text.trim().toUpperCase()
    ..kind = kind.text.trim()
    ..status = status
    ..remarks = remarks.text.trim();
  for (final x in targets) {
    await state.saveDrawing(x);
  }
  if (!targets.contains(d)) await state.saveDrawing(d);
}
