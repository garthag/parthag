import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:geolocator/geolocator.dart';
import 'package:image_picker/image_picker.dart';
import 'package:path/path.dart' as p;

import '../data/local_store.dart';
import '../models/models.dart';

enum CaptureMode { cameraPhoto, cameraVideo, galleryPhotos, galleryVideo }

class GeoFix {
  const GeoFix(this.latitude, this.longitude);
  final double latitude;
  final double longitude;
}

class MediaService {
  MediaService(this._store);

  final LocalStore _store;
  final _picker = ImagePicker();

  /// Opens camera or gallery and returns the chosen files.
  Future<List<(XFile, MediaType)>> pick(CaptureMode mode) async {
    switch (mode) {
      case CaptureMode.cameraPhoto:
        final f = await _picker.pickImage(
            source: ImageSource.camera, imageQuality: 80, maxWidth: 2048, maxHeight: 2048);
        return f == null ? [] : [(f, MediaType.photo)];
      case CaptureMode.cameraVideo:
        final f = await _picker.pickVideo(
            source: ImageSource.camera, maxDuration: const Duration(minutes: 3));
        return f == null ? [] : [(f, MediaType.video)];
      case CaptureMode.galleryPhotos:
        final fs = await _picker.pickMultiImage(imageQuality: 80, maxWidth: 2048, maxHeight: 2048);
        return [for (final f in fs) (f, MediaType.photo)];
      case CaptureMode.galleryVideo:
        final f = await _picker.pickVideo(source: ImageSource.gallery);
        return f == null ? [] : [(f, MediaType.video)];
    }
  }

  /// Copies a picked file into the app's private media folder so it stays
  /// available even if the user clears the gallery.
  Future<String> importFile(XFile file, String reportId) async {
    final root = await _store.mediaRoot();
    if (root == null) return file.path;
    final dir = Directory(p.join(root.path, reportId));
    await dir.create(recursive: true);
    var ext = p.extension(file.path);
    if (ext.isEmpty) ext = p.extension(file.name);
    final target = p.join(dir.path, '${newId()}$ext');
    await file.saveTo(target);
    return target;
  }

  /// Saves raw bytes (e.g. a signature PNG) into the media folder.
  Future<String> saveBytes(Uint8List bytes, String reportId, String name) async {
    final root = await _store.mediaRoot();
    if (root == null) return '';
    final dir = Directory(p.join(root.path, reportId));
    await dir.create(recursive: true);
    final f = File(p.join(dir.path, name));
    await f.writeAsBytes(bytes, flush: true);
    return f.path;
  }

  /// Best effort GPS fix for geo-tagging. Returns null if permission is
  /// denied, location is off, or no fix is obtained within the time limit.
  Future<GeoFix?> currentLocation() async {
    if (kIsWeb) return null;
    try {
      if (!await Geolocator.isLocationServiceEnabled()) return null;
      var perm = await Geolocator.checkPermission();
      if (perm == LocationPermission.denied) perm = await Geolocator.requestPermission();
      if (perm == LocationPermission.denied || perm == LocationPermission.deniedForever) return null;
      final pos = await Geolocator.getCurrentPosition(
        locationSettings: const LocationSettings(
          accuracy: LocationAccuracy.high,
          timeLimit: Duration(seconds: 12),
        ),
      );
      return GeoFix(pos.latitude, pos.longitude);
    } catch (_) {
      try {
        final last = await Geolocator.getLastKnownPosition();
        return last == null ? null : GeoFix(last.latitude, last.longitude);
      } catch (_) {
        return null;
      }
    }
  }
}
