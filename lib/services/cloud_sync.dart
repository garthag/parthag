import 'dart:io';

import 'package:cloud_firestore/cloud_firestore.dart';
import 'package:firebase_auth/firebase_auth.dart';
import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_storage/firebase_storage.dart';
import 'package:flutter/foundation.dart';
import 'package:path/path.dart' as p;

import '../models/models.dart';
import '../state/app_state.dart';

/// Optional Google cloud backup using Firebase (Auth + Firestore + Storage).
///
/// The app works fully offline without it. Once the project is connected to
/// Firebase (`flutterfire configure`), users can sign in with Google and sync
/// reports and media across the site team's devices.
class CloudSync extends ChangeNotifier {
  bool available = false;
  bool syncing = false;
  String? error;
  DateTime? lastSync;
  String progress = '';

  User? get user => available ? FirebaseAuth.instance.currentUser : null;

  Future<void> init() async {
    try {
      if (Firebase.apps.isEmpty) {
        await Firebase.initializeApp().timeout(const Duration(seconds: 8));
      }
      available = true;
      FirebaseAuth.instance.authStateChanges().listen((_) => notifyListeners());
    } catch (e) {
      available = false;
      debugPrint('Firebase not configured, running offline only: $e');
    }
    notifyListeners();
  }

  Future<void> signInWithGoogle() async {
    error = null;
    try {
      final provider = GoogleAuthProvider()..addScope('email');
      if (kIsWeb) {
        await FirebaseAuth.instance.signInWithPopup(provider);
      } else {
        await FirebaseAuth.instance.signInWithProvider(provider);
      }
    } catch (e) {
      error = 'Google sign-in failed: $e';
    }
    notifyListeners();
  }

  Future<void> signOut() async {
    await FirebaseAuth.instance.signOut();
    notifyListeners();
  }

  /// Two-way sync: uploads new media, pushes local documents, pulls remote
  /// documents and keeps whichever copy was updated last.
  Future<void> sync(AppState state) async {
    final u = user;
    if (u == null || syncing) return;
    syncing = true;
    error = null;
    notifyListeners();
    try {
      final db = FirebaseFirestore.instance;
      final root = db.collection('users').doc(u.uid);
      final storage = FirebaseStorage.instance;

      var done = 0;
      for (final r in state.reports) {
        progress = 'Uploading media ${++done}/${state.reports.length}';
        notifyListeners();
        var changed = false;
        for (final m in r.media) {
          if (m.remoteUrl.isNotEmpty || m.localPath.isEmpty) continue;
          final f = File(m.localPath);
          if (!await f.exists()) continue;
          final ref = storage.ref('users/${u.uid}/media/${r.id}/${m.id}${p.extension(m.localPath)}');
          await ref.putFile(f);
          m.remoteUrl = await ref.getDownloadURL();
          changed = true;
        }
        if (changed) await state.saveReport(r);
      }

      progress = 'Uploading reports';
      notifyListeners();
      // Firestore batches are limited to 500 writes.
      final writes = [
        for (final pr in state.projects) (root.collection('projects').doc(pr.id), pr.toJson()),
        for (final r in state.reports) (root.collection('reports').doc(r.id), r.toJson()),
      ];
      for (var i = 0; i < writes.length; i += 400) {
        final batch = db.batch();
        for (final (ref, data) in writes.skip(i).take(400)) {
          batch.set(ref, data);
        }
        await batch.commit();
      }

      progress = 'Downloading';
      notifyListeners();
      final rp = await root.collection('projects').get();
      final rr = await root.collection('reports').get();
      await state.mergeRemote(
        rp.docs.map((d) => Project.fromJson(d.data())).toList(),
        rr.docs.map((d) => DailyReport.fromJson(d.data())).toList(),
      );
      lastSync = DateTime.now();
    } catch (e) {
      error = 'Sync failed: $e';
    } finally {
      syncing = false;
      progress = '';
      notifyListeners();
    }
  }
}
