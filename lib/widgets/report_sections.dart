import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import 'package:signature/signature.dart';

import '../data/standards.dart';
import '../models/models.dart';
import '../screens/media_gallery.dart';
import '../services/media_service.dart';
import '../state/app_state.dart';
import 'common.dart';
import 'entry_form.dart';

/// Common inputs for every section: the report being edited, a callback to
/// persist edits and whether editing is locked (approved report).
abstract class ReportSection extends StatefulWidget {
  const ReportSection({super.key, required this.report, required this.onChanged});

  final DailyReport report;
  final VoidCallback onChanged;

  bool get locked => report.isLocked;
}

abstract class ReportSectionState<T extends ReportSection> extends State<T> {
  DailyReport get r => widget.report;
  bool get locked => widget.locked;

  void update(VoidCallback fn) {
    setState(fn);
    widget.onChanged();
  }

  Future<void> editList<E>({
    required List<E> list,
    required int? index,
    required String title,
    required List<FieldSpec> fields,
    required Map<String, dynamic> Function(E) toJson,
    required E Function(Map<String, dynamic>) fromJson,
    required E Function() create,
  }) async {
    final initial = toJson(index == null ? create() : list[index]);
    final res = await showEntryForm(context, title: title, fields: fields, initial: initial);
    if (res == null) return;
    update(() => index == null ? list.add(fromJson(res)) : list[index] = fromJson(res));
  }
}

Widget _summaryCard(BuildContext context, List<(String, String)> items) => Card(
      margin: const EdgeInsets.only(bottom: 12),
      color: Theme.of(context).colorScheme.secondaryContainer,
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 12, horizontal: 8),
        child: Row(
          mainAxisAlignment: MainAxisAlignment.spaceAround,
          children: [
            for (final (label, value) in items)
              Column(children: [
                Text(value, style: Theme.of(context).textTheme.titleLarge?.copyWith(fontWeight: FontWeight.bold)),
                Text(label, style: Theme.of(context).textTheme.bodySmall),
              ]),
          ],
        ),
      ),
    );

// ---------------------------------------------------------------- General

class GeneralSection extends ReportSection {
  const GeneralSection({super.key, required super.report, required super.onChanged, required this.project});

  final Project project;

  @override
  State<GeneralSection> createState() => _GeneralSectionState();
}

class _GeneralSectionState extends ReportSectionState<GeneralSection> {
  Widget _weatherPicker(String label, String value, ValueChanged<String> set) => DropdownButtonFormField<String>(
        initialValue: Standards.weather.contains(value) ? value : Standards.weather.first,
        isExpanded: true,
        decoration: InputDecoration(labelText: label),
        items: [for (final w in Standards.weather) DropdownMenuItem(value: w, child: Text(w))],
        onChanged: locked ? null : (v) => update(() => set(v!)),
      );

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    final p = widget.project;
    final w = r.weather;
    return ListView(
      padding: const EdgeInsets.fromLTRB(12, 12, 12, 96),
      children: [
        Card(
          child: Padding(
            padding: const EdgeInsets.all(12),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(p.name, style: t.textTheme.titleMedium),
                const SizedBox(height: 4),
                for (final (k, v) in [
                  ('Client', p.client),
                  ('Contractor', p.contractor),
                  ('PMC / Consultant', p.consultant),
                  ('Work order', p.workOrderNo),
                  ('Location', p.location),
                ])
                  if (v.isNotEmpty) Text('$k: $v', style: t.textTheme.bodySmall),
              ],
            ),
          ),
        ),
        const SizedBox(height: 12),
        Row(
          children: [
            Expanded(
              child: OutlinedButton.icon(
                icon: const Icon(Icons.event),
                label: Text(dateLongFmt.format(r.date)),
                onPressed: locked
                    ? null
                    : () async {
                        final d = await showDatePicker(
                          context: context,
                          firstDate: DateTime(2000),
                          lastDate: DateTime.now(),
                          initialDate: r.date,
                        );
                        if (d != null) update(() => r.date = d);
                      },
              ),
            ),
            const SizedBox(width: 12),
            SizedBox(
              width: 110,
              child: InputDecorator(
                decoration: const InputDecoration(labelText: 'Report No.'),
                child: Text('DPR-${r.reportNo.toString().padLeft(3, '0')}'),
              ),
            ),
          ],
        ),
        const SizedBox(height: 12),
        DropdownButtonFormField<String>(
          initialValue: Standards.shifts.contains(r.shift) ? r.shift : Standards.shifts.first,
          decoration: const InputDecoration(labelText: 'Working shift'),
          items: [for (final s in Standards.shifts) DropdownMenuItem(value: s, child: Text(s))],
          onChanged: locked ? null : (v) => update(() => r.shift = v!),
        ),
        const SizedBox(height: 20),
        Text('Weather', style: t.textTheme.titleMedium),
        const SizedBox(height: 8),
        _weatherPicker('Morning', w.morning, (v) => w.morning = v),
        const SizedBox(height: 12),
        _weatherPicker('Afternoon', w.afternoon, (v) => w.afternoon = v),
        const SizedBox(height: 12),
        _weatherPicker('Evening', w.evening, (v) => w.evening = v),
        const SizedBox(height: 12),
        Row(children: [
          Expanded(
            child: BoundTextField(
              label: 'Min temp',
              suffix: '°C',
              numeric: true,
              enabled: !locked,
              value: numText(w.minTempC),
              onChanged: (v) => update(() => w.minTempC = parseNum(v)),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: BoundTextField(
              label: 'Max temp',
              suffix: '°C',
              numeric: true,
              enabled: !locked,
              value: numText(w.maxTempC),
              onChanged: (v) => update(() => w.maxTempC = parseNum(v)),
            ),
          ),
        ]),
        const SizedBox(height: 12),
        Row(children: [
          Expanded(
            child: BoundTextField(
              label: 'Rainfall',
              suffix: 'mm',
              numeric: true,
              enabled: !locked,
              value: numText(w.rainfallMm),
              onChanged: (v) => update(() => w.rainfallMm = parseNum(v)),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: BoundTextField(
              label: 'Hours lost (weather)',
              suffix: 'h',
              numeric: true,
              enabled: !locked,
              value: numText(w.hoursLost),
              onChanged: (v) => update(() => w.hoursLost = parseNum(v)),
            ),
          ),
        ]),
        const SizedBox(height: 20),
        Text('Today at a glance', style: t.textTheme.titleMedium),
        const SizedBox(height: 8),
        _summaryCard(context, [
          ('Manpower', '${r.totalManpower}'),
          ('Work items', '${r.activities.where((a) => a.todayQty > 0).length}'),
          ('Photos', '${r.photoCount}'),
          ('Hrs lost', fmtNum(r.hindranceHours + w.hoursLost)),
        ]),
      ],
    );
  }
}

// ---------------------------------------------------------------- Manpower

class ManpowerSection extends ReportSection {
  const ManpowerSection({super.key, required super.report, required super.onChanged});

  @override
  State<ManpowerSection> createState() => _ManpowerSectionState();
}

class _ManpowerSectionState extends ReportSectionState<ManpowerSection> {
  static final _byName = {for (final l in Standards.labour) l.name: l};

  List<FieldSpec> get _fields => [
        FieldSpec('agency', 'Agency / Sub-contractor', hint: 'e.g. Main contractor, ABC Labour Supply'),
        FieldSpec(
          'category',
          'Trade / Category',
          kind: FieldKind.suggest,
          required: true,
          options: [for (final l in Standards.labour) l.name],
          optionLabel: (o) => _byName[o]?.display ?? o,
          onPicked: (v) => {'skill': _byName[v]?.skill ?? 'Skilled'},
        ),
        const FieldSpec('skill', 'Skill level (Min. Wages Act)', kind: FieldKind.choice, options: Standards.skillLevels),
        const FieldSpec('male', 'Male', kind: FieldKind.integer),
        const FieldSpec('female', 'Female', kind: FieldKind.integer),
        const FieldSpec('hours', 'Working hours', kind: FieldKind.number, suffix: 'h'),
      ];

  void _edit(int? i) => editList(
        list: r.manpower,
        index: i,
        title: i == null ? 'Add manpower' : 'Edit manpower',
        fields: _fields,
        toJson: (e) => e.toJson(),
        fromJson: ManpowerEntry.fromJson,
        create: () => ManpowerEntry(agency: r.manpower.lastOrNull?.agency ?? ''),
      );

  @override
  Widget build(BuildContext context) {
    final skilled = r.manpower.where((m) => m.skill != 'Unskilled').fold(0, (s, m) => s + m.total);
    return SectionList<ManpowerEntry>(
      items: r.manpower,
      locked: locked,
      header: _summaryCard(context, [
        ('Total', '${r.totalManpower}'),
        ('Skilled', '$skilled'),
        ('Unskilled', '${r.totalManpower - skilled}'),
        ('Man-hours', fmtNum(r.totalManHours)),
      ]),
      emptyIcon: Icons.engineering_outlined,
      emptyText: 'No manpower recorded',
      addLabel: 'Add trade',
      leading: (m) => CircleAvatar(child: Text('${m.total}')),
      title: (m) => m.category,
      subtitle: (m) => [
        if (m.agency.isNotEmpty) m.agency,
        m.skill,
        'M ${m.male} / F ${m.female}',
        '${fmtNum(m.hours)} h',
      ].join(' · '),
      onAdd: () => _edit(null),
      onEdit: _edit,
      onDelete: (i) => update(() => r.manpower.removeAt(i)),
    );
  }
}

// ---------------------------------------------------------------- Machinery

class MachinerySection extends ReportSection {
  const MachinerySection({super.key, required super.report, required super.onChanged});

  @override
  State<MachinerySection> createState() => _MachinerySectionState();
}

class _MachinerySectionState extends ReportSectionState<MachinerySection> {
  static const _fields = [
    FieldSpec('name', 'Equipment', kind: FieldKind.suggest, required: true, options: Standards.machinery),
    FieldSpec('ownership', 'Ownership', kind: FieldKind.choice, options: Standards.ownership),
    FieldSpec('count', 'Number', kind: FieldKind.integer),
    FieldSpec('workingHrs', 'Working hours', kind: FieldKind.number, suffix: 'h'),
    FieldSpec('idleHrs', 'Idle hours', kind: FieldKind.number, suffix: 'h'),
    FieldSpec('breakdownHrs', 'Breakdown hours', kind: FieldKind.number, suffix: 'h'),
    FieldSpec('remarks', 'Remarks', kind: FieldKind.multiline),
  ];

  void _edit(int? i) => editList(
        list: r.machinery,
        index: i,
        title: i == null ? 'Add machinery' : 'Edit machinery',
        fields: _fields,
        toJson: (e) => e.toJson(),
        fromJson: MachineryEntry.fromJson,
        create: MachineryEntry.new,
      );

  @override
  Widget build(BuildContext context) {
    return SectionList<MachineryEntry>(
      items: r.machinery,
      locked: locked,
      emptyIcon: Icons.precision_manufacturing_outlined,
      emptyText: 'No plant & machinery recorded',
      addLabel: 'Add equipment',
      leading: (m) => CircleAvatar(
        backgroundColor: m.breakdownHrs > 0 ? Colors.red.shade100 : null,
        child: Text('${m.count}'),
      ),
      title: (m) => m.name,
      subtitle: (m) => [
        m.ownership,
        'Work ${fmtNum(m.workingHrs)}h',
        'Idle ${fmtNum(m.idleHrs)}h',
        if (m.breakdownHrs > 0) 'Breakdown ${fmtNum(m.breakdownHrs)}h',
        if (m.remarks.isNotEmpty) m.remarks,
      ].join(' · '),
      onAdd: () => _edit(null),
      onEdit: _edit,
      onDelete: (i) => update(() => r.machinery.removeAt(i)),
    );
  }
}

// ---------------------------------------------------------------- Work progress

class WorkSection extends ReportSection {
  const WorkSection({super.key, required super.report, required super.onChanged});

  @override
  State<WorkSection> createState() => _WorkSectionState();
}

class _WorkSectionState extends ReportSectionState<WorkSection> {
  static final _byName = {for (final a in Standards.activities) a.description: a};

  List<FieldSpec> get _fields => [
        const FieldSpec('boqItemNo', 'BOQ / DSR item no.', hint: 'e.g. 5.1.2'),
        FieldSpec(
          'description',
          'Activity / Description',
          kind: FieldKind.suggest,
          required: true,
          options: _byName.keys.toList(),
          optionLabel: (o) => '$o  [${_byName[o]!.reference}]',
          onPicked: (v) => {'unit': _byName[v]?.unit},
        ),
        const FieldSpec('location', 'Location', hint: 'Block / Floor / Grid, e.g. Tower A - 2nd slab'),
        const FieldSpec('unit', 'Unit (IS 1200)', kind: FieldKind.choice, options: Standards.units),
        const FieldSpec('totalScope', 'Total scope (BOQ qty)', kind: FieldKind.number),
        const FieldSpec('previousQty', 'Cumulative up to previous DPR', kind: FieldKind.number),
        const FieldSpec('todayQty', 'Executed today', kind: FieldKind.number, required: true),
        const FieldSpec('remarks', 'Remarks', kind: FieldKind.multiline),
      ];

  /// Cumulative quantity for this activity from earlier reports of the
  /// project, used when an activity is added manually.
  double _previousFromHistory(WorkActivity a) {
    final state = context.read<AppState>();
    for (final rep in state.reportsFor(r.projectId)) {
      if (rep.id == r.id || !rep.date.isBefore(r.date)) continue;
      for (final x in rep.activities) {
        if (x.trackingKey == a.trackingKey) return x.cumulativeQty;
      }
    }
    return a.previousQty;
  }

  Future<void> _edit(int? i) async {
    final initial = (i == null ? WorkActivity() : r.activities[i]).toJson();
    final res = await showEntryForm(context,
        title: i == null ? 'Add work item' : 'Edit work item', fields: _fields, initial: initial);
    if (res == null) return;
    final a = WorkActivity.fromJson(res);
    if (i == null && a.previousQty == 0) a.previousQty = _previousFromHistory(a);
    update(() => i == null ? r.activities.add(a) : r.activities[i] = a);
  }

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    return SectionList<WorkActivity>(
      items: r.activities,
      locked: locked,
      emptyIcon: Icons.construction,
      emptyText: 'No work items recorded',
      addLabel: 'Add work item',
      header: Padding(
        padding: const EdgeInsets.only(bottom: 8),
        child: Text(
          'Quantities measured as per IS 1200. Cumulative = previous + today; '
          'carried forward to the next DPR automatically.',
          style: t.textTheme.bodySmall,
        ),
      ),
      title: (a) => [if (a.boqItemNo.isNotEmpty) a.boqItemNo, a.description].join(' - '),
      subtitle: (a) => [
        if (a.location.isNotEmpty) a.location,
        'Today ${fmtNum(a.todayQty)} ${a.unit}',
        'Cum. ${fmtNum(a.cumulativeQty)}${a.totalScope > 0 ? ' / ${fmtNum(a.totalScope)}' : ''} ${a.unit}',
      ].join('\n'),
      trailing: (a) => a.totalScope <= 0
          ? const SizedBox.shrink()
          : SizedBox(
              width: 44,
              height: 44,
              child: Stack(
                alignment: Alignment.center,
                children: [
                  CircularProgressIndicator(value: a.percentComplete / 100, strokeWidth: 4),
                  Text('${a.percentComplete.round()}%', style: const TextStyle(fontSize: 11)),
                ],
              ),
            ),
      onAdd: () => _edit(null),
      onEdit: _edit,
      onDelete: (i) => update(() => r.activities.removeAt(i)),
    );
  }
}

// ---------------------------------------------------------------- Materials

class MaterialsSection extends ReportSection {
  const MaterialsSection({super.key, required super.report, required super.onChanged});

  @override
  State<MaterialsSection> createState() => _MaterialsSectionState();
}

class _MaterialsSectionState extends ReportSectionState<MaterialsSection> {
  static final _byName = {for (final m in Standards.materials) m.name: m};

  List<FieldSpec> get _fields => [
        FieldSpec(
          'name',
          'Material',
          kind: FieldKind.suggest,
          required: true,
          options: _byName.keys.toList(),
          optionLabel: (o) => '$o  [${_byName[o]!.specification}]',
          onPicked: (v) => {'specification': _byName[v]?.specification, 'unit': _byName[v]?.unit},
        ),
        const FieldSpec('specification', 'Specification / IS code'),
        const FieldSpec('unit', 'Unit', kind: FieldKind.choice, options: Standards.units),
        const FieldSpec('opening', 'Opening stock', kind: FieldKind.number),
        const FieldSpec('received', 'Received today', kind: FieldKind.number),
        const FieldSpec('consumed', 'Consumed today', kind: FieldKind.number),
        const FieldSpec('supplier', 'Supplier'),
        const FieldSpec('challanNo', 'Challan / Invoice / MRN no.'),
      ];

  void _edit(int? i) => editList(
        list: r.materials,
        index: i,
        title: i == null ? 'Add material' : 'Edit material',
        fields: _fields,
        toJson: (e) => e.toJson(),
        fromJson: MaterialEntry.fromJson,
        create: MaterialEntry.new,
      );

  @override
  Widget build(BuildContext context) {
    return SectionList<MaterialEntry>(
      items: r.materials,
      locked: locked,
      emptyIcon: Icons.inventory_2_outlined,
      emptyText: 'No material movement recorded',
      addLabel: 'Add material',
      title: (m) => m.name,
      subtitle: (m) => [
        if (m.specification.isNotEmpty) m.specification,
        'Open ${fmtNum(m.opening)} + Rcvd ${fmtNum(m.received)} − Used ${fmtNum(m.consumed)}',
        if (m.challanNo.isNotEmpty) 'Challan ${m.challanNo}',
      ].join('\n'),
      trailing: (m) => Column(
        mainAxisAlignment: MainAxisAlignment.center,
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          Text(fmtNum(m.closing),
              style: TextStyle(fontWeight: FontWeight.bold, color: m.closing < 0 ? Colors.red : null)),
          Text(m.unit, style: const TextStyle(fontSize: 11)),
        ],
      ),
      onAdd: () => _edit(null),
      onEdit: _edit,
      onDelete: (i) => update(() => r.materials.removeAt(i)),
    );
  }
}

// ---------------------------------------------------------------- Quality

class QualitySection extends ReportSection {
  const QualitySection({super.key, required super.report, required super.onChanged});

  @override
  State<QualitySection> createState() => _QualitySectionState();
}

class _QualitySectionState extends ReportSectionState<QualitySection> {
  static final _byName = {for (final t in Standards.qualityTests) t.name: t};

  List<FieldSpec> get _fields => [
        FieldSpec(
          'testType',
          'Test / Inspection',
          kind: FieldKind.suggest,
          required: true,
          options: _byName.keys.toList(),
          optionLabel: (o) => '$o  [${_byName[o]!.standard}]',
          onPicked: (v) => {'standard': _byName[v]?.standard},
        ),
        const FieldSpec('location', 'Location / Element'),
        const FieldSpec('grade', 'Grade', kind: FieldKind.suggest, options: Standards.concreteGrades, hint: 'e.g. M25, Fe500D'),
        const FieldSpec('quantity', 'Quantity (cum / nos)', kind: FieldKind.number),
        const FieldSpec('samples', 'Cubes cast / samples', kind: FieldKind.integer),
        const FieldSpec('result', 'Result', kind: FieldKind.choice, options: Standards.qualityResults),
        const FieldSpec('standard', 'Reference standard'),
        const FieldSpec('remarks', 'Remarks (slump, strength, observations)', kind: FieldKind.multiline),
      ];

  void _edit(int? i) => editList(
        list: r.quality,
        index: i,
        title: i == null ? 'Add QC record' : 'Edit QC record',
        fields: _fields,
        toJson: (e) => e.toJson(),
        fromJson: QualityEntry.fromJson,
        create: QualityEntry.new,
      );

  @override
  Widget build(BuildContext context) {
    Color resultColor(String r) => switch (r) {
          'Pass' => Colors.green,
          'Fail' => Colors.red,
          'Conditional' => Colors.orange,
          _ => Colors.grey,
        };
    return SectionList<QualityEntry>(
      items: r.quality,
      locked: locked,
      emptyIcon: Icons.verified_outlined,
      emptyText: 'No QC tests or inspections',
      addLabel: 'Add QC record',
      leading: (q) => Icon(Icons.science_outlined, color: resultColor(q.result)),
      title: (q) => q.testType,
      subtitle: (q) => [
        if (q.location.isNotEmpty) q.location,
        if (q.grade.isNotEmpty) q.grade,
        if (q.quantity > 0) 'Qty ${fmtNum(q.quantity)}',
        if (q.samples > 0) '${q.samples} samples',
        if (q.standard.isNotEmpty) q.standard,
      ].join(' · '),
      trailing: (q) => Text(q.result, style: TextStyle(color: resultColor(q.result), fontWeight: FontWeight.bold)),
      onAdd: () => _edit(null),
      onEdit: _edit,
      onDelete: (i) => update(() => r.quality.removeAt(i)),
    );
  }
}

// ---------------------------------------------------------------- Safety

class SafetySection extends ReportSection {
  const SafetySection({super.key, required super.report, required super.onChanged});

  @override
  State<SafetySection> createState() => _SafetySectionState();
}

class _SafetySectionState extends ReportSectionState<SafetySection> {
  static const _incidentFields = [
    FieldSpec('type', 'Incident type', kind: FieldKind.choice, options: Standards.incidentTypes),
    FieldSpec('description', 'What happened', kind: FieldKind.multiline, required: true),
    FieldSpec('actionTaken', 'Corrective action taken', kind: FieldKind.multiline),
  ];

  Future<void> _editIncident(int? i) => editList(
        list: r.safety.incidents,
        index: i,
        title: i == null ? 'Report incident' : 'Edit incident',
        fields: _incidentFields,
        toJson: (e) => e.toJson(),
        fromJson: SafetyIncident.fromJson,
        create: SafetyIncident.new,
      );

  Widget _chips(List<String> options, List<String> selected) => Wrap(
        spacing: 6,
        runSpacing: 6,
        children: [
          for (final o in options)
            FilterChip(
              label: Text(o),
              selected: selected.contains(o),
              onSelected: locked ? null : (v) => update(() => v ? selected.add(o) : selected.remove(o)),
            ),
        ],
      );

  @override
  Widget build(BuildContext context) {
    final s = r.safety;
    final t = Theme.of(context);
    return ListView(
      padding: const EdgeInsets.fromLTRB(12, 12, 12, 96),
      children: [
        Card(
          color: s.isSafeDay ? Colors.green.shade50 : Colors.red.shade50,
          child: ListTile(
            leading: Icon(s.isSafeDay ? Icons.verified_user : Icons.warning_amber,
                color: s.isSafeDay ? Colors.green : Colors.red),
            title: Text(s.isSafeDay ? 'Safe day' : 'Reportable incident recorded',
                style: const TextStyle(color: Colors.black87)),
            subtitle: Text('Safe man-hours today: ${s.isSafeDay ? fmtNum(r.totalManHours) : 0}',
                style: const TextStyle(color: Colors.black54)),
          ),
        ),
        const SizedBox(height: 8),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Toolbox talk conducted'),
          value: s.toolboxTalk,
          onChanged: locked ? null : (v) => update(() => s.toolboxTalk = v),
        ),
        if (s.toolboxTalk) ...[
          Autocomplete<String>(
            initialValue: TextEditingValue(text: s.toolboxTopic),
            optionsBuilder: (v) => Standards.toolboxTopics
                .where((o) => o.toLowerCase().contains(v.text.toLowerCase())),
            onSelected: (v) => update(() => s.toolboxTopic = v),
            fieldViewBuilder: (context, c, f, _) => TextField(
              controller: c,
              focusNode: f,
              enabled: !locked,
              decoration: const InputDecoration(labelText: 'Topic'),
              onChanged: (v) => update(() => s.toolboxTopic = v),
            ),
          ),
          const SizedBox(height: 12),
          BoundTextField(
            label: 'Attendees',
            numeric: true,
            enabled: !locked,
            value: s.toolboxAttendees == 0 ? '' : '${s.toolboxAttendees}',
            onChanged: (v) => update(() => s.toolboxAttendees = parseNum(v).round()),
          ),
        ],
        const SizedBox(height: 16),
        Text('PPE compliance: ${s.ppeCompliancePct.round()}%', style: t.textTheme.titleSmall),
        Slider(
          value: s.ppeCompliancePct.clamp(0, 100).toDouble(),
          max: 100,
          divisions: 20,
          label: '${s.ppeCompliancePct.round()}%',
          onChanged: locked ? null : (v) => update(() => s.ppeCompliancePct = v),
        ),
        Text('PPE checked on site', style: t.textTheme.titleSmall),
        const SizedBox(height: 6),
        _chips(Standards.ppe, s.ppeChecked),
        const SizedBox(height: 16),
        Text('Work permits issued', style: t.textTheme.titleSmall),
        const SizedBox(height: 6),
        _chips(Standards.permits, s.permits),
        const SizedBox(height: 16),
        Row(
          children: [
            Expanded(child: Text('Incidents / observations', style: t.textTheme.titleSmall)),
            if (!locked)
              TextButton.icon(
                onPressed: () => _editIncident(null),
                icon: const Icon(Icons.add),
                label: const Text('Report'),
              ),
          ],
        ),
        if (s.incidents.isEmpty) const Text('No incidents reported.'),
        for (final (i, inc) in s.incidents.indexed)
          Card(
            margin: const EdgeInsets.only(bottom: 8),
            child: ListTile(
              leading: const Icon(Icons.report_outlined, color: Colors.red),
              title: Text(inc.type),
              subtitle: Text([inc.description, if (inc.actionTaken.isNotEmpty) 'Action: ${inc.actionTaken}'].join('\n')),
              onTap: locked ? null : () => _editIncident(i),
              onLongPress: locked
                  ? null
                  : () async {
                      if (await confirm(context, 'Delete incident?', inc.type)) {
                        update(() => s.incidents.removeAt(i));
                      }
                    },
            ),
          ),
        const SizedBox(height: 12),
        BoundTextField(
          label: 'Safety observations / housekeeping',
          maxLines: 4,
          enabled: !locked,
          value: s.observations,
          onChanged: (v) => update(() => s.observations = v),
        ),
      ],
    );
  }
}

// ---------------------------------------------------------------- Hindrances & visitors

class HindranceSection extends ReportSection {
  const HindranceSection({super.key, required super.report, required super.onChanged});

  @override
  State<HindranceSection> createState() => _HindranceSectionState();
}

class _HindranceSectionState extends ReportSectionState<HindranceSection> {
  static const _fields = [
    FieldSpec('category', 'Hindrance type', kind: FieldKind.suggest, required: true, options: Standards.hindranceCategories),
    FieldSpec('description', 'Details', kind: FieldKind.multiline),
    FieldSpec('hoursLost', 'Hours lost', kind: FieldKind.number, suffix: 'h'),
    FieldSpec('responsibility', 'Responsibility', kind: FieldKind.choice, options: Standards.responsibility),
    FieldSpec('actionRequired', 'Action required / by whom', kind: FieldKind.multiline),
  ];

  static const _visitorFields = [
    FieldSpec('name', 'Name', required: true),
    FieldSpec('organisation', 'Organisation / Designation'),
    FieldSpec('purpose', 'Purpose of visit', kind: FieldKind.multiline),
  ];

  void _edit(int? i) => editList(
        list: r.hindrances,
        index: i,
        title: i == null ? 'Add hindrance' : 'Edit hindrance',
        fields: _fields,
        toJson: (e) => e.toJson(),
        fromJson: Hindrance.fromJson,
        create: Hindrance.new,
      );

  void _editVisitor(int? i) => editList(
        list: r.visitors,
        index: i,
        title: i == null ? 'Add visitor' : 'Edit visitor',
        fields: _visitorFields,
        toJson: (e) => e.toJson(),
        fromJson: Visitor.fromJson,
        create: Visitor.new,
      );

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    return SectionList<Hindrance>(
      items: r.hindrances,
      locked: locked,
      emptyIcon: Icons.do_not_disturb_on_outlined,
      emptyText: 'No hindrances today',
      addLabel: 'Add hindrance',
      header: Text(
        'Record delays for the hindrance register (useful for Extension of Time claims).',
        style: t.textTheme.bodySmall,
      ),
      leading: (h) => const Icon(Icons.report_problem_outlined, color: Colors.orange),
      title: (h) => h.category,
      subtitle: (h) => [
        if (h.description.isNotEmpty) h.description,
        '${fmtNum(h.hoursLost)} h lost · ${h.responsibility}',
        if (h.actionRequired.isNotEmpty) 'Action: ${h.actionRequired}',
      ].join('\n'),
      onAdd: () => _edit(null),
      onEdit: _edit,
      onDelete: (i) => update(() => r.hindrances.removeAt(i)),
      extraActions: [
        OutlinedButton.icon(
          onPressed: () => _editVisitor(null),
          icon: const Icon(Icons.badge_outlined),
          label: Text('Add visitor (${r.visitors.length})'),
        ),
      ],
      footer: r.visitors.isEmpty
          ? null
          : Padding(
              padding: const EdgeInsets.only(top: 20),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('Visitors', style: t.textTheme.titleSmall),
                  for (final (i, v) in r.visitors.indexed)
                    Card(
                      margin: const EdgeInsets.only(top: 8),
                      child: ListTile(
                        leading: const Icon(Icons.badge_outlined),
                        title: Text(v.name),
                        subtitle: Text([v.organisation, v.purpose].where((s) => s.isNotEmpty).join(' · ')),
                        onTap: locked ? null : () => _editVisitor(i),
                        onLongPress: locked
                            ? null
                            : () async {
                                if (await confirm(context, 'Remove visitor?', v.name)) {
                                  update(() => r.visitors.removeAt(i));
                                }
                              },
                      ),
                    ),
                ],
              ),
            ),
    );
  }
}

// ---------------------------------------------------------------- Media

class MediaSection extends ReportSection {
  const MediaSection({super.key, required super.report, required super.onChanged});

  @override
  State<MediaSection> createState() => _MediaSectionState();
}

class _MediaSectionState extends ReportSectionState<MediaSection> {
  static final _hasCamera = !kIsWeb && (Platform.isAndroid || Platform.isIOS);

  @override
  Widget build(BuildContext context) {
    context.watch<AppState>();
    Widget btn(IconData icon, String label, CaptureMode mode) => Expanded(
          child: FilledButton.tonal(
            style: FilledButton.styleFrom(padding: const EdgeInsets.symmetric(vertical: 12)),
            onPressed: () async {
              await captureMedia(context, r, mode);
              if (mounted) setState(() {});
            },
            child: Column(children: [Icon(icon), const SizedBox(height: 4), Text(label, style: const TextStyle(fontSize: 12))]),
          ),
        );
    return MediaGrid(
      items: [for (final m in r.media.reversed) (r, m)],
      header: locked
          ? null
          : Padding(
              padding: const EdgeInsets.fromLTRB(12, 12, 12, 4),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(children: [
                    // Phones and tablets only: desktop (Windows) has no camera capture.
                    if (_hasCamera) ...[
                      btn(Icons.photo_camera, 'Photo', CaptureMode.cameraPhoto),
                      const SizedBox(width: 8),
                      btn(Icons.videocam, 'Video', CaptureMode.cameraVideo),
                      const SizedBox(width: 8),
                    ],
                    btn(Icons.photo_library, 'Gallery', CaptureMode.galleryPhotos),
                    const SizedBox(width: 8),
                    btn(Icons.video_library, 'Video file', CaptureMode.galleryVideo),
                  ]),
                  const SizedBox(height: 6),
                  Text('Photos are time-stamped and geo-tagged. Tag by category, trade and work item.',
                      style: Theme.of(context).textTheme.bodySmall),
                ],
              ),
            ),
    );
  }
}

// ---------------------------------------------------------------- Notes & sign-off

class SignOffSection extends ReportSection {
  const SignOffSection({super.key, required super.report, required super.onChanged, required this.onStatus});

  final ValueChanged<ReportStatus> onStatus;

  @override
  State<SignOffSection> createState() => _SignOffSectionState();
}

class _SignOffSectionState extends ReportSectionState<SignOffSection> {
  final _plan = TextEditingController();

  @override
  void dispose() {
    _plan.dispose();
    super.dispose();
  }

  void _addPlan() {
    final t = _plan.text.trim();
    if (t.isEmpty) return;
    update(() => r.tomorrowPlan.add(t));
    _plan.clear();
  }

  Future<void> _sign(SignOff s, String role) async {
    final media = context.read<MediaService>();
    final controller = SignatureController(
      penStrokeWidth: 3,
      penColor: Colors.black,
      exportBackgroundColor: Colors.white,
    );
    final name = TextEditingController(text: s.name);
    final designation = TextEditingController(text: s.designation);
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text('$role signature'),
        content: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(controller: name, decoration: const InputDecoration(labelText: 'Name')),
              const SizedBox(height: 8),
              TextField(controller: designation, decoration: const InputDecoration(labelText: 'Designation')),
              const SizedBox(height: 12),
              Container(
                decoration: BoxDecoration(border: Border.all(color: Colors.grey)),
                child: Signature(controller: controller, height: 160, width: 280, backgroundColor: Colors.white),
              ),
              TextButton(onPressed: controller.clear, child: const Text('Clear')),
            ],
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: const Text('Sign')),
        ],
      ),
    );
    if (ok == true) {
      final png = controller.isEmpty ? null : await controller.toPngBytes();
      final path = png == null ? '' : await media.saveBytes(png, r.id, 'sign_${role.toLowerCase().replaceAll(' ', '_')}_${newId()}.png');
      update(() {
        s
          ..name = name.text.trim()
          ..designation = designation.text.trim();
        if (path.isNotEmpty) {
          s
            ..signaturePath = path
            ..signedAt = DateTime.now();
        }
      });
    }
    controller.dispose();
    name.dispose();
    designation.dispose();
  }

  Widget _signCard(String role, SignOff s) => Card(
        margin: const EdgeInsets.only(bottom: 8),
        child: ListTile(
          leading: s.isSigned
              ? SizedBox(width: 64, height: 40, child: localImage(s.signaturePath, fit: BoxFit.contain))
              : const Icon(Icons.draw_outlined),
          title: Text(role),
          subtitle: Text([
            s.name.isEmpty ? 'Not set' : s.name,
            if (s.designation.isNotEmpty) s.designation,
            if (s.signedAt != null) 'Signed ${timeFmt.format(s.signedAt!)}',
          ].join(' · ')),
          trailing: locked ? null : const Icon(Icons.edit),
          onTap: locked ? null : () => _sign(s, role),
        ),
      );

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    return ListView(
      padding: const EdgeInsets.fromLTRB(12, 12, 12, 96),
      children: [
        BoundTextField(
          label: 'Instructions received (Client / PMC)',
          maxLines: 4,
          enabled: !locked,
          value: r.instructions,
          onChanged: (v) => update(() => r.instructions = v),
        ),
        const SizedBox(height: 12),
        BoundTextField(
          label: 'General remarks',
          maxLines: 4,
          enabled: !locked,
          value: r.remarks,
          onChanged: (v) => update(() => r.remarks = v),
        ),
        const SizedBox(height: 16),
        Text('Plan for tomorrow', style: t.textTheme.titleSmall),
        for (final (i, p) in r.tomorrowPlan.indexed)
          ListTile(
            dense: true,
            contentPadding: EdgeInsets.zero,
            leading: CircleAvatar(radius: 12, child: Text('${i + 1}', style: const TextStyle(fontSize: 11))),
            title: Text(p),
            trailing: locked
                ? null
                : IconButton(icon: const Icon(Icons.close), onPressed: () => update(() => r.tomorrowPlan.removeAt(i))),
          ),
        if (!locked)
          Row(children: [
            Expanded(
              child: TextField(
                controller: _plan,
                textCapitalization: TextCapitalization.sentences,
                decoration: const InputDecoration(labelText: 'Add planned activity'),
                onSubmitted: (_) => _addPlan(),
              ),
            ),
            IconButton.filledTonal(onPressed: _addPlan, icon: const Icon(Icons.add)),
          ]),
        const SizedBox(height: 20),
        Text('Sign-off', style: t.textTheme.titleSmall),
        const SizedBox(height: 6),
        _signCard('Prepared by', r.preparedBy),
        _signCard('Checked by', r.checkedBy),
        _signCard('Approved by', r.approvedBy),
        const SizedBox(height: 16),
        Text('Report status', style: t.textTheme.titleSmall),
        const SizedBox(height: 6),
        SegmentedButton<ReportStatus>(
          segments: const [
            ButtonSegment(value: ReportStatus.draft, label: Text('Draft'), icon: Icon(Icons.edit_note)),
            ButtonSegment(value: ReportStatus.submitted, label: Text('Submitted'), icon: Icon(Icons.send)),
            ButtonSegment(value: ReportStatus.approved, label: Text('Approved'), icon: Icon(Icons.verified)),
          ],
          selected: {r.status},
          onSelectionChanged: (s) => widget.onStatus(s.first),
        ),
        const SizedBox(height: 6),
        Text('Approved reports are locked for editing. Switch back to Draft to make changes.',
            style: t.textTheme.bodySmall),
      ],
    );
  }
}
