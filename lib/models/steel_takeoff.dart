// Steel take-off: a project's list of steel members with their IS section,
// length and number, giving weight in kg / MT.
//
// Relations:
//   Project 1 ── n SteelTakeoff 1 ── n SteelTakeoffItem ── 1 IS section
//   SteelTakeoff n ── n DailyReport (through [SteelTakeoff.postings])

import '../data/steel_sections.dart';
import 'models.dart';

double _d(dynamic v) => v is num ? v.toDouble() : double.tryParse('$v') ?? 0;
int _i(dynamic v) => v is num ? v.toInt() : int.tryParse('$v') ?? 0;
String _s(dynamic v) => v == null ? '' : '$v';
DateTime? _dt(dynamic v) => v == null ? null : DateTime.tryParse('$v');

enum TakeoffStatus { estimate, ordered, received, erected }

extension TakeoffStatusLabel on TakeoffStatus {
  String get label => switch (this) {
        TakeoffStatus.estimate => 'Estimate',
        TakeoffStatus.ordered => 'Ordered',
        TakeoffStatus.received => 'Received',
        TakeoffStatus.erected => 'Erected',
      };
}

class SteelTakeoffItem {
  SteelTakeoffItem({
    String? id,
    required this.familyCode,
    required this.designation,
    required this.kgPerM,
    this.lengthM = 0,
    this.nos = 1,
    this.memberMark = '',
    this.location = '',
    this.remarks = '',
  }) : id = id ?? newId();

  factory SteelTakeoffItem.fromRef(SteelRef r, {double lengthM = 0, int nos = 1}) => SteelTakeoffItem(
        familyCode: r.family.code,
        designation: r.designation,
        kgPerM: r.kgPerM,
        lengthM: lengthM,
        nos: nos,
      );

  final String id;

  /// Links to the IS table ([SteelLibrary.find]); custom sizes keep their
  /// own designation and kg/m.
  String familyCode;
  String designation;
  double kgPerM;
  double lengthM;
  int nos;

  /// Drawing member mark, e.g. "C1", "B12", "Purlin P3".
  String memberMark;

  /// Block / floor / grid.
  String location;
  String remarks;

  double get totalLengthM => lengthM * nos;
  double get kg => kgPerM * lengthM * nos;
  double get mt => kg / 1000;

  SteelRef? get ref => SteelLibrary.find(familyCode, designation);
  SteelFamily? get family => SteelTables.byCode(familyCode);

  /// Category for grouping; custom families fall back to their own.
  SteelCategory? get category => family?.category;

  Map<String, dynamic> toJson() => {
        'id': id,
        'familyCode': familyCode,
        'designation': designation,
        'kgPerM': kgPerM,
        'lengthM': lengthM,
        'nos': nos,
        'memberMark': memberMark,
        'location': location,
        'remarks': remarks,
      };

  factory SteelTakeoffItem.fromJson(Map<String, dynamic> j) => SteelTakeoffItem(
        id: j['id'] == null ? null : _s(j['id']),
        familyCode: _s(j['familyCode']),
        designation: _s(j['designation']),
        kgPerM: _d(j['kgPerM']),
        lengthM: _d(j['lengthM']),
        nos: _i(j['nos']),
        memberMark: _s(j['memberMark']),
        location: _s(j['location']),
        remarks: _s(j['remarks']),
      );
}

/// A record that a take-off was posted into a DPR.
class TakeoffPosting {
  TakeoffPosting({required this.reportId, required this.mt, this.asWork = false, this.asMaterial = false, DateTime? at})
      : at = at ?? DateTime.now();

  final String reportId;
  final double mt;
  final bool asWork;
  final bool asMaterial;
  final DateTime at;

  Map<String, dynamic> toJson() =>
      {'reportId': reportId, 'mt': mt, 'asWork': asWork, 'asMaterial': asMaterial, 'at': at.toIso8601String()};

  factory TakeoffPosting.fromJson(Map<String, dynamic> j) => TakeoffPosting(
        reportId: _s(j['reportId']),
        mt: _d(j['mt']),
        asWork: j['asWork'] == true,
        asMaterial: j['asMaterial'] == true,
        at: _dt(j['at']),
      );
}

/// Totals for one family inside a take-off.
class TakeoffGroup {
  TakeoffGroup(this.familyCode, this.title, this.items);
  final String familyCode;
  final String title;
  final List<SteelTakeoffItem> items;

  double get kg => items.fold(0.0, (s, i) => s + i.kg);
  double get mt => kg / 1000;
  int get nos => items.fold(0, (s, i) => s + i.nos);
  double get lengthM => items.fold(0.0, (s, i) => s + i.totalLengthM);
}

class SteelTakeoff {
  SteelTakeoff({
    String? id,
    required this.projectId,
    this.title = '',
    DateTime? date,
    this.drawingNo = '',
    this.status = TakeoffStatus.estimate,
    this.wastagePercent = 0,
    List<SteelTakeoffItem>? items,
    List<TakeoffPosting>? postings,
    DateTime? updatedAt,
  })  : id = id ?? newId(),
        date = date ?? DateTime.now(),
        items = items ?? [],
        postings = postings ?? [],
        updatedAt = updatedAt ?? DateTime.now();

  final String id;
  final String projectId;
  String title;
  DateTime date;

  /// Structural drawing reference.
  String drawingNo;
  TakeoffStatus status;

  /// Cutting / rolling margin added on top of the net weight.
  double wastagePercent;
  final List<SteelTakeoffItem> items;
  final List<TakeoffPosting> postings;
  DateTime updatedAt;

  double get netKg => items.fold(0.0, (s, i) => s + i.kg);
  double get wastageKg => netKg * wastagePercent / 100;
  double get grossKg => netKg + wastageKg;
  double get netMt => netKg / 1000;
  double get grossMt => grossKg / 1000;
  int get pieces => items.fold(0, (s, i) => s + i.nos);

  /// Items grouped by family, in IS table order (category → family).
  List<TakeoffGroup> get groups {
    final order = {for (final (i, f) in SteelTables.families.indexed) f.code: i};
    final map = <String, List<SteelTakeoffItem>>{};
    for (final it in items) {
      map.putIfAbsent(it.familyCode, () => []).add(it);
    }
    final codes = map.keys.toList()..sort((a, b) => (order[a] ?? 999).compareTo(order[b] ?? 999));
    return [
      for (final c in codes) TakeoffGroup(c, SteelTables.byCode(c)?.title ?? c, map[c]!),
    ];
  }

  /// Net weight per category (top of the hierarchy).
  Map<SteelCategory?, double> get kgByCategory {
    final out = <SteelCategory?, double>{};
    for (final it in items) {
      out[it.category] = (out[it.category] ?? 0) + it.kg;
    }
    return out;
  }

  bool postedTo(String reportId) => postings.any((p) => p.reportId == reportId);

  Map<String, dynamic> toJson() => {
        'id': id,
        'projectId': projectId,
        'title': title,
        'date': date.toIso8601String(),
        'drawingNo': drawingNo,
        'status': status.name,
        'wastagePercent': wastagePercent,
        'items': items.map((e) => e.toJson()).toList(),
        'postings': postings.map((e) => e.toJson()).toList(),
        'updatedAt': updatedAt.toIso8601String(),
      };

  factory SteelTakeoff.fromJson(Map<String, dynamic> j) {
    List<T> list<T>(dynamic v, T Function(Map<String, dynamic>) f) =>
        v is List ? v.map((e) => f(Map<String, dynamic>.from(e as Map))).toList() : <T>[];
    return SteelTakeoff(
      id: _s(j['id']),
      projectId: _s(j['projectId']),
      title: _s(j['title']),
      date: _dt(j['date']),
      drawingNo: _s(j['drawingNo']),
      status: TakeoffStatus.values.firstWhere((s) => s.name == j['status'], orElse: () => TakeoffStatus.estimate),
      wastagePercent: _d(j['wastagePercent']),
      items: list(j['items'], SteelTakeoffItem.fromJson),
      postings: list(j['postings'], TakeoffPosting.fromJson),
      updatedAt: _dt(j['updatedAt']),
    );
  }
}
