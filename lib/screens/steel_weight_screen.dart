import 'package:flutter/material.dart';
import 'package:share_plus/share_plus.dart';

import '../data/steel_sections.dart';
import '../widgets/common.dart';

/// Steel weight calculator and IS steel table (IS 808, IS 1730, IS 1731,
/// IS 1732, IS 1786, IS 1239, IS 4923, BS 4-1).
class SteelWeightScreen extends StatefulWidget {
  const SteelWeightScreen({super.key});

  @override
  State<SteelWeightScreen> createState() => _SteelWeightScreenState();
}

/// kg/m with three decimals for light sections (e.g. TMT 8 = 0.395).
String _kgm(double v) => v > 0 && v < 1 ? v.toStringAsFixed(3) : fmtNum(v);

class _TakeoffLine {
  _TakeoffLine(this.description, this.kgPerM, this.length, this.nos);
  final String description;
  final double kgPerM;
  final double length;
  final int nos;

  double get kg => kgPerM * length * nos;
}

class _SteelWeightScreenState extends State<SteelWeightScreen> with SingleTickerProviderStateMixin {
  late final _tabs = TabController(length: 2, vsync: this);

  SteelFamily _family = SteelTables.families.first;
  int _size = 0; // index into _family.sections, -1 = custom size
  final Map<String, TextEditingController> _dims = {};
  final _length = TextEditingController(text: '1');
  final _nos = TextEditingController(text: '1');
  final List<_TakeoffLine> _lines = [];

  SteelFamily _tableFamily = SteelTables.ismb;

  @override
  void dispose() {
    _tabs.dispose();
    _length.dispose();
    _nos.dispose();
    for (final c in _dims.values) {
      c.dispose();
    }
    super.dispose();
  }

  TextEditingController _dim(String key) => _dims.putIfAbsent(key, TextEditingController.new);

  double _num(TextEditingController c) => double.tryParse(c.text.trim()) ?? 0;

  bool get _custom => _size < 0 || _family.sections.isEmpty;

  double get _kgPerM {
    if (!_custom) return _family.sections[_size].kgPerM;
    return _family.kgPerMFor({for (final d in _family.inputs) d.key: _num(_dim(d.key))});
  }

  String get _description {
    if (!_custom) return _family.sections[_size].designation;
    final sizes = _family.inputs.map((d) => _dim(d.key).text.trim()).join('×');
    return '${_family.code} $sizes';
  }

  int get _count => int.tryParse(_nos.text.trim()) ?? 0;

  void _selectFamily(SteelFamily f, [int size = 0]) {
    setState(() {
      _family = f;
      _size = f.sections.isEmpty ? -1 : size;
    });
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
    setState(() => _lines.add(_TakeoffLine(_description, kgPerM, len, _count)));
  }

  double get _totalKg => _lines.fold(0.0, (s, l) => s + l.kg);

  Future<void> _share() async {
    final b = StringBuffer('*Steel weight take-off*\n');
    for (final (i, l) in _lines.indexed) {
      b.writeln('${i + 1}. ${l.description} – ${_kgm(l.kgPerM)} kg/m × '
          '${fmtNum(l.length)} m × ${l.nos} nos = ${fmtNum(l.kg)} kg');
    }
    b.writeln('*Total: ${fmtNum(_totalKg)} kg (${(_totalKg / 1000).toStringAsFixed(3)} MT)*');
    b.write('_Nominal weights as per IS 808 / IS 1786 / IS 1239 / IS 4923, steel @ 7850 kg/m³_');
    await SharePlus.instance.share(ShareParams(text: b.toString(), subject: 'Steel weight take-off'));
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Steel weight (IS)'),
        bottom: TabBar(
          controller: _tabs,
          tabs: const [Tab(text: 'Calculator'), Tab(text: 'IS steel table')],
        ),
      ),
      body: TabBarView(
        controller: _tabs,
        children: [_calculator(context), _table(context)],
      ),
    );
  }

  // ----------------------------------------------------------- calculator
  Widget _calculator(BuildContext context) {
    final t = Theme.of(context);
    final kgPerM = _kgPerM;
    final len = _num(_length);
    final kg = kgPerM * len * _count;
    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      children: [
        DropdownButtonFormField<SteelFamily>(
          key: ValueKey('family-${_family.code}'),
          initialValue: _family,
          isExpanded: true,
          decoration: const InputDecoration(labelText: 'Section type'),
          items: [
            for (final f in SteelTables.families)
              DropdownMenuItem(value: f, child: Text('${f.title}  (${f.standard})', overflow: TextOverflow.ellipsis)),
          ],
          onChanged: (f) {
            if (f != null) _selectFamily(f);
          },
        ),
        const SizedBox(height: 12),
        if (_family.sections.isNotEmpty)
          DropdownButtonFormField<int>(
            key: ValueKey('size-${_family.code}-$_size'),
            initialValue: _size,
            isExpanded: true,
            decoration: const InputDecoration(labelText: 'Size'),
            items: [
              if (_family.shape != SteelShape.table) const DropdownMenuItem(value: -1, child: Text('Custom size…')),
              for (final (i, s) in _family.sections.indexed)
                DropdownMenuItem(
                  value: i,
                  child: Text('${s.designation}  –  ${_kgm(s.kgPerM)} kg/m', overflow: TextOverflow.ellipsis),
                ),
            ],
            onChanged: (v) => setState(() => _size = v ?? 0),
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
                Text('Unit weight: ${_kgm(kgPerM)} kg/m', style: t.textTheme.titleMedium),
                if (_family.code == 'Plate' && _custom && _num(_dim('t')) > 0)
                  Text('= ${fmtNum(SteelCalc.plateKgPerSqm(_num(_dim('t'))))} kg/m² of plate'),
                const SizedBox(height: 6),
                Text('Weight: ${fmtNum(kg)} kg  (${(kg / 1000).toStringAsFixed(3)} MT)',
                    style: t.textTheme.headlineSmall),
                const SizedBox(height: 4),
                Text(_family.standard, style: t.textTheme.bodySmall),
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
            label: const Text('Add to take-off'),
          ),
        ),
        if (_lines.isNotEmpty) ...[
          const Divider(height: 32),
          Row(
            children: [
              Expanded(child: Text('Take-off (${_lines.length} items)', style: t.textTheme.titleMedium)),
              IconButton(tooltip: 'Share', icon: const Icon(Icons.share), onPressed: _share),
              IconButton(
                tooltip: 'Clear all',
                icon: const Icon(Icons.delete_sweep_outlined),
                onPressed: () => setState(_lines.clear),
              ),
            ],
          ),
          for (final (i, l) in _lines.indexed)
            ListTile(
              dense: true,
              contentPadding: EdgeInsets.zero,
              title: Text(l.description),
              subtitle: Text('${_kgm(l.kgPerM)} kg/m × ${fmtNum(l.length)} m × ${l.nos} nos'),
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
          const Divider(),
          Text('Total: ${fmtNum(_totalKg)} kg  =  ${(_totalKg / 1000).toStringAsFixed(3)} MT',
              style: t.textTheme.titleMedium),
        ],
        const SizedBox(height: 24),
        Text(
          'Nominal weights from IS 808 / SP 6(1) for rolled sections and BS 4-1 for UB/UC. '
          'Plates, flats, bars, pipes and hollow sections are worked out at 7850 kg/m³. '
          'Actual mill weight can vary within the IS 1852 rolling tolerance; check the supplier\'s chart before billing.',
          style: t.textTheme.bodySmall,
        ),
      ],
    );
  }

  // ---------------------------------------------------------------- table
  Widget _table(BuildContext context) {
    final t = Theme.of(context);
    final f = _tableFamily;
    return Column(
      children: [
        SizedBox(
          height: 56,
          child: ListView(
            scrollDirection: Axis.horizontal,
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
            children: [
              for (final x in SteelTables.families)
                Padding(
                  padding: const EdgeInsets.only(right: 6),
                  child: ChoiceChip(
                    label: Text(x.code),
                    selected: x == f,
                    onSelected: (_) => setState(() => _tableFamily = x),
                  ),
                ),
            ],
          ),
        ),
        ListTile(
          title: Text(f.title, style: t.textTheme.titleMedium),
          subtitle: Text('${f.standard} · tap a size to use it in the calculator'),
        ),
        const Divider(height: 1),
        Expanded(
          child: ListView.separated(
            itemCount: f.sections.length,
            separatorBuilder: (_, _) => const Divider(height: 1),
            itemBuilder: (context, i) {
              final s = f.sections[i];
              return ListTile(
                dense: true,
                title: Text(s.designation),
                subtitle: s.dims.isEmpty ? null : Text(s.dims),
                trailing: Text('${_kgm(s.kgPerM)} kg/m', style: t.textTheme.titleSmall),
                onTap: () {
                  _selectFamily(f, i);
                  _tabs.animateTo(0);
                },
              );
            },
          ),
        ),
      ],
    );
  }
}
