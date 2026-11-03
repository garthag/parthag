import 'package:flutter/material.dart';
import 'package:intl/date_symbol_data_local.dart';
import 'package:provider/provider.dart';

import 'data/local_store.dart';
import 'screens/home_screen.dart';
import 'services/cloud_sync.dart';
import 'services/media_service.dart';
import 'state/app_state.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  await initializeDateFormatting('en_IN');
  final store = LocalStore.create();
  final state = AppState(store);
  final cloud = CloudSync();
  await Future.wait([state.load(), cloud.init()]);
  runApp(SiteDprApp(state: state, cloud: cloud, media: MediaService(store)));
}

class SiteDprApp extends StatelessWidget {
  const SiteDprApp({super.key, required this.state, required this.cloud, required this.media});

  final AppState state;
  final CloudSync cloud;
  final MediaService media;

  @override
  Widget build(BuildContext context) {
    return MultiProvider(
      providers: [
        ChangeNotifierProvider.value(value: state),
        ChangeNotifierProvider.value(value: cloud),
        Provider.value(value: media),
      ],
      child: MaterialApp(
        title: 'Site DPR',
        debugShowCheckedModeBanner: false,
        theme: buildTheme(Brightness.light),
        darkTheme: buildTheme(Brightness.dark),
        home: const HomeScreen(),
      ),
    );
  }
}

ThemeData buildTheme(Brightness b) {
  final scheme = ColorScheme.fromSeed(seedColor: const Color(0xFFE65100), brightness: b);
  return ThemeData(
    colorScheme: scheme,
    useMaterial3: true,
    appBarTheme: AppBarTheme(
      backgroundColor: scheme.primaryContainer,
      foregroundColor: scheme.onPrimaryContainer,
    ),
    cardTheme: const CardThemeData(margin: EdgeInsets.zero),
    inputDecorationTheme: const InputDecorationTheme(border: OutlineInputBorder(), isDense: true),
  );
}
