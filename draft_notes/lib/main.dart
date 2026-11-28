import 'package:material_ui/material_ui.dart';
import 'package:provider/provider.dart';

import 'data/notes_store.dart';
import 'data/storage.dart';
import 'ui/common.dart';
import 'ui/home.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final store = NotesStore(Storage.create());
  await store.load();
  runApp(DraftNotesApp(store: store));
}

class DraftNotesApp extends StatefulWidget {
  const DraftNotesApp({super.key, required this.store});

  final NotesStore store;

  @override
  State<DraftNotesApp> createState() => _DraftNotesAppState();
}

class _DraftNotesAppState extends State<DraftNotesApp> {
  late final AppLifecycleListener _lifecycle;

  @override
  void initState() {
    super.initState();
    // Save pending edits when the app goes to the background or closes.
    _lifecycle = AppLifecycleListener(
      onPause: widget.store.flush,
      onInactive: widget.store.flush,
      onDetach: widget.store.flush,
    );
  }

  @override
  void dispose() {
    _lifecycle.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return ChangeNotifierProvider.value(
      value: widget.store,
      child: Selector<NotesStore, AppTheme>(
        selector: (_, s) => s.theme,
        builder: (context, theme, _) => MaterialApp(
          title: 'Draft Notes',
          debugShowCheckedModeBanner: false,
          themeMode: switch (theme) {
            AppTheme.system => ThemeMode.system,
            AppTheme.light => ThemeMode.light,
            AppTheme.dark => ThemeMode.dark,
          },
          theme: buildTheme(Brightness.light),
          darkTheme: buildTheme(Brightness.dark),
          home: const HomeScreen(),
        ),
      ),
    );
  }
}
