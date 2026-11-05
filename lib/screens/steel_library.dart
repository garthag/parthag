import 'package:flutter/material.dart';

import '../data/steel_sections.dart';
import '../widgets/common.dart';

/// kg/m with three decimals for light sections (e.g. TMT 8 = 0.395).
String kgPerMText(double v) => v > 0 && v < 1 ? v.toStringAsFixed(3) : fmtNum(v);

/// Weight in kg with MT alongside, e.g. "1,234.5 kg (1.235 MT)".
String kgMtText(double kg) => '${fmtNum(kg)} kg (${(kg / 1000).toStringAsFixed(3)} MT)';

IconData steelCategoryIcon(SteelCategory c) => switch (c) {
      SteelCategory.platesFlats => Icons.crop_landscape,
      SteelCategory.barsRebar => Icons.horizontal_rule,
      SteelCategory.angles => Icons.turn_right,
      SteelCategory.channels => Icons.code,
      SteelCategory.beamsColumns => Icons.view_column_outlined,
      SteelCategory.pipesHollow => Icons.circle_outlined,
    };

/// Browsable IS steel library: Category → Family → Size, with search,
/// filters, sorting and a detail sheet showing related sizes.
///
/// [onPick] is called when the user chooses a size (e.g. to use it in the
/// calculator or add it to a take-off); [pickLabel] names that action.
class SteelLibraryView extends StatefulWidget {
  const SteelLibraryView({super.key, this.onPick, this.pickLabel = 'Use this size'});

  final ValueChanged<SteelRef>? onPick;
  final String pickLabel;

  @override
  State<SteelLibraryView> createState() => _SteelLibraryViewState();
}

class _SteelLibraryViewState extends State<SteelLibraryView> {
  final _query = TextEditingController();
  SteelCategory? _category;
  String? _family;
  SteelSort _sort = SteelSort.size;
  double? _min;
  double? _max;

  @override
  void dispose() {
    _query.dispose();
    super.dispose();
  }

  bool get _hasWeightFilter => _min != null || _max != null;

  Future<void> _weightFilter() async {
    final min = TextEditingController(text: _min == null ? '' : fmtNum(_min!));
    final max = TextEditingController(text: _max == null ? '' : fmtNum(_max!));
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: const Text('Unit weight range (kg/m)'),
        content: Row(
          children: [
            Expanded(
              child: TextField(
                controller: min,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                decoration: const InputDecoration(labelText: 'From'),
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: TextField(
                controller: max,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                decoration: const InputDecoration(labelText: 'To'),
              ),
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () {
              min.clear();
              max.clear();
              Navigator.pop(c, true);
            },
            child: const Text('Clear'),
          ),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: const Text('Apply')),
        ],
      ),
    );
    if (ok == true) {
      setState(() {
        _min = double.tryParse(min.text.replaceAll(',', ''));
        _max = double.tryParse(max.text.replaceAll(',', ''));
      });
    }
  }

  void _open(SteelRef r) => showSteelSectionSheet(context, r, onPick: widget.onPick, pickLabel: widget.pickLabel);

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    final results = SteelLibrary.search(
      query: _query.text,
      category: _category,
      familyCode: _family,
      minKgPerM: _min,
      maxKgPerM: _max,
      sort: _sort,
    );
    final families = SteelLibrary.familiesIn(_category);

    // Flatten the hierarchy into rows: category header, family header, sizes.
    // Headers only make sense in table order.
    final rows = <Object>[];
    if (_sort == SteelSort.size) {
      SteelLibrary.group(results).forEach((cat, fams) {
        rows.add(cat);
        fams.forEach((fam, refs) {
          rows.add(fam);
          rows.addAll(refs);
        });
      });
    } else {
      rows.addAll(results);
    }

    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(12, 12, 12, 4),
          child: TextField(
            controller: _query,
            decoration: InputDecoration(
              prefixIcon: const Icon(Icons.search),
              hintText: 'Search: ISMB 300, 50x50, TMT 12, IS 1239…',
              border: const OutlineInputBorder(),
              isDense: true,
              suffixIcon: _query.text.isEmpty
                  ? null
                  : IconButton(
                      tooltip: 'Clear',
                      icon: const Icon(Icons.close),
                      onPressed: () => setState(_query.clear),
                    ),
            ),
            onChanged: (_) => setState(() {}),
          ),
        ),
        _chips([
          ChoiceChip(
            label: const Text('All'),
            selected: _category == null,
            onSelected: (_) => setState(() {
              _category = null;
              _family = null;
            }),
          ),
          for (final c in SteelCategory.values)
            ChoiceChip(
              avatar: Icon(steelCategoryIcon(c), size: 16),
              label: Text(c.label),
              selected: _category == c,
              onSelected: (_) => setState(() {
                _category = _category == c ? null : c;
                _family = null;
              }),
            ),
        ]),
        _chips([
          for (final f in families)
            FilterChip(
              label: Text(f.code),
              tooltip: '${f.name} · ${f.standard}',
              selected: _family == f.code,
              onSelected: (_) => setState(() => _family = _family == f.code ? null : f.code),
            ),
        ]),
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 12),
          child: Row(
            children: [
              Expanded(child: Text('${results.length} sizes', style: t.textTheme.bodySmall)),
              TextButton.icon(
                onPressed: _weightFilter,
                icon: Icon(_hasWeightFilter ? Icons.filter_alt : Icons.filter_alt_outlined, size: 18),
                label: Text(_hasWeightFilter
                    ? '${_min == null ? '0' : fmtNum(_min!)}–${_max == null ? '∞' : fmtNum(_max!)} kg/m'
                    : 'kg/m range'),
              ),
              PopupMenuButton<SteelSort>(
                tooltip: 'Sort',
                initialValue: _sort,
                onSelected: (s) => setState(() => _sort = s),
                itemBuilder: (_) => [
                  for (final s in SteelSort.values) PopupMenuItem(value: s, child: Text(s.label)),
                ],
                child: Padding(
                  padding: const EdgeInsets.all(8),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [const Icon(Icons.sort, size: 18), const SizedBox(width: 4), Text(_sort.label)],
                  ),
                ),
              ),
            ],
          ),
        ),
        const Divider(height: 1),
        Expanded(
          child: rows.isEmpty
              ? const EmptyState(
                  icon: Icons.search_off,
                  title: 'No sizes found',
                  message: 'Try another search or clear the filters.',
                )
              : ListView.builder(
                  itemCount: rows.length,
                  itemBuilder: (context, i) {
                    final row = rows[i];
                    if (row is SteelCategory) {
                      return Container(
                        color: t.colorScheme.surfaceContainerHighest,
                        padding: const EdgeInsets.fromLTRB(16, 10, 16, 10),
                        child: Row(
                          children: [
                            Icon(steelCategoryIcon(row), size: 18),
                            const SizedBox(width: 8),
                            Text(row.label, style: t.textTheme.titleSmall),
                          ],
                        ),
                      );
                    }
                    if (row is SteelFamily) {
                      return Padding(
                        padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
                        child: Text('${row.title}  ·  ${row.standard}',
                            style: t.textTheme.labelLarge?.copyWith(color: t.colorScheme.primary)),
                      );
                    }
                    final r = row as SteelRef;
                    return ListTile(
                      dense: true,
                      title: Text(r.designation),
                      subtitle: Text(r.section.dims.isNotEmpty
                          ? r.section.dims
                          : _sort == SteelSort.size
                              ? r.family.standard
                              : '${r.family.title} · ${r.family.standard}'),
                      trailing: Text('${kgPerMText(r.kgPerM)} kg/m', style: t.textTheme.titleSmall),
                      onTap: () => _open(r),
                    );
                  },
                ),
        ),
      ],
    );
  }

  Widget _chips(List<Widget> chips) => SizedBox(
        height: 48,
        child: ListView(
          scrollDirection: Axis.horizontal,
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
          children: [
            for (final c in chips) Padding(padding: const EdgeInsets.only(right: 6), child: c),
          ],
        ),
      );
}

/// Detail of one size with its relations: standard, stock-length weights,
/// next smaller / larger size and equal-weight alternatives. Tapping a
/// related size moves the sheet to it.
Future<void> showSteelSectionSheet(BuildContext context, SteelRef start,
    {ValueChanged<SteelRef>? onPick, String pickLabel = 'Use this size'}) {
  return showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    builder: (sheetContext) {
      var r = start;
      return StatefulBuilder(builder: (context, setSheet) {
        final t = Theme.of(context);
        final n = SteelLibrary.neighbours(r);
        final alts = SteelLibrary.alternatives(r);
        Widget relChip(SteelRef x, {String? prefix}) => ActionChip(
              label: Text('${prefix ?? ''}${x.designation} · ${kgPerMText(x.kgPerM)}'),
              onPressed: () => setSheet(() => r = x),
            );
        return DraggableScrollableSheet(
          expand: false,
          initialChildSize: 0.7,
          maxChildSize: 0.95,
          builder: (context, scroll) => ListView(
            controller: scroll,
            padding: const EdgeInsets.fromLTRB(20, 0, 20, 24),
            children: [
              Text(r.designation, style: t.textTheme.headlineSmall),
              Text('${r.category.label} › ${r.family.title}', style: t.textTheme.bodyMedium),
              const SizedBox(height: 4),
              Chip(label: Text(r.family.standard), avatar: const Icon(Icons.menu_book_outlined, size: 16)),
              if (r.section.dims.isNotEmpty) Text('Dimensions (mm): ${r.section.dims}'),
              const SizedBox(height: 12),
              Card(
                color: t.colorScheme.primaryContainer,
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text('${kgPerMText(r.kgPerM)} kg/m', style: t.textTheme.headlineMedium),
                      const SizedBox(height: 4),
                      Text('6 m length: ${fmtNum(r.kgPerM * 6)} kg   ·   12 m length: ${fmtNum(r.kgPerM * 12)} kg'),
                      Text('1 MT ≈ ${fmtNum(1000 / r.kgPerM)} m of this size'),
                    ],
                  ),
                ),
              ),
              if (n.smaller != null || n.larger != null) ...[
                const SizedBox(height: 16),
                Text('Next size in ${r.family.code}', style: t.textTheme.titleSmall),
                const SizedBox(height: 6),
                Wrap(spacing: 8, runSpacing: 4, children: [
                  if (n.smaller != null) relChip(n.smaller!, prefix: '↓ '),
                  if (n.larger != null) relChip(n.larger!, prefix: '↑ '),
                ]),
              ],
              if (alts.isNotEmpty) ...[
                const SizedBox(height: 16),
                Text('Alternatives within ±10% weight (${r.category.label})', style: t.textTheme.titleSmall),
                const SizedBox(height: 6),
                Wrap(spacing: 8, runSpacing: 4, children: [for (final a in alts) relChip(a)]),
                const SizedBox(height: 4),
                Text('Check strength (Zxx, rxx) before substituting – equal weight is not equal capacity.',
                    style: t.textTheme.bodySmall),
              ],
              if (onPick != null) ...[
                const SizedBox(height: 20),
                FilledButton.icon(
                  onPressed: () {
                    Navigator.pop(sheetContext);
                    onPick(r);
                  },
                  icon: const Icon(Icons.check),
                  label: Text(pickLabel),
                ),
              ],
            ],
          ),
        );
      });
    },
  );
}

/// Full-screen library used to pick a size, returning it to the caller.
class SteelPickerScreen extends StatelessWidget {
  const SteelPickerScreen({super.key, this.title = 'Choose steel section'});

  final String title;

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: Text(title)),
        body: SteelLibraryView(
          pickLabel: 'Select',
          onPick: (r) => Navigator.pop(context, r),
        ),
      );
}
