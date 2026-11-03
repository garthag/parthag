import 'package:flutter/material.dart';

enum FieldKind { text, multiline, number, integer, choice, suggest }

/// Declarative description of one form field.
class FieldSpec {
  const FieldSpec(
    this.key,
    this.label, {
    this.kind = FieldKind.text,
    this.options = const [],
    this.optionLabel,
    this.onPicked,
    this.hint,
    this.required = false,
    this.suffix,
  });

  final String key;
  final String label;
  final FieldKind kind;
  final List<String> options;

  /// Text shown for an option in the list (value stored is the option).
  final String Function(String)? optionLabel;

  /// Called when an option is picked; returned values patch other fields
  /// (e.g. picking a material fills its IS code and unit).
  final Map<String, dynamic> Function(String value)? onPicked;
  final String? hint;
  final bool required;
  final String? suffix;

  bool get isNumeric => kind == FieldKind.number || kind == FieldKind.integer;
}

/// Shows a bottom-sheet form and returns the edited values, or null if the
/// user cancelled. [initial] is typically a model's `toJson()`.
Future<Map<String, dynamic>?> showEntryForm(
  BuildContext context, {
  required String title,
  required List<FieldSpec> fields,
  Map<String, dynamic> initial = const {},
}) {
  return showModalBottomSheet<Map<String, dynamic>>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    useSafeArea: true,
    builder: (_) => _EntryForm(title: title, fields: fields, initial: initial),
  );
}

class _EntryForm extends StatefulWidget {
  const _EntryForm({required this.title, required this.fields, required this.initial});

  final String title;
  final List<FieldSpec> fields;
  final Map<String, dynamic> initial;

  @override
  State<_EntryForm> createState() => _EntryFormState();
}

class _EntryFormState extends State<_EntryForm> {
  final _formKey = GlobalKey<FormState>();
  final _controllers = <String, TextEditingController>{};
  final _focus = <String, FocusNode>{};
  final _choices = <String, String>{};
  int _choiceGen = 0;

  @override
  void initState() {
    super.initState();
    for (final f in widget.fields) {
      final v = widget.initial[f.key];
      if (f.kind == FieldKind.choice) {
        final s = v == null ? '' : '$v';
        _choices[f.key] = f.options.contains(s) ? s : (f.options.isEmpty ? '' : f.options.first);
      } else {
        _controllers[f.key] = TextEditingController(text: _fmt(v, f));
        _focus[f.key] = FocusNode();
      }
    }
  }

  String _fmt(dynamic v, FieldSpec f) {
    if (v == null) return '';
    if (v is num) {
      if (v == 0 && f.isNumeric) return '';
      return v == v.roundToDouble() ? v.toInt().toString() : v.toString();
    }
    return '$v';
  }

  @override
  void dispose() {
    for (final c in _controllers.values) {
      c.dispose();
    }
    for (final f in _focus.values) {
      f.dispose();
    }
    super.dispose();
  }

  void _applyPatch(Map<String, dynamic> patch) {
    setState(() {
      patch.forEach((k, v) {
        final spec = widget.fields.where((f) => f.key == k).firstOrNull;
        if (spec == null) return;
        if (spec.kind == FieldKind.choice) {
          if (spec.options.contains('$v')) _choices[k] = '$v';
          _choiceGen++;
        } else {
          _controllers[k]?.text = _fmt(v, spec);
        }
      });
    });
  }

  void _submit() {
    if (!_formKey.currentState!.validate()) return;
    final out = Map<String, dynamic>.from(widget.initial);
    for (final f in widget.fields) {
      switch (f.kind) {
        case FieldKind.choice:
          out[f.key] = _choices[f.key];
        case FieldKind.number:
          out[f.key] = double.tryParse(_controllers[f.key]!.text.trim()) ?? 0.0;
        case FieldKind.integer:
          out[f.key] = int.tryParse(_controllers[f.key]!.text.trim()) ?? 0;
        default:
          out[f.key] = _controllers[f.key]!.text.trim();
      }
    }
    Navigator.pop(context, out);
  }

  String? _validate(FieldSpec f, String? v) {
    final t = (v ?? '').trim();
    if (f.required && t.isEmpty) return 'Required';
    if (t.isNotEmpty && f.kind == FieldKind.number && double.tryParse(t) == null) return 'Enter a number';
    if (t.isNotEmpty && f.kind == FieldKind.integer && int.tryParse(t) == null) return 'Enter a whole number';
    return null;
  }

  Widget _field(FieldSpec f) {
    final deco = InputDecoration(
      labelText: f.required ? '${f.label} *' : f.label,
      hintText: f.hint,
      suffixText: f.suffix,
      border: const OutlineInputBorder(),
      isDense: true,
    );
    switch (f.kind) {
      case FieldKind.choice:
        return DropdownButtonFormField<String>(
          key: ValueKey('${f.key}-$_choiceGen'),
          initialValue: _choices[f.key]!.isEmpty ? null : _choices[f.key],
          isExpanded: true,
          decoration: deco,
          items: [
            for (final o in f.options)
              DropdownMenuItem(value: o, child: Text(f.optionLabel?.call(o) ?? o, overflow: TextOverflow.ellipsis)),
          ],
          onChanged: (v) {
            if (v == null) return;
            _choices[f.key] = v;
            final patch = f.onPicked?.call(v);
            if (patch != null) _applyPatch(patch);
          },
        );
      case FieldKind.suggest:
        return RawAutocomplete<String>(
          textEditingController: _controllers[f.key],
          focusNode: _focus[f.key],
          optionsBuilder: (value) {
            final q = value.text.toLowerCase();
            return f.options.where((o) => (f.optionLabel?.call(o) ?? o).toLowerCase().contains(q));
          },
          onSelected: (v) {
            final patch = f.onPicked?.call(v);
            if (patch != null) _applyPatch(patch);
          },
          fieldViewBuilder: (context, controller, focus, onSubmit) => TextFormField(
            controller: controller,
            focusNode: focus,
            decoration: deco.copyWith(suffixIcon: const Icon(Icons.arrow_drop_down)),
            textCapitalization: TextCapitalization.sentences,
            validator: (v) => _validate(f, v),
          ),
          optionsViewBuilder: (context, onSelected, options) => Align(
            alignment: Alignment.topLeft,
            child: Material(
              elevation: 4,
              borderRadius: BorderRadius.circular(8),
              child: ConstrainedBox(
                constraints: BoxConstraints(
                  maxHeight: 240,
                  maxWidth: MediaQuery.sizeOf(context).width - 32,
                ),
                child: ListView(
                  padding: EdgeInsets.zero,
                  shrinkWrap: true,
                  children: [
                    for (final o in options)
                      ListTile(
                        dense: true,
                        title: Text(f.optionLabel?.call(o) ?? o),
                        onTap: () => onSelected(o),
                      ),
                  ],
                ),
              ),
            ),
          ),
        );
      default:
        return TextFormField(
          controller: _controllers[f.key],
          decoration: deco,
          maxLines: f.kind == FieldKind.multiline ? 4 : 1,
          minLines: 1,
          textCapitalization:
              f.isNumeric ? TextCapitalization.none : TextCapitalization.sentences,
          keyboardType: f.isNumeric
              ? TextInputType.numberWithOptions(decimal: f.kind == FieldKind.number)
              : (f.kind == FieldKind.multiline ? TextInputType.multiline : TextInputType.text),
          validator: (v) => _validate(f, v),
        );
    }
  }

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: EdgeInsets.only(bottom: MediaQuery.viewInsetsOf(context).bottom),
      child: Form(
        key: _formKey,
        child: ListView(
          shrinkWrap: true,
          padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
          children: [
            Text(widget.title, style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 16),
            for (final f in widget.fields) ...[_field(f), const SizedBox(height: 12)],
            Row(
              children: [
                Expanded(
                  child: OutlinedButton(onPressed: () => Navigator.pop(context), child: const Text('Cancel')),
                ),
                const SizedBox(width: 12),
                Expanded(child: FilledButton(onPressed: _submit, child: const Text('Save'))),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
