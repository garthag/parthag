import 'dart:io';

import 'package:flutter/material.dart';

import '../data/local_store.dart';
import '../models/models.dart';

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
  UserProfile profile = UserProfile();
  bool loaded = false;

  LocalStore get store => _store;

  Future<void> load() async {
    final p = await _store.read('projects');
    final r = await _store.read('reports');
    final u = await _store.read('profile');
    projects
      ..clear()
      ..addAll((p as List? ?? []).map((e) => Project.fromJson(Map<String, dynamic>.from(e))));
    reports
      ..clear()
      ..addAll((r as List? ?? []).map((e) => DailyReport.fromJson(Map<String, dynamic>.from(e))));
    if (u is Map) profile = UserProfile.fromJson(Map<String, dynamic>.from(u));
    loaded = true;
    notifyListeners();
  }

  Future<void> _persistProjects() =>
      _store.write('projects', projects.map((e) => e.toJson()).toList());

  Future<void> _persistReports() =>
      _store.write('reports', reports.map((e) => e.toJson()).toList());

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
    projects.removeWhere((p) => p.id == id);
    await _persistProjects();
    await _persistReports();
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
    await _persistReports();
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
  Future<void> mergeRemote(List<Project> remoteProjects, List<DailyReport> remoteReports) async {
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
    notifyListeners();
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
