import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../models/models.dart';
import '../state/app_state.dart';
import '../widgets/common.dart';

class ProjectFormScreen extends StatefulWidget {
  const ProjectFormScreen({super.key, this.project});

  final Project? project;

  @override
  State<ProjectFormScreen> createState() => _ProjectFormScreenState();
}

class _ProjectFormScreenState extends State<ProjectFormScreen> {
  final _form = GlobalKey<FormState>();
  late final Project _p;
  late final Map<String, TextEditingController> _c;

  @override
  void initState() {
    super.initState();
    final state = context.read<AppState>();
    _p = widget.project == null
        ? Project(name: '', siteEngineer: state.profile.name, contractor: state.profile.company)
        : Project.fromJson(widget.project!.toJson());
    _c = {
      'name': TextEditingController(text: _p.name),
      'code': TextEditingController(text: _p.code),
      'client': TextEditingController(text: _p.client),
      'contractor': TextEditingController(text: _p.contractor),
      'consultant': TextEditingController(text: _p.consultant),
      'workOrderNo': TextEditingController(text: _p.workOrderNo),
      'location': TextEditingController(text: _p.location),
      'siteEngineer': TextEditingController(text: _p.siteEngineer),
      'value': TextEditingController(text: numText(_p.contractValueLakh)),
    };
  }

  @override
  void dispose() {
    for (final c in _c.values) {
      c.dispose();
    }
    super.dispose();
  }

  Future<void> _pickDate(bool start) async {
    final now = DateTime.now();
    final d = await showDatePicker(
      context: context,
      firstDate: DateTime(2000),
      lastDate: DateTime(now.year + 15),
      initialDate: (start ? _p.startDate : _p.completionDate) ?? now,
    );
    if (d != null) setState(() => start ? _p.startDate = d : _p.completionDate = d);
  }

  Future<void> _save() async {
    if (!_form.currentState!.validate()) return;
    _p
      ..name = _c['name']!.text.trim()
      ..code = _c['code']!.text.trim()
      ..client = _c['client']!.text.trim()
      ..contractor = _c['contractor']!.text.trim()
      ..consultant = _c['consultant']!.text.trim()
      ..workOrderNo = _c['workOrderNo']!.text.trim()
      ..location = _c['location']!.text.trim()
      ..siteEngineer = _c['siteEngineer']!.text.trim()
      ..contractValueLakh = parseNum(_c['value']!.text);
    await context.read<AppState>().saveProject(_p);
    if (mounted) Navigator.pop(context, _p);
  }

  Widget _text(String key, String label, {bool required = false, bool numeric = false, String? suffix}) =>
      Padding(
        padding: const EdgeInsets.only(bottom: 12),
        child: TextFormField(
          controller: _c[key],
          textCapitalization: TextCapitalization.words,
          keyboardType: numeric ? const TextInputType.numberWithOptions(decimal: true) : null,
          decoration: InputDecoration(labelText: required ? '$label *' : label, suffixText: suffix),
          validator: required ? (v) => (v ?? '').trim().isEmpty ? 'Required' : null : null,
        ),
      );

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: Text(widget.project == null ? 'New project' : 'Edit project')),
      body: Form(
        key: _form,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            _text('name', 'Project name', required: true),
            _text('code', 'Project code'),
            _text('location', 'Site location / address'),
            _text('client', 'Client / Employer'),
            _text('contractor', 'Contractor'),
            _text('consultant', 'PMC / Consultant / Architect'),
            _text('workOrderNo', 'Work order / LOA no.'),
            _text('value', 'Contract value', numeric: true, suffix: 'Rs. lakh'),
            _text('siteEngineer', 'Site engineer in-charge'),
            Row(
              children: [
                Expanded(
                  child: OutlinedButton.icon(
                    onPressed: () => _pickDate(true),
                    icon: const Icon(Icons.event),
                    label: Text(_p.startDate == null ? 'Start date' : 'Start: ${dateFmt.format(_p.startDate!)}'),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: OutlinedButton.icon(
                    onPressed: () => _pickDate(false),
                    icon: const Icon(Icons.flag_outlined),
                    label: Text(_p.completionDate == null
                        ? 'Completion date'
                        : 'End: ${dateFmt.format(_p.completionDate!)}'),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 24),
            FilledButton.icon(onPressed: _save, icon: const Icon(Icons.save), label: const Text('Save project')),
          ],
        ),
      ),
    );
  }
}
