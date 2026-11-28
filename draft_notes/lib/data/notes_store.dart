import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';

import '../models/drawing.dart';
import '../models/note.dart';
import 'samples.dart';
import 'storage.dart';

enum ScopeKind { all, folder, sketches, tag, trash }

/// Which notes the list shows: a folder, a smart folder, a tag or the bin.
@immutable
class Scope {
  const Scope(this.kind, [this.id]);

  static const all = Scope(ScopeKind.all);
  static const sketches = Scope(ScopeKind.sketches);
  static const trash = Scope(ScopeKind.trash);

  final ScopeKind kind;
  final String? id;

  @override
  bool operator ==(Object other) => other is Scope && other.kind == kind && other.id == id;

  @override
  int get hashCode => Object.hash(kind, id);
}

enum SortMode {
  modified('Date edited'),
  created('Date created'),
  title('Title');

  const SortMode(this.label);
  final String label;
}

enum AppTheme { system, light, dark }

/// All notes and folders, saved as one JSON document.
class NotesStore extends ChangeNotifier {
  NotesStore(this.storage);

  static const trashDays = 30;

  final Storage storage;
  final List<Folder> folders = [];
  final List<Note> notes = [];
  SortMode sort = SortMode.modified;
  bool gallery = false;
  AppTheme theme = AppTheme.system;
  Timer? _saveTimer;

  Future<void> load({bool seedSamples = true}) async {
    final raw = await storage.read('notes');
    if (raw == null) {
      if (seedSamples) {
        final s = sampleContent();
        folders.addAll(s.$1);
        notes.addAll(s.$2);
        await save();
      }
    } else {
      try {
        final j = jsonDecode(raw) as Map<String, dynamic>;
        folders.addAll([for (final f in (j['folders'] as List?) ?? const []) Folder.fromJson((f as Map).cast())]);
        notes.addAll([for (final n in (j['notes'] as List?) ?? const []) Note.fromJson((n as Map).cast())]);
        sort = SortMode.values.asNameMap()[j['sort']] ?? SortMode.modified;
        gallery = j['gallery'] == true;
        theme = AppTheme.values.asNameMap()[j['theme']] ?? AppTheme.system;
      } on FormatException {
        // A damaged file is left untouched; start empty rather than crash.
      }
    }
    final cutoff = DateTime.now().subtract(const Duration(days: trashDays));
    notes.removeWhere((n) => n.deletedAt != null && n.deletedAt!.isBefore(cutoff));
    notifyListeners();
  }

  Map<String, dynamic> toJson() => {
        'version': 1,
        'folders': [for (final f in folders) f.toJson()],
        'notes': [for (final n in notes) n.toJson()],
        'sort': sort.name,
        'gallery': gallery,
        'theme': theme.name,
      };

  Future<void> save() async {
    _saveTimer?.cancel();
    _saveTimer = null;
    await storage.write('notes', jsonEncode(toJson()));
  }

  /// Notifies listeners and saves shortly after, batching quick edits.
  void changed() {
    notifyListeners();
    _saveTimer?.cancel();
    _saveTimer = Timer(const Duration(milliseconds: 600), save);
  }

  Future<void> flush() async {
    if (_saveTimer != null) await save();
  }

  @override
  void dispose() {
    _saveTimer?.cancel();
    super.dispose();
  }

  // -- Notes ------------------------------------------------------------------

  Note? byId(String? id) => id == null ? null : notes.where((n) => n.id == id).firstOrNull;

  Note createNote({String? folderId, Drawing? sketch}) {
    final note = Note(
      folderId: folderId,
      blocks: [
        NoteBlock(type: BlockType.title, text: sketch == null ? '' : 'Sketch'),
        if (sketch != null) NoteBlock.sketch(sketch),
        if (sketch != null) NoteBlock(),
      ],
    );
    notes.add(note);
    changed();
    return note;
  }

  void touch(Note n) {
    n.modified = DateTime.now();
    changed();
  }

  void togglePin(Note n) {
    n.pinned = !n.pinned;
    changed();
  }

  void setColor(Note n, int color) {
    n.color = color;
    changed();
  }

  void moveNote(Note n, String? folderId) {
    n.folderId = folderId;
    n.deletedAt = null;
    changed();
  }

  Note duplicate(Note n) {
    final copy = Note.fromJson(n.toJson()..remove('id'));
    copy.blocks = [for (final b in copy.blocks) NoteBlock.fromJson(b.toJson()..remove('id'))];
    copy.pinned = false;
    copy.created = DateTime.now();
    copy.modified = DateTime.now();
    notes.add(copy);
    changed();
    return copy;
  }

  void deleteNote(Note n) {
    if (n.isDeleted) {
      notes.remove(n);
    } else {
      n.deletedAt = DateTime.now();
      n.pinned = false;
    }
    changed();
  }

  void restore(Note n) {
    n.deletedAt = null;
    if (n.folderId != null && folders.every((f) => f.id != n.folderId)) n.folderId = null;
    changed();
  }

  void emptyTrash() {
    notes.removeWhere((n) => n.isDeleted);
    changed();
  }

  /// Removes notes that are still blank, e.g. after the user opened "new note"
  /// and left without typing.
  void discardIfEmpty(Note n) {
    final blank = n.blocks.every((b) => b.type.isText ? b.text.trim().isEmpty : (b.drawing?.isEmpty ?? true));
    if (blank && notes.contains(n)) {
      notes.remove(n);
      changed();
    }
  }

  // -- Folders ----------------------------------------------------------------

  Folder addFolder(String name) {
    final f = Folder(name: name);
    folders.add(f);
    changed();
    return f;
  }

  void renameFolder(Folder f, String name) {
    f.name = name;
    changed();
  }

  /// Deletes the folder and moves its notes to Recently Deleted.
  void deleteFolder(Folder f) {
    folders.remove(f);
    for (final n in notes.where((n) => n.folderId == f.id)) {
      n.deletedAt ??= DateTime.now();
      n.folderId = null;
    }
    changed();
  }

  Folder? folder(String? id) => id == null ? null : folders.where((f) => f.id == id).firstOrNull;

  // -- Queries ----------------------------------------------------------------

  Set<String> get tags => {for (final n in notes.where((n) => !n.isDeleted)) ...n.tags};

  List<Note> notesIn(Scope scope, {String query = ''}) {
    final list = notes.where((n) {
      if (scope.kind == ScopeKind.trash) {
        if (!n.isDeleted) return false;
      } else {
        if (n.isDeleted) return false;
        switch (scope.kind) {
          case ScopeKind.folder:
            if (n.folderId != scope.id) return false;
          case ScopeKind.sketches:
            if (!n.hasSketch) return false;
          case ScopeKind.tag:
            if (!n.tags.contains(scope.id)) return false;
          case ScopeKind.all:
          case ScopeKind.trash:
            break;
        }
      }
      return query.trim().isEmpty || n.matches(query.trim());
    }).toList();
    list.sort(switch (sort) {
      SortMode.modified => (a, b) => b.modified.compareTo(a.modified),
      SortMode.created => (a, b) => b.created.compareTo(a.created),
      SortMode.title => (a, b) => a.title.toLowerCase().compareTo(b.title.toLowerCase()),
    });
    return list;
  }

  int count(Scope scope) => notesIn(scope).length;

  String scopeName(Scope scope) => switch (scope.kind) {
        ScopeKind.all => 'All Notes',
        ScopeKind.folder => folder(scope.id)?.name ?? 'Folder',
        ScopeKind.sketches => 'Sketches & Drawings',
        ScopeKind.tag => '#${scope.id}',
        ScopeKind.trash => 'Recently Deleted',
      };

  void setSort(SortMode s) {
    sort = s;
    changed();
  }

  void setGallery(bool v) {
    gallery = v;
    changed();
  }

  void setTheme(AppTheme t) {
    theme = t;
    changed();
  }
}

/// Groups notes into Apple Notes style date sections.
List<(String, List<Note>)> groupByDate(List<Note> notes, {DateTime? now, bool byCreated = false}) {
  now ??= DateTime.now();
  final today = DateTime(now.year, now.month, now.day);
  final groups = <String, List<Note>>{};
  const months = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
  for (final n in notes) {
    String key;
    if (n.pinned && !n.isDeleted) {
      key = 'Pinned';
    } else {
      final d = byCreated ? n.created : n.modified;
      final day = DateTime(d.year, d.month, d.day);
      final diff = today.difference(day).inDays;
      if (diff <= 0) {
        key = 'Today';
      } else if (diff == 1) {
        key = 'Yesterday';
      } else if (diff <= 7) {
        key = 'Previous 7 Days';
      } else if (diff <= 30) {
        key = 'Previous 30 Days';
      } else if (d.year == now.year) {
        key = months[d.month - 1];
      } else {
        key = '${d.year}';
      }
    }
    groups.putIfAbsent(key, () => []).add(n);
  }
  final pinned = groups.remove('Pinned');
  return [if (pinned != null) ('Pinned', pinned), ...groups.entries.map((e) => (e.key, e.value))];
}
