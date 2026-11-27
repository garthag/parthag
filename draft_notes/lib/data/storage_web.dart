import 'package:web/web.dart' as web;

import 'storage.dart';

Storage createStorage() => LocalStorage();

/// Browser local storage. Falls back to memory when the browser blocks it
/// (private windows, sandboxed frames).
class LocalStorage implements Storage {
  final _memory = MemoryStorage();

  @override
  Future<String?> read(String key) async {
    try {
      return web.window.localStorage.getItem('draft_notes.$key');
    } catch (_) {
      return _memory.read(key);
    }
  }

  @override
  Future<void> write(String key, String value) async {
    try {
      web.window.localStorage.setItem('draft_notes.$key', value);
    } catch (_) {
      await _memory.write(key, value);
    }
  }
}
