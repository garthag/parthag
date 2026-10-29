import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:path/path.dart' as p;
import 'package:path_provider/path_provider.dart';

/// Minimal JSON document store. Each collection is one JSON file in the app
/// documents directory, which keeps the app fully usable offline on site.
abstract class LocalStore {
  Future<dynamic> read(String name);
  Future<void> write(String name, Object? data);

  /// Directory for photos, videos and signatures. Null when files are
  /// unavailable (web preview / tests).
  Future<Directory?> mediaRoot();

  static LocalStore create() => kIsWeb ? MemoryStore() : FileStore();
}

class FileStore implements LocalStore {
  Directory? _dir;

  Future<Directory> _root() async {
    if (_dir != null) return _dir!;
    final docs = await getApplicationDocumentsDirectory();
    _dir = Directory(p.join(docs.path, 'site_dpr'));
    await _dir!.create(recursive: true);
    return _dir!;
  }

  @override
  Future<dynamic> read(String name) async {
    final f = File(p.join((await _root()).path, '$name.json'));
    if (!await f.exists()) return null;
    try {
      return jsonDecode(await f.readAsString());
    } on FormatException {
      return null;
    }
  }

  @override
  Future<void> write(String name, Object? data) async {
    final root = await _root();
    // Write to a temp file then rename so a crash never leaves a half file.
    final tmp = File(p.join(root.path, '$name.json.tmp'));
    await tmp.writeAsString(jsonEncode(data), flush: true);
    await tmp.rename(p.join(root.path, '$name.json'));
  }

  @override
  Future<Directory?> mediaRoot() async {
    final d = Directory(p.join((await _root()).path, 'media'));
    await d.create(recursive: true);
    return d;
  }
}

class MemoryStore implements LocalStore {
  final Map<String, String> _data = {};

  @override
  Future<dynamic> read(String name) async =>
      _data[name] == null ? null : jsonDecode(_data[name]!);

  @override
  Future<void> write(String name, Object? data) async => _data[name] = jsonEncode(data);

  @override
  Future<Directory?> mediaRoot() async => null;
}
