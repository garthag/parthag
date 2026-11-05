import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import 'package:share_plus/share_plus.dart';

import '../data/steel_sections.dart';
import '../models/steel_takeoff.dart';
import '../services/steel_text.dart';
import '../state/app_state.dart';
import '../widgets/common.dart';
import 'steel_library.dart';
import 'steel_takeoff_screen.dart';

/// Steel weight calculator with the IS steel library (IS 808, IS 1730,
/// IS 1731, IS 1732, IS 1786, IS 1239, IS 4923, BS 4-1).
class SteelWeightScreen extends StatefulWidget {
  const SteelWeightScreen({super.key});

  @override
  State<SteelWeightScreen> createState() => _SteelWeightScreenState();
}

class _SteelWeightScreenState extends State<SteelWeightScreen> with SingleTickerProviderStateMixin {
  late final _tabs = TabController(length: 2, vsync: this);

  SteelCategory _category = SteelTables.families.first.category;
  SteelFamily _family = SteelTables.families.first;
  int _size = 0; // index into _family.sections, -1 = custom size
  final Map<String, TextEditingController> _dims = {};
  final _length = TextEditingController(text: '1');
  final _nos = TextEditingController(text: '1');
  final _mark = TextEditingController();
  final List<SteelTakeoffItem> _lines = [];

  @override
  void dispose() {
    _tabs.dispose();
    _length.dispose();
    _nos.dispose();
    _mark.dispose();
    for (final c in _dims.values) {
      c.dispose();
    }
    super.dispose();
  }

  TextEditingController _dim(String key) => _dims.putIfAbsent(key, TextEditingController.new);

  double _num(TextEditingController c) => double.tryParse(c.text.trim().replaceAll(',', '')) ?? 0;

  bool get _custom => _size < 0 || _family.sections.isEmpty;

  double get _kgPerM {
    if (!_custom) return _family.sections[_size].kgPerM;
    return _family.kgPerMFor({for (final d in _family.inputs) d.key: _num(_dim(d.key))});
  }

  String get _designation {
    if (!_custom) return _family.sections[_size].designation;
    final sizes = _family.inputs.map((d) => _dim(d.key).text.trim()).join('×');
    return '${_family.code} $sizes';
  }

  int get _count => int.tryParse(_nos.text.trim()) ?? 0;

  void _select(SteelFamily f, [int size = 0]) {
    setState(() {
      _category = f.category;
      _family = f;
      _size = f.sections.isEmpty ? -1 : size;
    });
  }

  void _pick(SteelRef r) {
    _select(r.family, r.family.sections.indexWhere((s) => s.designation == r.designation));
    _tabs.animateTo(0);
  }

  void _addLine() {
    final kgPerM = _kgPerM;
    final len = _num(_length);
    if (kgPerM <= 0 || len <= 0 || _count <= 0) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Enter a valid size, length and number of pieces')),
      );
      return;
    }
    setState(() => _lines.add(SteelTakeoffItem(
          familyCode: _family.code,
          designation: _designation,
          kgPerM: kgPerM,
          lengthM: len,
          nos: _count,
          memberMark: _mark.text.trim(),
        )));
  }

  double get _totalKg => _lines.fold(0.0, (s, l) => s + l.kg);

  Future<void> _share() => SharePlus.instance.share(
        ShareParams(text: SteelText.items(_lines), subject: 'Steel weight take-off'),
      );

  /// Saves the list into a project take-off (new or existing) – this is how
  /// quick calculations become part of a project's records.
  Future<void> _saveToProject() async {
    final state = context.read<AppState>();
    if (state.projects.isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Create a project first, then save the take-off to it.')),
      );
      return;
    }
    final target = await showDialog<(String, String?)>(
      context: context,
      builder: (c) => SimpleDialog(
        title: const Text('Save to project take-off'),
        children: [
          for (final p in state.projects) ...[
            ListTile(
              leading: const Icon(Icons.add),
              title: Text(p.name),
              subtitle: const Text('New take-off'),
              onTap: () => Navigator.pop(c, (p.id, null)),
            ),
            for (final t in state.takeoffsFor(p.id))
              ListTile(
                contentPadding: const EdgeInsets.only(left: 56, right: 16),
                title: Text(t.title.isEmpty ? 'Untitled take-off' : t.title),
                subtitle: Text('${p.name} · ${fmtNum(t.netMt)} MT'),
                onTap: () => Navigator.pop(c, (p.id, t.id)),
              ),
            const Divider(),
          ],
        ],
      ),
    );
    if (target == null) return;
    final (projectId, takeoffId) = target;
    final t = (takeoffId == null ? null : state.takeoff(takeoffId)) ??
        SteelTakeoff(projectId: projectId, title: 'Take-off ${dateFmt.format(DateTime.now())}');
    t.items.addAll(_lines.map((l) => SteelTakeoffItem.fromJson(l.toJson()..remove('id'))));
    await state.saveTakeoff(t);
    if (!mounted) return;
    setState(_lines.clear);
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(
      content: Text('Saved to "${t.title}"'),
      action: SnackBarAction(
        label: 'Open',
        onPressed: () => Navigator.push(
          context,
          MaterialPageRoute(builder: (_) => TakeoffScreen(takeoffId: t.id)),
        ),
      ),
    ));
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Steel weight (IS)'),
        bottom: TabBar(
          controller: _tabs,
          tabs: const [
            Tab(icon: Icon(Icons.calculate_outlined), text: 'Calculator'),
            Tab(icon: Icon(Icons.menu_book_outlined), text: 'IS steel library'),
          ],
        ),
      ),
      body: TabBarView(
        controller: _tabs,
        children: [
          _calculator(context),
          SteelLibraryView(onPick: _pick, pickLabel: 'Use in calculator'),
        ],
      ),
    );
  }

  Widget _calculator(BuildContext context) {
    final t = Theme.of(context);
    final kgPerM = _kgPerM;
    final kg = kgPerM * _num(_length) * _count;
    final families = SteelLibrary.familiesIn(_category);
    return ListView(
      key: const PageStorageKey('steel-calculator'),
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      children: [
        // Hierarchy: Category → Family → Size.
        DropdownButtonFormField<SteelCategory>(
          key: ValueKey('cat-${_category.name}'),
          initialValue: _category,
          isExpanded: true,
          decoration: const InputDecoration(labelText: '1. Category'),
          items: [
            for (final c in SteelCategory.values)
              DropdownMenuItem(
                value: c,
                child: Row(children: [Icon(steelCategoryIcon(c), size: 18), const SizedBox(width: 8), Text(c.label)]),
              ),
          ],
          onChanged: (c) {
            if (c != null) _select(SteelLibrary.familiesIn(c).first);
          },
        ),
        const SizedBox(height: 12),
        DropdownButtonFormField<SteelFamily>(
          key: ValueKey('family-${_family.code}'),
          initialValue: _family,
          isExpanded: true,
          decoration: const InputDecoration(labelText: '2. Section type'),
          items: [
            for (final f in families)
              DropdownMenuItem(value: f, child: Text('${f.title}  (${f.standard})', overflow: TextOverflow.ellipsis)),
          ],
          onChanged: (f) {
            if (f != null) _select(f);
          },
        ),
        const SizedBox(height: 12),
        if (_family.sections.isNotEmpty)
          Row(
            children: [
              Expanded(
                child: DropdownButtonFormField<int>(
                  key: ValueKey('size-${_family.code}-$_size'),
                  initialValue: _size,
                  isExpanded: true,
                  decoration: const InputDecoration(labelText: '3. Size'),
                  items: [
                    if (_family.shape != SteelShape.table)
                      const DropdownMenuItem(value: -1, child: Text('Custom size…')),
                    for (final (i, s) in _family.sections.indexed)
                      DropdownMenuItem(
                        value: i,
                        child: Text('${s.designation}  –  ${kgPerMText(s.kgPerM)} kg/m', overflow: TextOverflow.ellipsis),
                      ),
                  ],
                  onChanged: (v) => setState(() => _size = v ?? 0),
                ),
              ),
              if (!_custom)
                IconButton(
                  tooltip: 'Details and related sizes',
                  icon: const Icon(Icons.info_outline),
                  onPressed: () => showSteelSectionSheet(
                    context,
                    SteelRef(_family, _family.sections[_size]),
                    onPick: _pick,
                    pickLabel: 'Use in calculator',
                  ),
                ),
            ],
          ),
        if (_custom) ...[
          const SizedBox(height: 12),
          Wrap(
            spacing: 12,
            runSpacing: 12,
            children: [
              for (final d in _family.inputs)
                SizedBox(
                  width: 150,
                  child: TextField(
                    controller: _dim(d.key),
                    keyboardType: const TextInputType.numberWithOptions(decimal: true),
                    decoration: InputDecoration(labelText: d.label),
                    onChanged: (_) => setState(() {}),
                  ),
                ),
            ],
          ),
        ],
        if (!_custom && _family.sections[_size].dims.isNotEmpty) ...[
          const SizedBox(height: 6),
          Text(_family.sections[_size].dims, style: t.textTheme.bodySmall),
        ],
        const SizedBox(height: 12),
        Row(
          children: [
            Expanded(
              child: TextField(
                controller: _length,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                decoration: const InputDecoration(labelText: 'Length (m)'),
                onChanged: (_) => setState(() {}),
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: TextField(
                controller: _nos,
                keyboardType: TextInputType.number,
                decoration: const InputDecoration(labelText: 'No. of pieces'),
                onChanged: (_) => setState(() {}),
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: TextField(
                controller: _mark,
                decoration: const InputDecoration(labelText: 'Member mark'),
              ),
            ),
          ],
        ),
        const SizedBox(height: 16),
        Card(
          color: t.colorScheme.primaryContainer,
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('Unit weight: ${kgPerMText(kgPerM)} kg/m', style: t.textTheme.titleMedium),
                if (_family.code == 'Plate' && _custom && _num(_dim('t')) > 0)
                  Text('= ${fmtNum(SteelCalc.plateKgPerSqm(_num(_dim('t'))))} kg/m² of plate'),
                const SizedBox(height: 6),
                Text('Weight: ${kgMtText(kg)}', style: t.textTheme.headlineSmall),
                const SizedBox(height: 4),
                Text('${_family.title} · ${_family.standard}', style: t.textTheme.bodySmall),
              ],
            ),
          ),
        ),
        const SizedBox(height: 8),
        Align(
          alignment: Alignment.centerRight,
          child: FilledButton.tonalIcon(
            onPressed: _addLine,
            icon: const Icon(Icons.playlist_add),
            label: const Text('Add to list'),
          ),
        ),
        if (_lines.isNotEmpty) ...[
          const Divider(height: 32),
          Text('Total: ${kgMtText(_totalKg)}', style: t.textTheme.titleMedium),
          Row(
            children: [
              Expanded(child: Text('${_lines.length} items', style: t.textTheme.bodySmall)),
              IconButton(tooltip: 'Share', icon: const Icon(Icons.share), onPressed: _share),
              IconButton(
                tooltip: 'Clear all',
                icon: const Icon(Icons.delete_sweep_outlined),
                onPressed: () => setState(_lines.clear),
              ),
            ],
          ),
          FilledButton.icon(
            onPressed: _saveToProject,
            icon: const Icon(Icons.save_alt),
            label: const Text('Save to project take-off'),
          ),
          const SizedBox(height: 8),
          for (final (i, l) in _lines.indexed)
            ListTile(
              dense: true,
              contentPadding: EdgeInsets.zero,
              title: Text(l.memberMark.isEmpty ? l.designation : '${l.memberMark} – ${l.designation}'),
              subtitle: Text('${kgPerMText(l.kgPerM)} kg/m × ${fmtNum(l.lengthM)} m × ${l.nos} nos'),
              trailing: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text('${fmtNum(l.kg)} kg'),
                  IconButton(
                    icon: const Icon(Icons.close, size: 18),
                    tooltip: 'Remove',
                    onPressed: () => setState(() => _lines.removeAt(i)),
                  ),
                ],
              ),
            ),
        ],
        const SizedBox(height: 24),
        Text(steelDisclaimer, style: t.textTheme.bodySmall),
      ],
    );
  }
}
