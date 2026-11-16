import 'dart:io';

import 'package:archive/archive.dart';
import 'package:file_picker/file_picker.dart';
import 'package:flutter/foundation.dart';
import 'package:path/path.dart' as p;

import '../data/local_store.dart';
import '../models/drawing.dart';
import 'drawing_parser.dart';

/// Result of one import run, shown to the user afterwards.
class DrawingImportResult {
  final List<Drawing> added = [];
  final List<String> duplicates = [];
  int junkSkipped = 0;
  final List<String> errors = [];

  int get unclassified => added.where((d) => d.typeCode == DrawingType.other.code).length;
  Set<String> get plants => added.map((d) => d.plant).toSet();
  Set<String> get units => added.map((d) => '${d.plant}|${d.unit}').toSet();
  Set<String> get types => added.map((d) => d.typeCode).toSet();
}

/// A file waiting to be imported: either a ZIP entry or a picked file.
class _Source {
  _Source(this.path, this.size, this.save, this.bytes);

  /// Path relative to the ZIP root (folders included).
  final String path;
  final int size;

  /// Writes the file to [target] on disk.
  final Future<void> Function(String target) save;
  final Future<Uint8List> Function() bytes;
}

/// Unzips drawing packages into app storage and reads drawings back for the
/// viewer.
class DrawingService {
  DrawingService(this._store);

  final LocalStore _store;

  /// Files kept in memory when there is no file system (web preview, tests).
  static final Map<String, Uint8List> memoryFiles = {};

  static const pickExtensions = [
    'zip', 'pdf', 'dwg', 'dxf', 'dgn', 'dwf', 'png', 'jpg', 'jpeg', 'tif', 'tiff', 'webp',
  ];

  /// Lets the user choose one or more ZIP packages and/or drawing files.
  Future<List<PlatformFile>> pick() => FilePicker.pickFiles(
        dialogTitle: 'Choose drawing ZIP or files',
        type: FileType.custom,
        allowedExtensions: pickExtensions,
      );

  Future<Directory?> _dir(String projectId) async {
    final root = await _store.mediaRoot();
    if (root == null) return null;
    final d = Directory(p.join(root.path, 'drawings', projectId));
    await d.create(recursive: true);
    return d;
  }

  /// Imports picked files. ZIPs (including ZIPs inside ZIPs) are unpacked.
  ///
  /// [existing] is the project's current register; a file whose drawing
  /// number, plant, unit and revision are already there is skipped.
  Future<DrawingImportResult> importPicked(
    List<PlatformFile> files, {
    required String projectId,
    required String defaultPlant,
    required Iterable<Drawing> existing,
    void Function(int done, int total, String name)? onProgress,
  }) async {
    final sources = <_Source>[];
    final result = DrawingImportResult();
    for (final f in files) {
      try {
        if (f.name.toLowerCase().endsWith('.zip')) {
          final InputStream input = !kIsWeb && f.path != null
              ? InputFileStream(f.path!)
              : InputMemoryStream(await f.readAsBytes());
          sources.addAll(_zipSources(ZipDecoder().decodeStream(input), '', 0, result));
        } else {
          sources.add(_Source(
            f.name,
            f.lengthSync() ?? 0,
            (target) => f.xFile.saveTo(target),
            () => f.readAsBytes(),
          ));
        }
      } catch (e) {
        result.errors.add('${f.name}: could not open ($e)');
      }
    }
    await _importSources(sources, projectId, defaultPlant, existing, result, onProgress);
    return result;
  }

  /// Imports a ZIP given as bytes. Used by tests and the web preview.
  Future<DrawingImportResult> importZipBytes(
    Uint8List zip, {
    required String projectId,
    required String defaultPlant,
    Iterable<Drawing> existing = const [],
  }) async {
    final result = DrawingImportResult();
    final sources = _zipSources(ZipDecoder().decodeBytes(zip), '', 0, result);
    await _importSources(sources, projectId, defaultPlant, existing, result, null);
    return result;
  }

  List<_Source> _zipSources(Archive archive, String prefix, int depth, DrawingImportResult result) {
    final out = <_Source>[];
    final files = archive.files.where((f) => f.isFile).toList();
    for (final f in files) {
      final name = f.name.replaceAll('\\', '/');
      if (!isDrawingFile(name)) {
        result.junkSkipped++;
        continue;
      }
      final path = prefix.isEmpty ? name : '$prefix/$name';
      if (name.toLowerCase().endsWith('.zip') && depth < 2) {
        try {
          final inner = ZipDecoder().decodeBytes(f.content);
          final folder = path.substring(0, path.length - 4);
          out.addAll(_zipSources(inner, folder, depth + 1, result));
        } catch (e) {
          result.errors.add('$path: could not unzip ($e)');
        }
        continue;
      }
      out.add(_Source(
        path,
        f.size,
        (target) async {
          final o = OutputFileStream(target);
          f.writeContent(o);
          await o.close();
        },
        () async => f.content,
      ));
    }
    return out;
  }

  Future<void> _importSources(
    List<_Source> sources,
    String projectId,
    String defaultPlant,
    Iterable<Drawing> existing,
    DrawingImportResult result,
    void Function(int done, int total, String name)? onProgress,
  ) async {
    // A single wrapper folder (usually the ZIP's own name) carries no meaning.
    final root = commonRootFolder(sources.map((s) => s.path));
    final seen = {for (final d in existing) '${d.registerKey}|${d.revision.toUpperCase()}|${p.extension(d.fileName).toLowerCase()}'};
    final dir = await _dir(projectId);

    for (var i = 0; i < sources.length; i++) {
      final s = sources[i];
      onProgress?.call(i, sources.length, s.path);
      // Keep the progress dialog responsive on large packages.
      if (i % 4 == 0) await Future<void>.delayed(Duration.zero);

      final rel = root.isEmpty ? s.path : s.path.substring(root.length + 1);
      final parsed = parseDrawingPath(rel, defaultPlant: defaultPlant);
      final d = Drawing(
        projectId: projectId,
        number: parsed.number,
        title: parsed.title,
        revision: parsed.revision,
        plant: parsed.plant,
        unit: parsed.unit,
        typeCode: parsed.type.code,
        kind: parsed.kind,
        status: parsed.status,
        fileName: parsed.fileName,
        sourcePath: parsed.folder,
        sizeBytes: s.size,
      );
      final key = '${d.registerKey}|${d.revision.toUpperCase()}|${p.extension(d.fileName).toLowerCase()}';
      if (!seen.add(key)) {
        result.duplicates.add(s.path);
        continue;
      }
      try {
        final ext = p.extension(parsed.fileName).toLowerCase();
        if (dir == null) {
          d.localPath = 'mem://${d.id}$ext';
          memoryFiles[d.localPath] = await s.bytes();
          d.sizeBytes = memoryFiles[d.localPath]!.length;
        } else {
          d.localPath = p.join(dir.path, '${d.id}$ext');
          await s.save(d.localPath);
          d.sizeBytes = await File(d.localPath).length();
        }
        result.added.add(d);
      } catch (e) {
        result.errors.add('${s.path}: $e');
      }
    }
    onProgress?.call(sources.length, sources.length, '');
  }

  Future<Uint8List?> readBytes(Drawing d) async {
    if (d.localPath.startsWith('mem://')) return memoryFiles[d.localPath];
    if (kIsWeb || d.localPath.isEmpty) return null;
    final f = File(d.localPath);
    return await f.exists() ? f.readAsBytes() : null;
  }

  Future<void> deleteFile(Drawing d) async {
    if (d.localPath.startsWith('mem://')) {
      memoryFiles.remove(d.localPath);
      return;
    }
    if (kIsWeb || d.localPath.isEmpty) return;
    try {
      final f = File(d.localPath);
      if (await f.exists()) await f.delete();
    } catch (_) {}
  }
}

String fileSizeLabel(int bytes) {
  if (bytes <= 0) return '';
  if (bytes < 1024) return '$bytes B';
  if (bytes < 1024 * 1024) return '${(bytes / 1024).toStringAsFixed(0)} KB';
  return '${(bytes / 1024 / 1024).toStringAsFixed(1)} MB';
}

/// Default plant name from a ZIP name: "Refinery_Plant-2_Drawings.zip" →
/// "Refinery Plant-2 Drawings".
String plantNameFromZip(String zipName) =>
    p.basenameWithoutExtension(zipName).replaceAll('_', ' ').replaceAll(RegExp(r'\s+'), ' ').trim();
