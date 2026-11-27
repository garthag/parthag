import 'dart:async';

import 'package:material_ui/material_ui.dart';
import 'package:provider/provider.dart';

import '../data/notes_store.dart';
import '../models/drawing.dart';
import '../models/note.dart';
import 'canvas_screen.dart';
import 'common.dart';
import 'note_actions.dart';
import 'note_editor.dart';

/// Adaptive home: three panes on wide screens (folders, list, note), two on
/// tablets, and a list with a folder drawer on phones.
class HomeScreen extends StatefulWidget {
  const HomeScreen({super.key});

  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  Scope _scope = Scope.all;
  String? _selected;
  String _query = '';

  void _setScope(Scope s) {
    setState(() {
      _scope = s;
      _selected = null;
    });
  }

  void _open(Note n, {required bool phone}) {
    if (phone) {
      Navigator.of(context).push(MaterialPageRoute<void>(
        builder: (ctx) => NoteEditor(note: n, showBack: true, onRemoved: () => Navigator.of(ctx).maybePop()),
      ));
    } else {
      setState(() => _selected = n.id);
    }
  }

  void _newNote({Drawing? sketch, required bool phone}) {
    final store = context.read<NotesStore>();
    if (_scope.kind == ScopeKind.trash) _scope = Scope.all;
    final n = store.createNote(folderId: _scope.kind == ScopeKind.folder ? _scope.id : null, sketch: sketch);
    if (_scope.kind == ScopeKind.tag && sketch == null) {
      n.blocks.add(NoteBlock(text: '#${_scope.id}'));
    }
    _open(n, phone: phone);
    if (sketch != null) {
      Navigator.of(context).push(MaterialPageRoute<void>(
        builder: (_) => CanvasScreen(
          drawing: sketch,
          title: n.title,
          onChanged: () => store.touch(n),
          onClose: () => scheduleMicrotask(store.changed),
        ),
      ));
    }
  }

  @override
  Widget build(BuildContext context) {
    final store = context.watch<NotesStore>();
    return LayoutBuilder(builder: (context, cons) {
      final w = cons.maxWidth;
      final phone = w < 700;
      final list = NoteListPane(
        scope: _scope,
        query: _query,
        selectedId: phone ? null : _selected,
        large: phone,
        onQuery: (q) => setState(() => _query = q),
        onOpen: (n) => _open(n, phone: phone),
        onNew: ({sketch}) => _newNote(sketch: sketch, phone: phone),
        showMenu: w < 1100,
      );
      if (phone) {
        return Scaffold(
          drawer: Drawer(child: SafeArea(child: FolderSidebar(scope: _scope, onScope: _closeDrawerThen(_setScope)))),
          body: list,
        );
      }
      final notes = store.notesIn(_scope, query: _query);
      var note = store.byId(_selected);
      if (note == null && notes.isNotEmpty && _query.isEmpty) note = notes.first;
      final editor = note == null
          ? const _EmptyEditor()
          : NoteEditor(key: ValueKey(note.id), note: note, onRemoved: () => setState(() => _selected = null));
      final scheme = Theme.of(context).colorScheme;
      final divider = VerticalDivider(width: 1, color: scheme.outlineVariant);
      return Scaffold(
        drawer: w < 1100 ? Drawer(child: SafeArea(child: FolderSidebar(scope: _scope, onScope: _closeDrawerThen(_setScope)))) : null,
        body: Row(children: [
          if (w >= 1100) ...[
            SizedBox(width: 250, child: FolderSidebar(scope: _scope, onScope: _setScope)),
            divider,
          ],
          SizedBox(width: w >= 1100 ? 350 : 320, child: list),
          divider,
          Expanded(child: editor),
        ]),
      );
    });
  }

  ValueChanged<Scope> _closeDrawerThen(ValueChanged<Scope> f) => (s) {
        Navigator.of(context).maybePop();
        f(s);
      };
}

class _EmptyEditor extends StatelessWidget {
  const _EmptyEditor();

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return ColoredBox(
      color: scheme.surface,
      child: Center(
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          Icon(Icons.sticky_note_2_outlined, size: 56, color: scheme.onSurfaceVariant.withValues(alpha: .5)),
          const SizedBox(height: 8),
          Text('No note selected', style: TextStyle(color: scheme.onSurfaceVariant)),
        ]),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// Sidebar
// ---------------------------------------------------------------------------

class FolderSidebar extends StatelessWidget {
  const FolderSidebar({super.key, required this.scope, required this.onScope});

  final Scope scope;
  final ValueChanged<Scope> onScope;

  @override
  Widget build(BuildContext context) {
    final store = context.watch<NotesStore>();
    final scheme = Theme.of(context).colorScheme;
    final tags = store.tags.toList()..sort();
    Widget section(String title, {Widget? action}) => Padding(
          padding: const EdgeInsets.fromLTRB(16, 18, 8, 4),
          child: Row(children: [
            Expanded(
              child: Text(title,
                  style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w700, color: scheme.onSurfaceVariant, letterSpacing: .5)),
            ),
            ?action,
          ]),
        );
    Widget item(Scope s, IconData icon, String label, {Widget? trailing, VoidCallback? onLongPress}) {
      final selected = s == scope;
      return Padding(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 1),
        child: Material(
          color: selected ? scheme.primary.withValues(alpha: .2) : Colors.transparent,
          borderRadius: BorderRadius.circular(9),
          child: InkWell(
            borderRadius: BorderRadius.circular(9),
            onTap: () => onScope(s),
            onLongPress: onLongPress,
            onSecondaryTap: onLongPress,
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 9),
              child: Row(children: [
                Icon(icon, size: 20, color: scheme.primary),
                const SizedBox(width: 12),
                Expanded(child: Text(label, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 15))),
                trailing ?? Text('${store.count(s)}', style: TextStyle(color: scheme.onSurfaceVariant)),
              ]),
            ),
          ),
        ),
      );
    }

    return ColoredBox(
      color: scheme.surfaceContainerLow,
      child: ListView(
        padding: const EdgeInsets.only(bottom: 16),
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(18, 18, 18, 6),
            child: Row(children: [
              Container(
                width: 30,
                height: 30,
                decoration: BoxDecoration(color: kAccent, borderRadius: BorderRadius.circular(8)),
                child: const Icon(Icons.architecture, size: 20, color: Colors.white),
              ),
              const SizedBox(width: 10),
              const Flexible(
                child: Text('Draft Notes', overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 20, fontWeight: FontWeight.w800)),
              ),
            ]),
          ),
          section('LIBRARY'),
          item(Scope.all, Icons.notes, 'All Notes'),
          item(Scope.sketches, Icons.architecture, 'Sketches & Drawings'),
          section(
            'FOLDERS',
            action: IconButton(
              tooltip: 'New folder',
              visualDensity: VisualDensity.compact,
              icon: const Icon(Icons.create_new_folder_outlined, size: 20),
              onPressed: () async {
                final name = await promptText(context, title: 'New Folder', hint: 'Name', confirm: 'Save');
                if (name != null && name.isNotEmpty) {
                  final f = store.addFolder(name);
                  onScope(Scope(ScopeKind.folder, f.id));
                }
              },
            ),
          ),
          for (final f in store.folders)
            item(
              Scope(ScopeKind.folder, f.id),
              Icons.folder_outlined,
              f.name,
              onLongPress: () => _folderMenu(context, store, f),
            ),
          if (store.folders.isEmpty)
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 4),
              child: Text('No folders yet', style: TextStyle(color: scheme.onSurfaceVariant)),
            ),
          if (tags.isNotEmpty) ...[
            section('TAGS'),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12),
              child: Wrap(
                spacing: 6,
                runSpacing: 6,
                children: [
                  for (final t in tags)
                    ChoiceChip(
                      label: Text('#$t'),
                      selected: scope == Scope(ScopeKind.tag, t),
                      onSelected: (_) => onScope(Scope(ScopeKind.tag, t)),
                    ),
                ],
              ),
            ),
          ],
          const SizedBox(height: 12),
          item(Scope.trash, Icons.delete_outline, 'Recently Deleted'),
          section('APPEARANCE'),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 12),
            child: SegmentedButton<AppTheme>(
              showSelectedIcon: false,
              segments: const [
                ButtonSegment(value: AppTheme.system, icon: Icon(Icons.brightness_auto, size: 18), tooltip: 'System'),
                ButtonSegment(value: AppTheme.light, icon: Icon(Icons.light_mode, size: 18), tooltip: 'Light'),
                ButtonSegment(value: AppTheme.dark, icon: Icon(Icons.dark_mode, size: 18), tooltip: 'Dark'),
              ],
              selected: {store.theme},
              onSelectionChanged: (s) => store.setTheme(s.first),
            ),
          ),
        ],
      ),
    );
  }

  Future<void> _folderMenu(BuildContext context, NotesStore store, Folder f) async {
    final v = await showModalBottomSheet<String>(
      context: context,
      showDragHandle: true,
      builder: (ctx) => SafeArea(
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          ListTile(leading: const Icon(Icons.edit_outlined), title: const Text('Rename folder'), onTap: () => Navigator.pop(ctx, 'rename')),
          ListTile(leading: const Icon(Icons.delete_outline), title: const Text('Delete folder'), onTap: () => Navigator.pop(ctx, 'delete')),
        ]),
      ),
    );
    if (!context.mounted) return;
    if (v == 'rename') {
      final name = await promptText(context, title: 'Rename Folder', initial: f.name, confirm: 'Save');
      if (name != null && name.isNotEmpty) store.renameFolder(f, name);
    } else if (v == 'delete') {
      final ok = await confirmDialog(context, 'Delete "${f.name}"?', 'Its notes move to Recently Deleted.');
      if (ok) {
        store.deleteFolder(f);
        if (scope == Scope(ScopeKind.folder, f.id)) onScope(Scope.all);
      }
    }
  }
}

// ---------------------------------------------------------------------------
// Note list
// ---------------------------------------------------------------------------

typedef NewNote = void Function({Drawing? sketch});

class NoteListPane extends StatefulWidget {
  const NoteListPane({
    super.key,
    required this.scope,
    required this.query,
    required this.selectedId,
    required this.onQuery,
    required this.onOpen,
    required this.onNew,
    this.large = false,
    this.showMenu = true,
  });

  final Scope scope;
  final String query;
  final String? selectedId;
  final ValueChanged<String> onQuery;
  final ValueChanged<Note> onOpen;
  final NewNote onNew;
  final bool large;
  final bool showMenu;

  @override
  State<NoteListPane> createState() => _NoteListPaneState();
}

class _NoteListPaneState extends State<NoteListPane> {
  late final _search = TextEditingController(text: widget.query);

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  Future<void> _menu(Note n, Offset at) async {
    final overlay = Overlay.of(context).context.findRenderObject() as RenderBox;
    final v = await showMenu<String>(
      context: context,
      position: RelativeRect.fromRect(at & const Size(1, 1), Offset.zero & overlay.size),
      items: noteMenuEntries(n),
    );
    if (v != null && mounted) await runNoteAction(context, n, v);
  }

  @override
  Widget build(BuildContext context) {
    final store = context.watch<NotesStore>();
    final scheme = Theme.of(context).colorScheme;
    final notes = store.notesIn(widget.scope, query: widget.query);
    final trash = widget.scope.kind == ScopeKind.trash;
    final groups = store.sort == SortMode.title
        ? [('', notes)]
        : groupByDate(notes, byCreated: store.sort == SortMode.created);
    final title = store.scopeName(widget.scope);

    final actions = <Widget>[
      if (trash && notes.isNotEmpty)
        TextButton(
          onPressed: () async {
            final ok = await confirmDialog(context, 'Delete all?', 'All notes in Recently Deleted will be deleted permanently.');
            if (ok) store.emptyTrash();
          },
          child: const Text('Delete All'),
        ),
      IconButton(
        tooltip: store.gallery ? 'View as list' : 'View as gallery',
        icon: Icon(store.gallery ? Icons.view_agenda_outlined : Icons.grid_view_outlined),
        onPressed: () => store.setGallery(!store.gallery),
      ),
      PopupMenuButton<SortMode>(
        tooltip: 'Sort by',
        icon: const Icon(Icons.sort),
        onSelected: store.setSort,
        itemBuilder: (_) => [
          for (final s in SortMode.values) CheckedPopupMenuItem(value: s, checked: store.sort == s, child: Text(s.label)),
        ],
      ),
    ];

    final searchField = Padding(
      padding: const EdgeInsets.fromLTRB(16, 4, 16, 8),
      child: TextField(
        controller: _search,
        onChanged: widget.onQuery,
        textInputAction: TextInputAction.search,
        decoration: InputDecoration(
          hintText: 'Search',
          prefixIcon: const Icon(Icons.search, size: 20),
          suffixIcon: widget.query.isEmpty
              ? null
              : IconButton(
                  icon: const Icon(Icons.close, size: 18),
                  onPressed: () {
                    _search.clear();
                    widget.onQuery('');
                  },
                ),
          filled: true,
          fillColor: scheme.onSurface.withValues(alpha: .07),
          border: OutlineInputBorder(borderRadius: BorderRadius.circular(10), borderSide: BorderSide.none),
          contentPadding: const EdgeInsets.symmetric(vertical: 9),
        ),
      ),
    );

    final slivers = <Widget>[
      if (widget.large)
        SliverAppBar.large(
          title: Text(title, style: const TextStyle(fontWeight: FontWeight.w800)),
          leading: widget.showMenu ? _menuButton() : null,
          actions: actions,
        )
      else
        SliverAppBar(
          pinned: true,
          toolbarHeight: 52,
          leading: widget.showMenu ? _menuButton() : null,
          automaticallyImplyLeading: false,
          titleSpacing: widget.showMenu ? 0 : 16,
          title: Text(title, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 20)),
          actions: actions,
        ),
      SliverToBoxAdapter(child: searchField),
      if (notes.isEmpty)
        SliverFillRemaining(
          hasScrollBody: false,
          child: Center(
            child: Column(mainAxisSize: MainAxisSize.min, children: [
              Text(widget.query.isEmpty ? 'No Notes' : 'No Results',
                  style: TextStyle(fontSize: 20, fontWeight: FontWeight.w700, color: scheme.onSurfaceVariant)),
              const SizedBox(height: 4),
              Text(
                trash
                    ? 'Deleted notes stay here for ${NotesStore.trashDays} days.'
                    : (widget.query.isEmpty ? 'Tap the compose button to start.' : 'Try a different search.'),
                style: TextStyle(color: scheme.onSurfaceVariant),
              ),
            ]),
          ),
        ),
      for (final (label, items) in groups) ...[
        if (label.isNotEmpty)
          SliverToBoxAdapter(
            child: Padding(
              padding: const EdgeInsets.fromLTRB(20, 14, 20, 6),
              child: Row(children: [
                if (label == 'Pinned') Icon(Icons.push_pin, size: 16, color: scheme.primary),
                if (label == 'Pinned') const SizedBox(width: 4),
                Text(label, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800)),
              ]),
            ),
          ),
        if (store.gallery) _gallery(items, store) else _list(items, store),
      ],
      const SliverToBoxAdapter(child: SizedBox(height: 24)),
    ];

    return Scaffold(
      backgroundColor: scheme.surfaceContainerLowest,
      body: CustomScrollView(slivers: slivers),
      bottomNavigationBar: _bottomBar(context, notes.length, trash),
    );
  }

  Widget _menuButton() => Builder(
        builder: (ctx) => IconButton(
          tooltip: 'Folders',
          icon: const Icon(Icons.menu),
          onPressed: () => _openDrawer(ctx),
        ),
      );

  /// The list has its own Scaffold, so open the drawer of the outer one.
  void _openDrawer(BuildContext ctx) {
    var s = Scaffold.maybeOf(ctx);
    while (s != null && !s.hasDrawer) {
      s = s.context.findAncestorStateOfType<ScaffoldState>();
    }
    s?.openDrawer();
  }

  Widget _bottomBar(BuildContext context, int count, bool trash) {
    final scheme = Theme.of(context).colorScheme;
    return Container(
      decoration: BoxDecoration(
        color: scheme.surfaceContainerLowest,
        border: Border(top: BorderSide(color: scheme.outlineVariant)),
      ),
      child: SafeArea(
        top: false,
        child: SizedBox(
          height: 50,
          child: Row(children: [
            const SizedBox(width: 4),
            if (!trash)
              PopupMenuButton<String>(
                tooltip: 'New sketch',
                icon: const Icon(Icons.draw_outlined),
                onSelected: (v) => widget.onNew(sketch: v == 'cad' ? Drawing.cad() : Drawing.ink()),
                itemBuilder: (_) => const [
                  PopupMenuItem(
                    value: 'ink',
                    child: ListTile(
                      leading: Icon(Icons.draw_outlined),
                      title: Text('Handwriting note'),
                      subtitle: Text('Pen, pencil, highlighter on paper'),
                    ),
                  ),
                  PopupMenuItem(
                    value: 'cad',
                    child: ListTile(
                      leading: Icon(Icons.architecture),
                      title: Text('CAD drawing'),
                      subtitle: Text('Lines, snaps, dimensions, layers'),
                    ),
                  ),
                ],
              )
            else
              const SizedBox(width: 48),
            Expanded(
              child: Text(
                count == 1 ? '1 Note' : '$count Notes',
                textAlign: TextAlign.center,
                style: TextStyle(fontSize: 12.5, color: scheme.onSurfaceVariant),
              ),
            ),
            if (!trash)
              IconButton(tooltip: 'New note', icon: const Icon(Icons.edit_square), onPressed: () => widget.onNew())
            else
              const SizedBox(width: 48),
            const SizedBox(width: 4),
          ]),
        ),
      ),
    );
  }

  Widget _list(List<Note> items, NotesStore store) {
    final scheme = Theme.of(context).colorScheme;
    final showFolder = widget.scope.kind != ScopeKind.folder;
    return SliverPadding(
      padding: const EdgeInsets.symmetric(horizontal: 12),
      sliver: SliverToBoxAdapter(
        child: Material(
          color: scheme.surface,
          borderRadius: BorderRadius.circular(12),
          clipBehavior: Clip.antiAlias,
          child: Column(children: [
            for (var i = 0; i < items.length; i++) ...[
              if (i > 0) Divider(indent: 16, height: 1, color: scheme.outlineVariant),
              _row(items[i], store, showFolder),
            ],
          ]),
        ),
      ),
    );
  }

  Widget _row(Note n, NotesStore store, bool showFolder) {
    final scheme = Theme.of(context).colorScheme;
    final selected = n.id == widget.selectedId;
    final folder = store.folder(n.folderId);
    final (done, total) = n.checklistProgress;
    final drawing = n.firstDrawing;
    Widget row = Material(
      color: selected ? scheme.primary.withValues(alpha: .28) : Colors.transparent,
      child: InkWell(
        onTap: () => widget.onOpen(n),
        onLongPress: () {
          final box = context.findRenderObject() as RenderBox?;
          _menu(n, box?.localToGlobal(const Offset(120, 120)) ?? Offset.zero);
        },
        onSecondaryTapUp: (d) => _menu(n, d.globalPosition),
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 11, 12, 11),
          child: Row(children: [
            if (n.color != 0)
              Container(
                width: 4,
                height: 40,
                margin: const EdgeInsets.only(right: 10),
                decoration: BoxDecoration(color: Color(n.color), borderRadius: BorderRadius.circular(2)),
              ),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(n.title, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w700)),
                  const SizedBox(height: 2),
                  Row(children: [
                    Text(noteTimeLabel(n.modified), style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w500)),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        n.snippet,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: TextStyle(fontSize: 14, color: scheme.onSurfaceVariant),
                      ),
                    ),
                  ]),
                  if ((showFolder && folder != null) || total > 0)
                    Padding(
                      padding: const EdgeInsets.only(top: 3),
                      child: Row(children: [
                        if (showFolder && folder != null) ...[
                          Icon(Icons.folder_outlined, size: 14, color: scheme.onSurfaceVariant),
                          const SizedBox(width: 3),
                          Text(folder.name, style: TextStyle(fontSize: 12.5, color: scheme.onSurfaceVariant)),
                          const SizedBox(width: 10),
                        ],
                        if (total > 0) ...[
                          Icon(Icons.checklist, size: 14, color: scheme.onSurfaceVariant),
                          const SizedBox(width: 3),
                          Text('$done/$total', style: TextStyle(fontSize: 12.5, color: scheme.onSurfaceVariant)),
                        ],
                      ]),
                    ),
                ],
              ),
            ),
            if (drawing != null && !drawing.isEmpty)
              Padding(
                padding: const EdgeInsets.only(left: 10),
                child: SizedBox(width: 54, height: 54, child: DrawingPreview(drawing: drawing, radius: 8, paperLines: false)),
              ),
          ]),
        ),
      ),
    );
    if (widget.large && !n.isDeleted) {
      row = Dismissible(
        key: ValueKey('dismiss-${n.id}'),
        background: Container(
          color: const Color(0xFFF5A524),
          alignment: Alignment.centerLeft,
          padding: const EdgeInsets.only(left: 24),
          child: Icon(n.pinned ? Icons.push_pin_outlined : Icons.push_pin, color: Colors.white),
        ),
        secondaryBackground: Container(
          color: const Color(0xFFE5484D),
          alignment: Alignment.centerRight,
          padding: const EdgeInsets.only(right: 24),
          child: const Icon(Icons.delete_outline, color: Colors.white),
        ),
        confirmDismiss: (dir) async {
          if (dir == DismissDirection.startToEnd) {
            store.togglePin(n);
            return false;
          }
          return true;
        },
        onDismissed: (_) => runNoteAction(context, n, 'delete'),
        child: row,
      );
    }
    return row;
  }

  Widget _gallery(List<Note> items, NotesStore store) {
    final scheme = Theme.of(context).colorScheme;
    return SliverPadding(
      padding: const EdgeInsets.symmetric(horizontal: 12),
      sliver: SliverGrid(
        gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(
          maxCrossAxisExtent: 170,
          mainAxisSpacing: 12,
          crossAxisSpacing: 12,
          childAspectRatio: .72,
        ),
        delegate: SliverChildBuilderDelegate(
          childCount: items.length,
          (context, i) {
            final n = items[i];
            final selected = n.id == widget.selectedId;
            final d = n.firstDrawing;
            return GestureDetector(
              onTap: () => widget.onOpen(n),
              onLongPressStart: (e) => _menu(n, e.globalPosition),
              onSecondaryTapUp: (e) => _menu(n, e.globalPosition),
              child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                Expanded(
                  child: Container(
                    decoration: BoxDecoration(
                      color: n.color != 0 ? Color(n.color) : scheme.surface,
                      borderRadius: BorderRadius.circular(12),
                      border: Border.all(color: selected ? scheme.primary : scheme.outlineVariant, width: selected ? 2.5 : 1),
                    ),
                    clipBehavior: Clip.antiAlias,
                    child: d != null && !d.isEmpty
                        ? DrawingPreview(drawing: d, radius: 0, paperLines: false)
                        : Padding(
                            padding: const EdgeInsets.all(10),
                            child: Text(
                              n.plainText,
                              overflow: TextOverflow.fade,
                              style: TextStyle(
                                fontSize: 9.5,
                                height: 1.35,
                                color: n.color != 0 ? const Color(0xFF333333) : scheme.onSurface,
                              ),
                            ),
                          ),
                  ),
                ),
                const SizedBox(height: 6),
                Text(n.title, maxLines: 1, overflow: TextOverflow.ellipsis, textAlign: TextAlign.center, style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 13.5)),
                Text(noteTimeLabel(n.modified), textAlign: TextAlign.center, style: TextStyle(fontSize: 12, color: scheme.onSurfaceVariant)),
              ]),
            );
          },
        ),
      ),
    );
  }
}
