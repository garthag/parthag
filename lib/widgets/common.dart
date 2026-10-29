import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../models/models.dart';

final dateFmt = DateFormat('dd-MM-yyyy');
final dateLongFmt = DateFormat('EEE, dd MMM yyyy');
final timeFmt = DateFormat('dd-MM-yyyy HH:mm');
final _numFmt = NumberFormat('#,##,##0.##', 'en_IN');

String fmtNum(num v) => _numFmt.format(v);

Future<bool> confirm(BuildContext context, String title, String message,
    {String action = 'Delete'}) async {
  final ok = await showDialog<bool>(
    context: context,
    builder: (c) => AlertDialog(
      title: Text(title),
      content: Text(message),
      actions: [
        TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Cancel')),
        FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(action)),
      ],
    ),
  );
  return ok ?? false;
}

class EmptyState extends StatelessWidget {
  const EmptyState({super.key, required this.icon, required this.title, this.message, this.action});

  final IconData icon;
  final String title;
  final String? message;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 56, color: t.colorScheme.primary.withValues(alpha: .6)),
            const SizedBox(height: 12),
            Text(title, style: t.textTheme.titleMedium, textAlign: TextAlign.center),
            if (message != null) ...[
              const SizedBox(height: 6),
              Text(message!, style: t.textTheme.bodyMedium, textAlign: TextAlign.center),
            ],
            if (action != null) ...[const SizedBox(height: 16), action!],
          ],
        ),
      ),
    );
  }
}

/// Text field that owns its controller and reports every change. Used for
/// fields bound directly to a report that autosaves.
class BoundTextField extends StatefulWidget {
  const BoundTextField({
    super.key,
    required this.label,
    required this.value,
    required this.onChanged,
    this.numeric = false,
    this.maxLines = 1,
    this.suffix,
    this.enabled = true,
  });

  final String label;
  final String value;
  final ValueChanged<String> onChanged;
  final bool numeric;
  final int maxLines;
  final String? suffix;
  final bool enabled;

  @override
  State<BoundTextField> createState() => _BoundTextFieldState();
}

class _BoundTextFieldState extends State<BoundTextField> {
  late final _c = TextEditingController(text: widget.value);

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => TextField(
        controller: _c,
        enabled: widget.enabled,
        maxLines: widget.maxLines,
        minLines: 1,
        keyboardType: widget.numeric
            ? const TextInputType.numberWithOptions(decimal: true)
            : (widget.maxLines > 1 ? TextInputType.multiline : TextInputType.text),
        textCapitalization: widget.numeric ? TextCapitalization.none : TextCapitalization.sentences,
        decoration: InputDecoration(
          labelText: widget.label,
          suffixText: widget.suffix,
          border: const OutlineInputBorder(),
          isDense: true,
        ),
        onChanged: widget.onChanged,
      );
}

String numText(double v) => v == 0 ? '' : (v == v.roundToDouble() ? '${v.toInt()}' : '$v');
double parseNum(String s) => double.tryParse(s.trim()) ?? 0;

/// Generic editable list used by every DPR section.
class SectionList<T> extends StatelessWidget {
  const SectionList({
    super.key,
    required this.items,
    required this.title,
    required this.onAdd,
    required this.onEdit,
    required this.onDelete,
    required this.addLabel,
    this.subtitle,
    this.trailing,
    this.leading,
    this.header,
    this.emptyIcon = Icons.playlist_add,
    this.emptyText = 'Nothing added yet',
    this.locked = false,
    this.extraActions = const [],
    this.footer,
  });

  final List<T> items;
  final String Function(T) title;
  final String Function(T)? subtitle;
  final Widget Function(T)? trailing;
  final Widget Function(T)? leading;
  final VoidCallback onAdd;
  final void Function(int) onEdit;
  final void Function(int) onDelete;
  final String addLabel;
  final Widget? header;
  final IconData emptyIcon;
  final String emptyText;
  final bool locked;
  final List<Widget> extraActions;
  final Widget? footer;

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.fromLTRB(12, 12, 12, 96),
      children: [
        ?header,
        if (items.isEmpty)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 32),
            child: EmptyState(icon: emptyIcon, title: emptyText),
          ),
        for (final (i, item) in items.indexed)
          Card(
            margin: const EdgeInsets.only(bottom: 8),
            child: ListTile(
              leading: leading?.call(item),
              title: Text(title(item)),
              subtitle: subtitle == null ? null : Text(subtitle!(item)),
              trailing: trailing?.call(item),
              onTap: locked ? null : () => onEdit(i),
              onLongPress: locked
                  ? null
                  : () async {
                      if (await confirm(context, 'Delete entry?', title(item))) onDelete(i);
                    },
            ),
          ),
        if (!locked) ...[
          const SizedBox(height: 8),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              FilledButton.tonalIcon(onPressed: onAdd, icon: const Icon(Icons.add), label: Text(addLabel)),
              ...extraActions,
            ],
          ),
          if (items.isNotEmpty)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text('Tap to edit, long-press to delete',
                  style: Theme.of(context).textTheme.bodySmall),
            ),
        ],
        ?footer,
      ],
    );
  }
}

class StatTile extends StatelessWidget {
  const StatTile({super.key, required this.label, required this.value, required this.icon, this.color});

  final String label;
  final String value;
  final IconData icon;
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    final c = color ?? t.colorScheme.primary;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(icon, color: c, size: 20),
            const SizedBox(height: 8),
            Text(value, style: t.textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w600)),
            Text(label, style: t.textTheme.bodySmall, maxLines: 2),
          ],
        ),
      ),
    );
  }
}

class StatusChip extends StatelessWidget {
  const StatusChip(this.status, {super.key});

  final ReportStatus status;

  @override
  Widget build(BuildContext context) {
    final (bg, fg) = switch (status) {
      ReportStatus.draft => (Colors.grey.shade300, Colors.grey.shade800),
      ReportStatus.submitted => (Colors.blue.shade100, Colors.blue.shade900),
      ReportStatus.approved => (Colors.green.shade100, Colors.green.shade900),
    };
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(12)),
      child: Text(status.label, style: TextStyle(color: fg, fontSize: 12, fontWeight: FontWeight.w600)),
    );
  }
}

/// Thumbnail for a photo or video.
class MediaThumb extends StatelessWidget {
  const MediaThumb(this.item, {super.key, this.fit = BoxFit.cover});

  final MediaItem item;
  final BoxFit fit;

  @override
  Widget build(BuildContext context) {
    if (item.type == MediaType.video) {
      return Container(
        color: Colors.black87,
        child: const Center(child: Icon(Icons.play_circle_fill, color: Colors.white, size: 40)),
      );
    }
    return localImage(item.localPath, remote: item.remoteUrl, fit: fit);
  }
}

Widget _broken() => Container(color: Colors.grey.shade300, child: const Icon(Icons.broken_image_outlined));

Widget localImage(String path, {String remote = '', BoxFit fit = BoxFit.cover}) {
  Widget onError(BuildContext c, Object e, StackTrace? s) => _broken();
  if (kIsWeb) return Image.network(path, fit: fit, errorBuilder: onError);
  final f = File(path);
  if (path.isNotEmpty && f.existsSync()) return Image.file(f, fit: fit, errorBuilder: onError, cacheWidth: 800);
  if (remote.isNotEmpty) return Image.network(remote, fit: fit, errorBuilder: onError);
  return _broken();
}
