import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import 'package:share_plus/share_plus.dart';

import '../data/steel_sections.dart';
import '../models/models.dart';
import '../models/steel_takeoff.dart';
import '../services/steel_text.dart';
import '../state/app_state.dart';
import '../widgets/common.dart';
import 'report_screen.dart';
import 'steel_library.dart';
import 'steel_weight_screen.dart';

enum _TakeoffSort {
  date('Newest first'),
  weight('Heaviest first'),
  title('Title A → Z');

  const _TakeoffSort(this.label);
  final String label;
}

/// "Steel" tab of a project: all its take-offs with filter and sort.
class ProjectSteelTab extends StatefulWidget {
  const ProjectSteelTab({super.key, required this.projectId});

  final String projectId;

  @override
  State<ProjectSteelTab> createState() => _ProjectSteelTabState();
}

class _ProjectSteelTabState extends State<ProjectSteelTab> {
  TakeoffStatus? _status;
  _TakeoffSort _sort = _TakeoffSort.date;

  Future<void> _new() async {
    final state = context.read<AppState>();
    final t = SteelTakeoff(projectId: widget.projectId, title: 'Take-off ${dateFmt.format(DateTime.now())}');
    final ok = await editTakeoffDetails(context, t);
    if (!ok) return;
    await state.saveTakeoff(t);
    if (mounted) {
      Navigator.push(context, MaterialPageRoute(builder: (_) => TakeoffScreen(takeoffId: t.id)));
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    final all = context.watch<AppState>().takeoffsFor(widget.projectId);
    final list = all.where((x) => _status == null || x.status == _status).toList();
    switch (_sort) {
      case _TakeoffSort.date:
        break; // already newest first
      case _TakeoffSort.weight:
        list.sort((a, b) => b.netKg.compareTo(a.netKg));
      case _TakeoffSort.title:
        list.sort((a, b) => a.title.toLowerCase().compareTo(b.title.toLowerCase()));
    }
    final byStatus = {
      for (final s in TakeoffStatus.values) s: all.where((x) => x.status == s).fold(0.0, (v, x) => v + x.netMt),
    };

    return ListView(
      padding: const EdgeInsets.fromLTRB(12, 12, 12, 96),
      children: [
        Row(
          children: [
            Expanded(child: Text('Steel take-offs', style: t.textTheme.titleMedium)),
            TextButton.icon(
              onPressed: () => Navigator.push(context, MaterialPageRoute(builder: (_) => const SteelWeightScreen())),
              icon: const Icon(Icons.calculate_outlined),
              label: const Text('Calculator'),
            ),
            FilledButton.tonalIcon(onPressed: _new, icon: const Icon(Icons.add), label: const Text('New')),
          ],
        ),
        if (all.isNotEmpty) ...[
          const SizedBox(height: 8),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              StatTile(
                icon: Icons.scale_outlined,
                label: 'Total steel',
                value: '${fmtNum(all.fold(0.0, (v, x) => v + x.netMt))} MT',
              ),
              for (final s in TakeoffStatus.values)
                if (byStatus[s]! > 0) StatTile(icon: Icons.label_outline, label: s.label, value: '${fmtNum(byStatus[s]!)} MT'),
            ],
          ),
          const SizedBox(height: 8),
          SizedBox(
            height: 44,
            child: ListView(
              scrollDirection: Axis.horizontal,
              children: [
                Padding(
                  padding: const EdgeInsets.only(right: 6),
                  child: ChoiceChip(
                    label: const Text('All'),
                    selected: _status == null,
                    onSelected: (_) => setState(() => _status = null),
                  ),
                ),
                for (final s in TakeoffStatus.values)
                  Padding(
                    padding: const EdgeInsets.only(right: 6),
                    child: ChoiceChip(
                      label: Text(s.label),
                      selected: _status == s,
                      onSelected: (_) => setState(() => _status = _status == s ? null : s),
                    ),
                  ),
                PopupMenuButton<_TakeoffSort>(
                  tooltip: 'Sort',
                  initialValue: _sort,
                  onSelected: (s) => setState(() => _sort = s),
                  itemBuilder: (_) => [for (final s in _TakeoffSort.values) PopupMenuItem(value: s, child: Text(s.label))],
                  child: Padding(
                    padding: const EdgeInsets.all(8),
                    child: Row(children: [const Icon(Icons.sort, size: 18), const SizedBox(width: 4), Text(_sort.label)]),
                  ),
                ),
              ],
            ),
          ),
        ],
        const SizedBox(height: 8),
        if (all.isEmpty)
          const Padding(
            padding: EdgeInsets.only(top: 32),
            child: EmptyState(
              icon: Icons.view_column_outlined,
              title: 'No steel take-offs yet',
              message: 'List the steel members from the drawing (ISMB, ISMC, ISA, plates, pipes…) '
                  'to get the weight in MT and post it into a DPR.',
            ),
          )
        else
          for (final x in list)
            Card(
              child: ListTile(
                leading: CircleAvatar(child: Text(x.items.length.toString())),
                title: Text(x.title.isEmpty ? 'Untitled take-off' : x.title),
                subtitle: Text([
                  dateFmt.format(x.date),
                  x.status.label,
                  if (x.drawingNo.isNotEmpty) 'Dwg ${x.drawingNo}',
                  if (x.postings.isNotEmpty) 'Posted to ${x.postings.map((p) => p.reportId).toSet().length} DPR(s)',
                ].join(' · ')),
                trailing: Text('${fmtNum(x.netMt)} MT', style: t.textTheme.titleMedium),
                onTap: () => Navigator.push(context, MaterialPageRoute(builder: (_) => TakeoffScreen(takeoffId: x.id))),
              ),
            ),
      ],
    );
  }
}

enum _ItemSort {
  entry('Entry order'),
  mark('Member mark'),
  size('Section size'),
  weight('Heaviest first');

  const _ItemSort(this.label);
  final String label;
}

/// One take-off: items grouped Category → Family → Item with sub-totals,
/// filter, sort, and links to the DPRs it was posted into.
class TakeoffScreen extends StatefulWidget {
  const TakeoffScreen({super.key, required this.takeoffId});

  final String takeoffId;

  @override
  State<TakeoffScreen> createState() => _TakeoffScreenState();
}

class _TakeoffScreenState extends State<TakeoffScreen> {
  final _query = TextEditingController();
  String? _family;
  _ItemSort _sort = _ItemSort.entry;

  @override
  void dispose() {
    _query.dispose();
    super.dispose();
  }

  Future<void> _save(SteelTakeoff t) => context.read<AppState>().saveTakeoff(t);

  Future<void> _addItem(SteelTakeoff t) async {
    final ref = await Navigator.push<SteelRef>(
      context,
      MaterialPageRoute(builder: (_) => const SteelPickerScreen(title: 'Add steel member')),
    );
    if (ref == null || !mounted) return;
    final item = SteelTakeoffItem.fromRef(ref);
    if (await _editTakeoffItem(context, item, isNew: true) == _ItemAction.save) {
      t.items.add(item);
      await _save(t);
    }
  }

  Future<void> _addCustom(SteelTakeoff t) async {
    final item = SteelTakeoffItem(familyCode: 'Custom', designation: '', kgPerM: 0);
    if (await _editTakeoffItem(context, item, isNew: true, custom: true) == _ItemAction.save) {
      t.items.add(item);
      await _save(t);
    }
  }

  Future<void> _editItem(SteelTakeoff t, SteelTakeoffItem item) async {
    final copy = SteelTakeoffItem.fromJson(item.toJson());
    final action = await _editTakeoffItem(context, copy, custom: item.ref == null);
    if (action == _ItemAction.save) {
      final i = t.items.indexWhere((e) => e.id == item.id);
      if (i >= 0) t.items[i] = copy;
      await _save(t);
    } else if (action == _ItemAction.delete) {
      t.items.removeWhere((e) => e.id == item.id);
      await _save(t);
    } else if (action == _ItemAction.duplicate) {
      final dup = SteelTakeoffItem.fromJson(copy.toJson()..remove('id'));
      t.items.insert(t.items.indexWhere((e) => e.id == item.id) + 1, dup);
      await _save(t);
    }
  }

  Future<void> _menu(String v, SteelTakeoff t) async {
    final state = context.read<AppState>();
    switch (v) {
      case 'details':
        if (await editTakeoffDetails(context, t)) await _save(t);
      case 'custom':
        await _addCustom(t);
      case 'share':
        await SharePlus.instance.share(
          ShareParams(text: SteelText.takeoff(t, state.project(t.projectId)), subject: 'Steel take-off ${t.title}'),
        );
      case 'post':
        if (mounted) await postTakeoffDialog(context, t);
      case 'delete':
        final ok = await confirm(context, 'Delete take-off?',
            '"${t.title}" and its ${t.items.length} items will be deleted. DPR entries already posted stay as they are.');
        if (ok && mounted) {
          Navigator.pop(context);
          await state.deleteTakeoff(t.id);
        }
    }
  }

  List<SteelTakeoffItem> _filtered(List<SteelTakeoffItem> items) {
    final q = normalizeSteelText(_query.text);
    final out = items.where((i) {
      if (_family != null && i.familyCode != _family) return false;
      if (q.isEmpty) return true;
      final hay = normalizeSteelText('${i.memberMark} ${i.designation} ${i.location} ${i.remarks}');
      return q.split(' ').every((w) => hay.contains(w) || hay.replaceAll(' ', '').contains(w));
    }).toList();
    switch (_sort) {
      case _ItemSort.entry:
        break;
      case _ItemSort.mark:
        out.sort((a, b) => a.memberMark.toLowerCase().compareTo(b.memberMark.toLowerCase()));
      case _ItemSort.size:
        out.sort((a, b) => a.kgPerM.compareTo(b.kgPerM));
      case _ItemSort.weight:
        out.sort((a, b) => b.kg.compareTo(a.kg));
    }
    return out;
  }

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final tk = state.takeoff(widget.takeoffId);
    if (tk == null) return const Scaffold(body: SizedBox());
    final project = state.project(tk.projectId);
    final th = Theme.of(context);
    final groups = tk.groups;
    final visibleGroups = [
      for (final g in groups)
        if (_filtered(g.items).isNotEmpty) TakeoffGroup(g.familyCode, g.title, _filtered(g.items)),
    ];

    return Scaffold(
      appBar: AppBar(
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(tk.title.isEmpty ? 'Steel take-off' : tk.title, overflow: TextOverflow.ellipsis),
            if (project != null) Text(project.name, style: th.textTheme.bodySmall),
          ],
        ),
        actions: [
          IconButton(tooltip: 'Post to DPR', icon: const Icon(Icons.post_add), onPressed: () => _menu('post', tk)),
          PopupMenuButton<String>(
            onSelected: (v) => _menu(v, tk),
            itemBuilder: (_) => const [
              PopupMenuItem(value: 'details', child: ListTile(leading: Icon(Icons.edit_outlined), title: Text('Edit details'))),
              PopupMenuItem(value: 'custom', child: ListTile(leading: Icon(Icons.straighten), title: Text('Add custom size'))),
              PopupMenuItem(value: 'share', child: ListTile(leading: Icon(Icons.share), title: Text('Share (WhatsApp)'))),
              PopupMenuItem(value: 'post', child: ListTile(leading: Icon(Icons.post_add), title: Text('Post to DPR'))),
              PopupMenuItem(value: 'delete', child: ListTile(leading: Icon(Icons.delete_outline), title: Text('Delete take-off'))),
            ],
          ),
        ],
      ),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: () => _addItem(tk),
        icon: const Icon(Icons.add),
        label: const Text('Add member'),
      ),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(12, 12, 12, 96),
        children: [
          // ---- summary
          Card(
            color: th.colorScheme.primaryContainer,
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('Net: ${kgMtText(tk.netKg)}', style: th.textTheme.titleLarge),
                  if (tk.wastagePercent > 0)
                    Text('+ ${fmtNum(tk.wastagePercent)}% wastage = gross ${kgMtText(tk.grossKg)}'),
                  const SizedBox(height: 4),
                  Text([
                    '${tk.items.length} items',
                    '${tk.pieces} pieces',
                    dateFmt.format(tk.date),
                    tk.status.label,
                    if (tk.drawingNo.isNotEmpty) 'Dwg ${tk.drawingNo}',
                  ].join(' · ')),
                ],
              ),
            ),
          ),
          // ---- category breakdown (top of hierarchy)
          if (tk.items.isNotEmpty) ...[
            const SizedBox(height: 8),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final e in tk.kgByCategory.entries)
                  Chip(
                    avatar: Icon(e.key == null ? Icons.straighten : steelCategoryIcon(e.key!), size: 16),
                    label: Text('${e.key?.label ?? 'Custom'}: ${(e.value / 1000).toStringAsFixed(3)} MT'),
                  ),
              ],
            ),
          ],
          // ---- links to DPRs
          if (tk.postings.isNotEmpty) ...[
            const SizedBox(height: 12),
            Text('Posted to DPRs', style: th.textTheme.titleSmall),
            for (final p in tk.postings.reversed)
              Builder(builder: (context) {
                final r = state.report(p.reportId);
                return ListTile(
                  dense: true,
                  leading: const Icon(Icons.link),
                  title: Text(r == null
                      ? 'Deleted DPR'
                      : 'DPR-${r.reportNo.toString().padLeft(3, '0')} · ${dateFmt.format(r.date)}'),
                  subtitle: Text('${fmtNum(p.mt)} MT · ${[if (p.asWork) 'Work done', if (p.asMaterial) 'Materials'].join(' + ')}'),
                  trailing: r == null ? null : const Icon(Icons.chevron_right),
                  onTap: r == null
                      ? null
                      : () => Navigator.push(context, MaterialPageRoute(builder: (_) => ReportScreen(reportId: r.id))),
                );
              }),
          ],
          const SizedBox(height: 12),
          // ---- filter & sort
          if (tk.items.isNotEmpty) ...[
            TextField(
              controller: _query,
              decoration: InputDecoration(
                prefixIcon: const Icon(Icons.search),
                hintText: 'Filter: member mark, section, location',
                isDense: true,
                border: const OutlineInputBorder(),
                suffixIcon: _query.text.isEmpty
                    ? null
                    : IconButton(icon: const Icon(Icons.close), onPressed: () => setState(_query.clear)),
              ),
              onChanged: (_) => setState(() {}),
            ),
            SizedBox(
              height: 48,
              child: ListView(
                scrollDirection: Axis.horizontal,
                padding: const EdgeInsets.symmetric(vertical: 6),
                children: [
                  for (final g in groups)
                    Padding(
                      padding: const EdgeInsets.only(right: 6),
                      child: FilterChip(
                        label: Text('${g.familyCode} (${g.items.length})'),
                        selected: _family == g.familyCode,
                        onSelected: (_) => setState(() => _family = _family == g.familyCode ? null : g.familyCode),
                      ),
                    ),
                  PopupMenuButton<_ItemSort>(
                    tooltip: 'Sort',
                    initialValue: _sort,
                    onSelected: (s) => setState(() => _sort = s),
                    itemBuilder: (_) => [for (final s in _ItemSort.values) PopupMenuItem(value: s, child: Text(s.label))],
                    child: Padding(
                      padding: const EdgeInsets.all(8),
                      child: Row(children: [const Icon(Icons.sort, size: 18), const SizedBox(width: 4), Text(_sort.label)]),
                    ),
                  ),
                ],
              ),
            ),
          ],
          // ---- items, grouped by family
          if (tk.items.isEmpty)
            const Padding(
              padding: EdgeInsets.only(top: 32),
              child: EmptyState(
                icon: Icons.playlist_add,
                title: 'No members yet',
                message: 'Tap "Add member" and pick a section from the IS steel library.',
              ),
            ),
          for (final g in visibleGroups)
            Card(
              child: ExpansionTile(
                initiallyExpanded: true,
                title: Text(g.title),
                subtitle: Text('${g.nos} nos · ${fmtNum(g.lengthM)} m · ${kgMtText(g.kg)}'),
                children: [
                  for (final it in g.items)
                    ListTile(
                      dense: true,
                      title: Text(it.memberMark.isEmpty ? it.designation : '${it.memberMark} – ${it.designation}'),
                      subtitle: Text([
                        '${kgPerMText(it.kgPerM)} kg/m × ${fmtNum(it.lengthM)} m × ${it.nos} nos',
                        if (it.location.isNotEmpty) it.location,
                        if (it.remarks.isNotEmpty) it.remarks,
                      ].join(' · ')),
                      trailing: Text('${fmtNum(it.kg)} kg', style: th.textTheme.titleSmall),
                      onTap: () => _editItem(tk, it),
                    ),
                ],
              ),
            ),
          const SizedBox(height: 16),
          Text(steelDisclaimer, style: th.textTheme.bodySmall),
        ],
      ),
    );
  }
}

// ------------------------------------------------------------------ dialogs

/// Edits title, date, drawing, status and wastage. Returns true when saved.
Future<bool> editTakeoffDetails(BuildContext context, SteelTakeoff t) async {
  final title = TextEditingController(text: t.title);
  final dwg = TextEditingController(text: t.drawingNo);
  final wastage = TextEditingController(text: t.wastagePercent == 0 ? '' : fmtNum(t.wastagePercent));
  var status = t.status;
  var date = t.date;
  final ok = await showDialog<bool>(
    context: context,
    builder: (c) => StatefulBuilder(
      builder: (c, setD) => AlertDialog(
        title: const Text('Take-off details'),
        content: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(controller: title, decoration: const InputDecoration(labelText: 'Title (e.g. Tower A roof truss)')),
              TextField(controller: dwg, decoration: const InputDecoration(labelText: 'Drawing no.')),
              const SizedBox(height: 8),
              DropdownButtonFormField<TakeoffStatus>(
                initialValue: status,
                decoration: const InputDecoration(labelText: 'Status'),
                items: [for (final s in TakeoffStatus.values) DropdownMenuItem(value: s, child: Text(s.label))],
                onChanged: (s) => setD(() => status = s ?? status),
              ),
              TextField(
                controller: wastage,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                decoration: const InputDecoration(labelText: 'Wastage / cutting allowance (%)'),
              ),
              ListTile(
                contentPadding: EdgeInsets.zero,
                leading: const Icon(Icons.event),
                title: Text(dateFmt.format(date)),
                onTap: () async {
                  final d = await showDatePicker(
                    context: c,
                    firstDate: DateTime(2000),
                    lastDate: DateTime(2100),
                    initialDate: date,
                  );
                  if (d != null) setD(() => date = d);
                },
              ),
            ],
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: const Text('Save')),
        ],
      ),
    ),
  );
  if (ok == true) {
    t
      ..title = title.text.trim()
      ..drawingNo = dwg.text.trim()
      ..status = status
      ..date = date
      ..wastagePercent = double.tryParse(wastage.text.trim()) ?? 0;
  }
  return ok == true;
}

enum _ItemAction { save, delete, duplicate }

/// Edits a take-off item in place. For IS sizes the section is fixed (pick
/// again to change it); custom items take their own designation and kg/m.
Future<_ItemAction?> _editTakeoffItem(BuildContext context, SteelTakeoffItem item,
    {bool isNew = false, bool custom = false}) async {
  final designation = TextEditingController(text: item.designation);
  final kgPerM = TextEditingController(text: item.kgPerM == 0 ? '' : item.kgPerM.toString());
  final length = TextEditingController(text: item.lengthM == 0 ? '' : fmtNum(item.lengthM));
  final nos = TextEditingController(text: item.nos.toString());
  final mark = TextEditingController(text: item.memberMark);
  final location = TextEditingController(text: item.location);
  final remarks = TextEditingController(text: item.remarks);
  double n(TextEditingController c) => double.tryParse(c.text.trim().replaceAll(',', '')) ?? 0;

  final action = await showDialog<_ItemAction>(
    context: context,
    builder: (c) => StatefulBuilder(builder: (c, setD) {
      final w = (custom ? n(kgPerM) : item.kgPerM) * n(length) * n(nos);
      final ref = item.ref;
      return AlertDialog(
        title: Text(isNew ? 'Add member' : 'Edit member'),
        content: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              if (custom) ...[
                TextField(controller: designation, decoration: const InputDecoration(labelText: 'Description / size')),
                TextField(
                  controller: kgPerM,
                  keyboardType: const TextInputType.numberWithOptions(decimal: true),
                  decoration: const InputDecoration(labelText: 'Unit weight (kg/m)'),
                  onChanged: (_) => setD(() {}),
                ),
              ] else ...[
                Text(item.designation, style: Theme.of(c).textTheme.titleMedium),
                Text('${kgPerMText(item.kgPerM)} kg/m · ${ref?.family.standard ?? ''}'),
              ],
              TextField(controller: mark, decoration: const InputDecoration(labelText: 'Member mark (C1, B12…)')),
              Row(
                children: [
                  Expanded(
                    child: TextField(
                      controller: length,
                      autofocus: !custom,
                      keyboardType: const TextInputType.numberWithOptions(decimal: true),
                      decoration: const InputDecoration(labelText: 'Length (m)'),
                      onChanged: (_) => setD(() {}),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: TextField(
                      controller: nos,
                      keyboardType: TextInputType.number,
                      decoration: const InputDecoration(labelText: 'Nos'),
                      onChanged: (_) => setD(() {}),
                    ),
                  ),
                ],
              ),
              TextField(controller: location, decoration: const InputDecoration(labelText: 'Location (block / floor / grid)')),
              TextField(controller: remarks, decoration: const InputDecoration(labelText: 'Remarks')),
              const SizedBox(height: 12),
              Text('Weight: ${kgMtText(w)}', style: Theme.of(c).textTheme.titleMedium),
            ],
          ),
        ),
        actions: [
          if (!isNew) ...[
            IconButton(
              tooltip: 'Delete',
              icon: const Icon(Icons.delete_outline),
              onPressed: () => Navigator.pop(c, _ItemAction.delete),
            ),
            IconButton(
              tooltip: 'Duplicate',
              icon: const Icon(Icons.copy_outlined),
              onPressed: () => Navigator.pop(c, _ItemAction.duplicate),
            ),
          ],
          TextButton(onPressed: () => Navigator.pop(c), child: const Text('Cancel')),
          FilledButton(
            onPressed: () {
              final kg = custom ? n(kgPerM) : item.kgPerM;
              if (kg <= 0 || n(length) <= 0 || n(nos) <= 0 || (custom && designation.text.trim().isEmpty)) {
                ScaffoldMessenger.of(c).showSnackBar(
                  const SnackBar(content: Text('Enter size, kg/m, length and nos')),
                );
                return;
              }
              Navigator.pop(c, _ItemAction.save);
            },
            child: const Text('Save'),
          ),
        ],
      );
    }),
  );
  if (action == _ItemAction.save || action == _ItemAction.duplicate) {
    if (custom) {
      item
        ..designation = designation.text.trim()
        ..kgPerM = n(kgPerM);
    }
    item
      ..lengthM = n(length)
      ..nos = n(nos).round()
      ..memberMark = mark.text.trim()
      ..location = location.text.trim()
      ..remarks = remarks.text.trim();
  }
  return action;
}

/// Posts a take-off into one of the project's DPRs (work done and/or
/// materials). Returns true when posted.
Future<bool> postTakeoffDialog(BuildContext context, SteelTakeoff t, {DailyReport? report}) async {
  final state = context.read<AppState>();
  final reports = state.reportsFor(t.projectId).where((r) => !r.isLocked).toList();
  if (t.netKg <= 0) {
    ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Add members to the take-off first.')));
    return false;
  }
  if (reports.isEmpty) {
    ScaffoldMessenger.of(context).showSnackBar(
      const SnackBar(content: Text('No open DPR in this project. Create a DPR first (approved DPRs are locked).')),
    );
    return false;
  }
  final posted = t.postings.fold(0.0, (s, p) => s + p.mt);
  final remaining = t.netMt - posted;
  final mt = TextEditingController(text: (remaining > 0 ? remaining : t.netMt).toStringAsFixed(3));
  var target = reports.firstWhere((r) => r.id == report?.id, orElse: () => reports.first);
  var asWork = true;
  var asMaterial = false;

  final ok = await showDialog<bool>(
    context: context,
    builder: (c) => StatefulBuilder(
      builder: (c, setD) => AlertDialog(
        title: const Text('Post to DPR'),
        content: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('Net ${fmtNum(t.netMt)} MT · already posted ${fmtNum(posted)} MT'),
              const SizedBox(height: 8),
              DropdownButtonFormField<String>(
                initialValue: target.id,
                isExpanded: true,
                decoration: const InputDecoration(labelText: 'DPR'),
                items: [
                  for (final r in reports)
                    DropdownMenuItem(
                      value: r.id,
                      child: Text('DPR-${r.reportNo.toString().padLeft(3, '0')} · ${dateFmt.format(r.date)}'
                          '${t.postedTo(r.id) ? '  (posted before)' : ''}'),
                    ),
                ],
                onChanged: (id) => setD(() => target = reports.firstWhere((r) => r.id == id)),
              ),
              TextField(
                controller: mt,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                decoration: const InputDecoration(labelText: 'Quantity (MT)'),
              ),
              CheckboxListTile(
                contentPadding: EdgeInsets.zero,
                value: asWork,
                onChanged: (v) => setD(() => asWork = v ?? false),
                title: const Text('Work done'),
                subtitle: const Text('Structural steel erection, unit MT'),
              ),
              CheckboxListTile(
                contentPadding: EdgeInsets.zero,
                value: asMaterial,
                onChanged: (v) => setD(() => asMaterial = v ?? false),
                title: const Text('Materials received'),
                subtitle: const Text('Family-wise, e.g. Steel – ISMB, unit MT'),
              ),
              if (t.postedTo(target.id))
                Text('This take-off was already posted to this DPR; quantities will be added again.',
                    style: TextStyle(color: Theme.of(c).colorScheme.error)),
            ],
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: const Text('Post')),
        ],
      ),
    ),
  );
  final qty = double.tryParse(mt.text.trim()) ?? 0;
  if (ok != true || qty <= 0 || (!asWork && !asMaterial)) return false;
  await state.postTakeoffToReport(t, target, mt: qty, asWork: asWork, asMaterial: asMaterial);
  if (context.mounted) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text('Posted ${fmtNum(qty)} MT to DPR-${target.reportNo.toString().padLeft(3, '0')}')),
    );
  }
  return true;
}
