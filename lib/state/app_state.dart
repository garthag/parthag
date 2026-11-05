import 'dart:io';

import 'package:flutter/material.dart';

import '../data/local_store.dart';
import '../data/steel_sections.dart';
import '../models/models.dart';
import '../models/steel_takeoff.dart';

class UserProfile {
  UserProfile({this.name = '', this.designation = 'Site Engineer', this.company = ''});

  String name;
  String designation;
  String company;

  Map<String, dynamic> toJson() =>
      {'name': name, 'designation': designation, 'company': company};

  factory UserProfile.fromJson(Map<String, dynamic> j) => UserProfile(
        name: '${j['name'] ?? ''}',
        designation: '${j['designation'] ?? 'Site Engineer'}',
        company: '${j['company'] ?? ''}',
      );
}

/// Aggregate figures for a project, shown on the project dashboard.
class ProjectSummary {
  ProjectSummary({
    required this.reportCount,
    required this.latestManpower,
    required this.totalManDays,
    required this.safeManHours,
    required this.hindranceHours,
    required this.photos,
    required this.videos,
    required this.incidents,
    required this.activities,
  });

  final int reportCount;
  final int latestManpower;
  final int totalManDays;
  final double safeManHours;
  final double hindranceHours;
  final int photos;
  final int videos;
  final int incidents;

  /// Latest known state of every tracked activity.
  final List<WorkActivity> activities;

  double get overallProgress {
    final scoped = activities.where((a) => a.totalScope > 0).toList();
    if (scoped.isEmpty) return 0;
    return scoped.fold(0.0, (s, a) => s + a.percentComplete) / scoped.length;
  }
}

class AppState extends ChangeNotifier {
  AppState(this._store);

  final LocalStore _store;

  final List<Project> projects = [];
  final List<DailyReport> reports = [];
  final List<SteelTakeoff> takeoffs = [];
  UserProfile profile = UserProfile();
  bool loaded = false;

  LocalStore get store => _store;

  Future<void> load() async {
    final p = await _store.read('projects');
    final r = await _store.read('reports');
    final u = await _store.read('profile');
    final t = await _store.read('steel_takeoffs');
    projects
      ..clear()
      ..addAll((p as List? ?? []).map((e) => Project.fromJson(Map<String, dynamic>.from(e))));
    reports
      ..clear()
      ..addAll((r as List? ?? []).map((e) => DailyReport.fromJson(Map<String, dynamic>.from(e))));
    takeoffs
      ..clear()
      ..addAll((t as List? ?? []).map((e) => SteelTakeoff.fromJson(Map<String, dynamic>.from(e))));
    if (u is Map) profile = UserProfile.fromJson(Map<String, dynamic>.from(u));
    loaded = true;
    notifyListeners();
  }

  Future<void> _persistProjects() =>
      _store.write('projects', projects.map((e) => e.toJson()).toList());

  Future<void> _persistReports() =>
      _store.write('reports', reports.map((e) => e.toJson()).toList());

  Future<void> _persistTakeoffs() =>
      _store.write('steel_takeoffs', takeoffs.map((e) => e.toJson()).toList());

  Future<void> saveProfile(UserProfile p) async {
    profile = p;
    await _store.write('profile', p.toJson());
    notifyListeners();
  }

  Project? project(String id) {
    for (final p in projects) {
      if (p.id == id) return p;
    }
    return null;
  }

  DailyReport? report(String id) {
    for (final r in reports) {
      if (r.id == id) return r;
    }
    return null;
  }

  Future<void> saveProject(Project p) async {
    p.updatedAt = DateTime.now();
    final i = projects.indexWhere((e) => e.id == p.id);
    i >= 0 ? projects[i] = p : projects.add(p);
    await _persistProjects();
    notifyListeners();
  }

  Future<void> deleteProject(String id) async {
    for (final r in reports.where((r) => r.projectId == id).toList()) {
      await _deleteFiles(r);
    }
    reports.removeWhere((r) => r.projectId == id);
    takeoffs.removeWhere((t) => t.projectId == id);
    projects.removeWhere((p) => p.id == id);
    await _persistProjects();
    await _persistReports();
    await _persistTakeoffs();
    notifyListeners();
  }

  /// Reports of a project, newest first.
  List<DailyReport> reportsFor(String projectId) =>
      reports.where((r) => r.projectId == projectId).toList()
        ..sort((a, b) {
          final c = b.date.compareTo(a.date);
          return c != 0 ? c : b.reportNo.compareTo(a.reportNo);
        });

  DailyReport? reportOn(String projectId, DateTime date) {
    for (final r in reports) {
      if (r.projectId == projectId && DateUtils.isSameDay(r.date, date)) return r;
    }
    return null;
  }

  int nextReportNo(String projectId) =>
      reports.where((r) => r.projectId == projectId).fold(0, (m, r) => r.reportNo > m ? r.reportNo : m) + 1;

  /// Creates a report for [date]. When [carryForward] is set, the most recent
  /// earlier report seeds manpower, machinery, activities and stock.
  Future<DailyReport> createReport(String projectId, DateTime date, {bool carryForward = true}) async {
    final earlier = reportsFor(projectId).where((r) => r.date.isBefore(date)).toList();
    final no = nextReportNo(projectId);
    final r = carryForward && earlier.isNotEmpty
        ? earlier.first.carryForward(reportNo: no, date: date)
        : DailyReport(projectId: projectId, reportNo: no, date: date);
    if (r.preparedBy.name.isEmpty) {
      r.preparedBy
        ..name = profile.name
        ..designation = profile.designation;
    }
    reports.add(r);
    await _persistReports();
    notifyListeners();
    return r;
  }

  Future<void> saveReport(DailyReport r) async {
    r.updatedAt = DateTime.now();
    final i = reports.indexWhere((e) => e.id == r.id);
    i >= 0 ? reports[i] = r : reports.add(r);
    await _persistReports();
    notifyListeners();
  }

  Future<void> deleteReport(String id) async {
    final r = report(id);
    if (r == null) return;
    await _deleteFiles(r);
    reports.remove(r);
    // Keep take-off links consistent: drop postings to the deleted DPR.
    for (final t in takeoffs) {
      t.postings.removeWhere((p) => p.reportId == id);
    }
    await _persistReports();
    await _persistTakeoffs();
    notifyListeners();
  }

  Future<void> _deleteFiles(DailyReport r) async {
    final paths = [
      ...r.media.map((m) => m.localPath),
      r.preparedBy.signaturePath,
      r.checkedBy.signaturePath,
      r.approvedBy.signaturePath,
    ].where((p) => p.isNotEmpty);
    for (final path in paths) {
      try {
        final f = File(path);
        if (await f.exists()) await f.delete();
      } catch (_) {}
    }
  }

  /// Replaces local data with merged cloud data (used by sync).
  Future<void> mergeRemote(List<Project> remoteProjects, List<DailyReport> remoteReports,
      [List<SteelTakeoff> remoteTakeoffs = const []]) async {
    for (final rt in remoteTakeoffs) {
      final i = takeoffs.indexWhere((t) => t.id == rt.id);
      if (i < 0) {
        takeoffs.add(rt);
      } else if (rt.updatedAt.isAfter(takeoffs[i].updatedAt)) {
        takeoffs[i] = rt;
      }
    }
    for (final rp in remoteProjects) {
      final i = projects.indexWhere((p) => p.id == rp.id);
      if (i < 0) {
        projects.add(rp);
      } else if (rp.updatedAt.isAfter(projects[i].updatedAt)) {
        projects[i] = rp;
      }
    }
    for (final rr in remoteReports) {
      final i = reports.indexWhere((r) => r.id == rr.id);
      if (i < 0) {
        reports.add(rr);
      } else if (rr.updatedAt.isAfter(reports[i].updatedAt)) {
        reports[i] = rr;
      }
    }
    await _persistProjects();
    await _persistReports();
    await _persistTakeoffs();
    notifyListeners();
  }

  // ------------------------------------------------------- steel take-offs

  SteelTakeoff? takeoff(String id) {
    for (final t in takeoffs) {
      if (t.id == id) return t;
    }
    return null;
  }

  /// Take-offs of a project, newest first.
  List<SteelTakeoff> takeoffsFor(String projectId) =>
      takeoffs.where((t) => t.projectId == projectId).toList()..sort((a, b) => b.date.compareTo(a.date));

  /// Take-offs that were posted into a DPR (reverse link).
  List<SteelTakeoff> takeoffsPostedTo(String reportId) =>
      takeoffs.where((t) => t.postedTo(reportId)).toList();

  Future<void> saveTakeoff(SteelTakeoff t) async {
    t.updatedAt = DateTime.now();
    final i = takeoffs.indexWhere((e) => e.id == t.id);
    i >= 0 ? takeoffs[i] = t : takeoffs.add(t);
    final p = project(t.projectId);
    if (p != null) p.updatedAt = DateTime.now();
    await _persistTakeoffs();
    notifyListeners();
  }

  Future<void> deleteTakeoff(String id) async {
    takeoffs.removeWhere((t) => t.id == id);
    await _persistTakeoffs();
    notifyListeners();
  }

  /// Posts [mt] tonnes of a take-off into a DPR.
  ///
  /// * Work done: adds the quantity to the "Structural steel erection" item
  ///   for this take-off (unit MT, scope = take-off net weight).
  /// * Materials: adds the received steel family-wise (unit MT).
  ///
  /// The posting is recorded on the take-off so both sides stay linked.
  Future<void> postTakeoffToReport(SteelTakeoff t, DailyReport r,
      {required double mt, bool asWork = true, bool asMaterial = false}) async {
    if (r.isLocked) throw StateError('DPR-${r.reportNo} is approved and locked');
    if (mt <= 0 || (!asWork && !asMaterial)) return;
    double round3(double v) => (v * 1000).roundToDouble() / 1000;
    final label = t.title.isEmpty ? 'Steel take-off' : t.title;

    if (asWork) {
      const description = 'Structural steel erection';
      final existing = r.activities.where((a) =>
          a.description.trim().toLowerCase() == description.toLowerCase() &&
          a.location.trim().toLowerCase() == label.trim().toLowerCase());
      if (existing.isNotEmpty) {
        final a = existing.first;
        a.todayQty = round3(a.todayQty + mt);
        if (a.totalScope < t.netMt) a.totalScope = round3(t.netMt);
      } else {
        r.activities.add(WorkActivity(
          description: description,
          location: label,
          unit: 'MT',
          totalScope: round3(t.netMt),
          todayQty: round3(mt),
          remarks: t.drawingNo.isEmpty ? 'From steel take-off' : 'From steel take-off, Dwg ${t.drawingNo}',
        ));
      }
    }

    if (asMaterial && t.netKg > 0) {
      final factor = mt * 1000 / t.netKg;
      for (final g in t.groups) {
        final f = SteelTables.byCode(g.familyCode);
        final name = f == null ? 'Steel – ${g.familyCode}' : 'Steel – ${f.code} (${f.name})';
        final qty = round3(g.mt * factor);
        if (qty <= 0) continue;
        final existing = r.materials.where((m) => m.name == name && m.unit == 'MT');
        if (existing.isNotEmpty) {
          existing.first.received = round3(existing.first.received + qty);
        } else {
          r.materials.add(MaterialEntry(
            name: name,
            specification: f?.standard ?? '',
            unit: 'MT',
            received: qty,
            challanNo: t.drawingNo,
          ));
        }
      }
    }

    t.postings.add(TakeoffPosting(reportId: r.id, mt: round3(mt), asWork: asWork, asMaterial: asMaterial));
    await saveReport(r);
    await saveTakeoff(t);
  }

  ProjectSummary summary(String projectId) {
    final list = reportsFor(projectId);
    final latest = <String, WorkActivity>{};
    // Oldest to newest so that the newest state of an activity wins.
    for (final r in list.reversed) {
      for (final a in r.activities) {
        latest[a.trackingKey] = a;
      }
    }
    return ProjectSummary(
      reportCount: list.length,
      latestManpower: list.isEmpty ? 0 : list.first.totalManpower,
      totalManDays: list.fold(0, (s, r) => s + r.totalManpower),
      safeManHours: list.where((r) => r.safety.isSafeDay).fold(0.0, (s, r) => s + r.totalManHours),
      hindranceHours: list.fold(0.0, (s, r) => s + r.hindranceHours),
      photos: list.fold(0, (s, r) => s + r.photoCount),
      videos: list.fold(0, (s, r) => s + r.videoCount),
      incidents: list.fold(0, (s, r) => s + r.safety.incidents.length),
      activities: latest.values.toList(),
    );
  }
}
