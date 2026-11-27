import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:path_provider/path_provider.dart';

import 'storage.dart';

Storage createStorage() => FileStorage();

/// One JSON file per key in the app documents folder.
class FileStorage implements Storage {
  Directory? _dir;

  Future<Directory> _root() async {
    if (_dir != null) return _dir!;
    final docs = await getApplicationDocumentsDirectory();
    _dir = Directory(p.join(docs.path, 'draft_notes'));
    await _dir!.create(recursive: true);
    return _dir!;
  }

  @override
  Future<String?> read(String key) async {
    final f = File(p.join((await _root()).path, '$key.json'));
    return await f.exists() ? f.readAsString() : null;
  }

  @override
  Future<void> write(String key, String value) async {
    final root = await _root();
    // Write a temp file and rename it, so a crash never leaves half a file.
    final tmp = File(p.join(root.path, '$key.json.tmp'));
    await tmp.writeAsString(value, flush: true);
    await tmp.rename(p.join(root.path, '$key.json'));
  }
}
