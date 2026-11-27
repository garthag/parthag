import 'storage_io.dart' if (dart.library.js_interop) 'storage_web.dart' as impl;

/// Key/value text storage. Files on Android, iOS and Windows; the browser's
/// local storage on the web.
abstract class Storage {
  Future<String?> read(String key);
  Future<void> write(String key, String value);

  static Storage create() => impl.createStorage();
}

class MemoryStorage implements Storage {
  final Map<String, String> data = {};

  @override
  Future<String?> read(String key) async => data[key];

  @override
  Future<void> write(String key, String value) async => data[key] = value;
}
