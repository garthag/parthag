import 'package:material_ui/material_ui.dart';
import 'package:provider/provider.dart';

import '../data/notes_store.dart';
import '../models/note.dart';
import 'common.dart';

/// Menu entries shared by the editor's "more" button and the list's
/// long-press / right-click menu.
List<PopupMenuEntry<String>> noteMenuEntries(Note note) {
  if (note.isDeleted) {
    return const [
      PopupMenuItem(value: 'recover', child: ListTile(leading: Icon(Icons.restore_from_trash), title: Text('Recover'))),
      PopupMenuItem(value: 'delete', child: ListTile(leading: Icon(Icons.delete_forever), title: Text('Delete permanently'))),
    ];
  }
  return [
    PopupMenuItem(
      value: 'pin',
      child: ListTile(
        leading: Icon(note.pinned ? Icons.push_pin : Icons.push_pin_outlined),
        title: Text(note.pinned ? 'Unpin note' : 'Pin note'),
      ),
    ),
    const PopupMenuItem(value: 'color', child: ListTile(leading: Icon(Icons.palette_outlined), title: Text('Note colour'))),
    const PopupMenuItem(value: 'move', child: ListTile(leading: Icon(Icons.drive_file_move_outline), title: Text('Move to folder'))),
    const PopupMenuItem(value: 'duplicate', child: ListTile(leading: Icon(Icons.copy_all), title: Text('Duplicate'))),
    const PopupMenuItem(value: 'share', child: ListTile(leading: Icon(Icons.ios_share), title: Text('Share as text'))),
    const PopupMenuDivider(),
    const PopupMenuItem(value: 'delete', child: ListTile(leading: Icon(Icons.delete_outline), title: Text('Delete'))),
  ];
}

Future<void> runNoteAction(BuildContext context, Note note, String action, {VoidCallback? onRemoved}) async {
  final store = context.read<NotesStore>();
  switch (action) {
    case 'pin':
      store.togglePin(note);
    case 'color':
      final c = await showDialog<int>(
        context: context,
        builder: (ctx) => SimpleDialog(
          title: const Text('Note colour'),
          children: [
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
              child: Wrap(children: [
                for (final col in noteColors)
                  ColorDot(
                    color: col == 0 ? 0xFFFFFFFF : col,
                    size: 36,
                    selected: note.color == col,
                    onTap: () => Navigator.pop(ctx, col),
                  ),
              ]),
            ),
          ],
        ),
      );
      if (c != null) store.setColor(note, c);
    case 'move':
      if (!context.mounted) return;
      final target = await showDialog<String>(
        context: context,
        builder: (ctx) => SimpleDialog(
          title: const Text('Move to folder'),
          children: [
            SimpleDialogOption(
              onPressed: () => Navigator.pop(ctx, ''),
              child: ListTile(leading: const Icon(Icons.notes), title: const Text('Notes (no folder)'), selected: note.folderId == null),
            ),
            for (final f in store.folders)
              SimpleDialogOption(
                onPressed: () => Navigator.pop(ctx, f.id),
                child: ListTile(leading: const Icon(Icons.folder_outlined), title: Text(f.name), selected: note.folderId == f.id),
              ),
          ],
        ),
      );
      if (target != null) store.moveNote(note, target.isEmpty ? null : target);
    case 'duplicate':
      store.duplicate(note);
      if (context.mounted) toast(context, 'Duplicated "${note.title}"');
    case 'share':
      await shareText(context, note.toMarkdown(), subject: note.title);
    case 'recover':
      store.restore(note);
    case 'delete':
      if (note.isDeleted) {
        final ok = await confirmDialog(context, 'Delete permanently?', '"${note.title}" will be deleted. This can\'t be undone.');
        if (!ok) return;
      }
      final wasDeleted = note.isDeleted;
      store.deleteNote(note);
      onRemoved?.call();
      if (!wasDeleted && context.mounted) {
        ScaffoldMessenger.maybeOf(context)
          ?..hideCurrentSnackBar()
          ..showSnackBar(SnackBar(
            behavior: SnackBarBehavior.floating,
            content: const Text('Moved to Recently Deleted'),
            action: SnackBarAction(label: 'Undo', onPressed: () => store.restore(note)),
          ));
      }
  }
}
