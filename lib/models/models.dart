// Data models for the Daily Progress Report (DPR).
//
// All models are plain Dart objects with toJson/fromJson so they can be
// stored locally as JSON and mirrored to Cloud Firestore unchanged.

import 'package:uuid/uuid.dart';

const _uuid = Uuid();

String newId() => _uuid.v4();

double _d(dynamic v) => v is num ? v.toDouble() : double.tryParse('$v') ?? 0;
int _i(dynamic v) => v is num ? v.toInt() : int.tryParse('$v') ?? 0;
String _s(dynamic v) => v == null ? '' : '$v';
DateTime? _dt(dynamic v) => v == null ? null : DateTime.tryParse('$v');
List<T> _list<T>(dynamic v, T Function(Map<String, dynamic>) f) =>
    v is List ? v.map((e) => f(Map<String, dynamic>.from(e as Map))).toList() : <T>[];
List<String> _strings(dynamic v) => v is List ? v.map((e) => '$e').toList() : <String>[];

class Project {
  Project({
    String? id,
    required this.name,
    this.code = '',
    this.client = '',
    this.contractor = '',
    this.consultant = '',
    this.workOrderNo = '',
    this.location = '',
    this.startDate,
    this.completionDate,
    this.contractValueLakh = 0,
    this.siteEngineer = '',
    DateTime? updatedAt,
  })  : id = id ?? newId(),
        updatedAt = updatedAt ?? DateTime.now();

  final String id;
  String name;
  String code;
  String client;
  String contractor;

  /// PMC / Architect / Engineer-in-charge.
  String consultant;
  String workOrderNo;
  String location;
  DateTime? startDate;
  DateTime? completionDate;

  /// Contract value in ₹ lakh, the usual unit on Indian sites.
  double contractValueLakh;
  String siteEngineer;
  DateTime updatedAt;

  Map<String, dynamic> toJson() => {
        'id': id,
        'name': name,
        'code': code,
        'client': client,
        'contractor': contractor,
        'consultant': consultant,
        'workOrderNo': workOrderNo,
        'location': location,
        'startDate': startDate?.toIso8601String(),
        'completionDate': completionDate?.toIso8601String(),
        'contractValueLakh': contractValueLakh,
        'siteEngineer': siteEngineer,
        'updatedAt': updatedAt.toIso8601String(),
      };

  factory Project.fromJson(Map<String, dynamic> j) => Project(
        id: _s(j['id']),
        name: _s(j['name']),
        code: _s(j['code']),
        client: _s(j['client']),
        contractor: _s(j['contractor']),
        consultant: _s(j['consultant']),
        workOrderNo: _s(j['workOrderNo']),
        location: _s(j['location']),
        startDate: _dt(j['startDate']),
        completionDate: _dt(j['completionDate']),
        contractValueLakh: _d(j['contractValueLakh']),
        siteEngineer: _s(j['siteEngineer']),
        updatedAt: _dt(j['updatedAt']),
      );
}

enum ReportStatus { draft, submitted, approved }

extension ReportStatusLabel on ReportStatus {
  String get label => switch (this) {
        ReportStatus.draft => 'Draft',
        ReportStatus.submitted => 'Submitted',
        ReportStatus.approved => 'Approved',
      };
}

class WeatherInfo {
  WeatherInfo({
    this.morning = 'Sunny',
    this.afternoon = 'Sunny',
    this.evening = 'Sunny',
    this.minTempC = 0,
    this.maxTempC = 0,
    this.rainfallMm = 0,
    this.hoursLost = 0,
  });

  String morning;
  String afternoon;
  String evening;
  double minTempC;
  double maxTempC;
  double rainfallMm;
  double hoursLost;

  Map<String, dynamic> toJson() => {
        'morning': morning,
        'afternoon': afternoon,
        'evening': evening,
        'minTempC': minTempC,
        'maxTempC': maxTempC,
        'rainfallMm': rainfallMm,
        'hoursLost': hoursLost,
      };

  factory WeatherInfo.fromJson(Map<String, dynamic> j) => WeatherInfo(
        morning: _s(j['morning']),
        afternoon: _s(j['afternoon']),
        evening: _s(j['evening']),
        minTempC: _d(j['minTempC']),
        maxTempC: _d(j['maxTempC']),
        rainfallMm: _d(j['rainfallMm']),
        hoursLost: _d(j['hoursLost']),
      );
}

class ManpowerEntry {
  ManpowerEntry({
    this.agency = '',
    this.category = '',
    this.skill = 'Skilled',
    this.male = 0,
    this.female = 0,
    this.hours = 8,
  });

  /// Contractor / sub-contractor / petty contractor.
  String agency;
  String category;

  /// Skilled / Semi-skilled / Unskilled / Highly skilled (Minimum Wages Act).
  String skill;
  int male;
  int female;
  double hours;

  int get total => male + female;
  double get manHours => total * hours;

  Map<String, dynamic> toJson() => {
        'agency': agency,
        'category': category,
        'skill': skill,
        'male': male,
        'female': female,
        'hours': hours,
      };

  factory ManpowerEntry.fromJson(Map<String, dynamic> j) => ManpowerEntry(
        agency: _s(j['agency']),
        category: _s(j['category']),
        skill: _s(j['skill']),
        male: _i(j['male']),
        female: _i(j['female']),
        hours: _d(j['hours']),
      );
}

class MachineryEntry {
  MachineryEntry({
    this.name = '',
    this.ownership = 'Hired',
    this.count = 1,
    this.workingHrs = 0,
    this.idleHrs = 0,
    this.breakdownHrs = 0,
    this.remarks = '',
  });

  String name;
  String ownership;
  int count;
  double workingHrs;
  double idleHrs;
  double breakdownHrs;
  String remarks;

  Map<String, dynamic> toJson() => {
        'name': name,
        'ownership': ownership,
        'count': count,
        'workingHrs': workingHrs,
        'idleHrs': idleHrs,
        'breakdownHrs': breakdownHrs,
        'remarks': remarks,
      };

  factory MachineryEntry.fromJson(Map<String, dynamic> j) => MachineryEntry(
        name: _s(j['name']),
        ownership: _s(j['ownership']),
        count: _i(j['count']),
        workingHrs: _d(j['workingHrs']),
        idleHrs: _d(j['idleHrs']),
        breakdownHrs: _d(j['breakdownHrs']),
        remarks: _s(j['remarks']),
      );
}

class WorkActivity {
  WorkActivity({
    String? id,
    this.boqItemNo = '',
    this.description = '',
    this.location = '',
    this.unit = 'cum',
    this.totalScope = 0,
    this.previousQty = 0,
    this.todayQty = 0,
    this.remarks = '',
  }) : id = id ?? newId();

  final String id;
  String boqItemNo;
  String description;

  /// Block / Tower / Floor / Grid reference.
  String location;

  /// Unit of measurement as per IS 1200.
  String unit;
  double totalScope;

  /// Cumulative quantity up to the previous report.
  double previousQty;
  double todayQty;
  String remarks;

  double get cumulativeQty => previousQty + todayQty;
  double get percentComplete =>
      totalScope <= 0 ? 0 : (cumulativeQty / totalScope * 100).clamp(0, 100).toDouble();

  /// Key used to carry cumulative quantities forward between reports.
  String get trackingKey =>
      '${boqItemNo.trim().toLowerCase()}|${description.trim().toLowerCase()}|${location.trim().toLowerCase()}';

  Map<String, dynamic> toJson() => {
        'id': id,
        'boqItemNo': boqItemNo,
        'description': description,
        'location': location,
        'unit': unit,
        'totalScope': totalScope,
        'previousQty': previousQty,
        'todayQty': todayQty,
        'remarks': remarks,
      };

  factory WorkActivity.fromJson(Map<String, dynamic> j) => WorkActivity(
        id: j['id'] == null ? null : _s(j['id']),
        boqItemNo: _s(j['boqItemNo']),
        description: _s(j['description']),
        location: _s(j['location']),
        unit: _s(j['unit']),
        totalScope: _d(j['totalScope']),
        previousQty: _d(j['previousQty']),
        todayQty: _d(j['todayQty']),
        remarks: _s(j['remarks']),
      );
}

class MaterialEntry {
  MaterialEntry({
    this.name = '',
    this.specification = '',
    this.unit = 'bags',
    this.opening = 0,
    this.received = 0,
    this.consumed = 0,
    this.supplier = '',
    this.challanNo = '',
  });

  String name;
  String specification;
  String unit;
  double opening;
  double received;
  double consumed;
  String supplier;

  /// Delivery challan / invoice reference.
  String challanNo;

  double get closing => opening + received - consumed;

  Map<String, dynamic> toJson() => {
        'name': name,
        'specification': specification,
        'unit': unit,
        'opening': opening,
        'received': received,
        'consumed': consumed,
        'supplier': supplier,
        'challanNo': challanNo,
      };

  factory MaterialEntry.fromJson(Map<String, dynamic> j) => MaterialEntry(
        name: _s(j['name']),
        specification: _s(j['specification']),
        unit: _s(j['unit']),
        opening: _d(j['opening']),
        received: _d(j['received']),
        consumed: _d(j['consumed']),
        supplier: _s(j['supplier']),
        challanNo: _s(j['challanNo']),
      );
}

class QualityEntry {
  QualityEntry({
    this.testType = '',
    this.location = '',
    this.grade = '',
    this.quantity = 0,
    this.samples = 0,
    this.result = 'Pending',
    this.standard = '',
    this.remarks = '',
  });

  String testType;
  String location;

  /// Concrete grade (IS 456) or material grade.
  String grade;
  double quantity;

  /// Cubes cast / samples taken.
  int samples;
  String result;
  String standard;
  String remarks;

  Map<String, dynamic> toJson() => {
        'testType': testType,
        'location': location,
        'grade': grade,
        'quantity': quantity,
        'samples': samples,
        'result': result,
        'standard': standard,
        'remarks': remarks,
      };

  factory QualityEntry.fromJson(Map<String, dynamic> j) => QualityEntry(
        testType: _s(j['testType']),
        location: _s(j['location']),
        grade: _s(j['grade']),
        quantity: _d(j['quantity']),
        samples: _i(j['samples']),
        result: _s(j['result']),
        standard: _s(j['standard']),
        remarks: _s(j['remarks']),
      );
}

class SafetyIncident {
  SafetyIncident({this.type = 'Near miss', this.description = '', this.actionTaken = ''});

  String type;
  String description;
  String actionTaken;

  Map<String, dynamic> toJson() =>
      {'type': type, 'description': description, 'actionTaken': actionTaken};

  factory SafetyIncident.fromJson(Map<String, dynamic> j) => SafetyIncident(
        type: _s(j['type']),
        description: _s(j['description']),
        actionTaken: _s(j['actionTaken']),
      );
}

class SafetyInfo {
  SafetyInfo({
    this.toolboxTalk = false,
    this.toolboxTopic = '',
    this.toolboxAttendees = 0,
    this.ppeCompliancePct = 100,
    List<String>? permits,
    List<String>? ppeChecked,
    List<SafetyIncident>? incidents,
    this.observations = '',
  })  : permits = permits ?? [],
        ppeChecked = ppeChecked ?? [],
        incidents = incidents ?? [];

  bool toolboxTalk;
  String toolboxTopic;
  int toolboxAttendees;
  double ppeCompliancePct;
  List<String> permits;
  List<String> ppeChecked;
  List<SafetyIncident> incidents;
  String observations;

  /// A day counts towards safe man-hours only if there was no reportable
  /// (lost time / dangerous occurrence / fatal) incident.
  bool get isSafeDay => !incidents.any((i) => const {
        'Lost time injury (LTI)',
        'Dangerous occurrence',
        'Fatal',
      }.contains(i.type));

  Map<String, dynamic> toJson() => {
        'toolboxTalk': toolboxTalk,
        'toolboxTopic': toolboxTopic,
        'toolboxAttendees': toolboxAttendees,
        'ppeCompliancePct': ppeCompliancePct,
        'permits': permits,
        'ppeChecked': ppeChecked,
        'incidents': incidents.map((e) => e.toJson()).toList(),
        'observations': observations,
      };

  factory SafetyInfo.fromJson(Map<String, dynamic> j) => SafetyInfo(
        toolboxTalk: j['toolboxTalk'] == true,
        toolboxTopic: _s(j['toolboxTopic']),
        toolboxAttendees: _i(j['toolboxAttendees']),
        ppeCompliancePct: _d(j['ppeCompliancePct']),
        permits: _strings(j['permits']),
        ppeChecked: _strings(j['ppeChecked']),
        incidents: _list(j['incidents'], SafetyIncident.fromJson),
        observations: _s(j['observations']),
      );
}

class Hindrance {
  Hindrance({
    this.category = '',
    this.description = '',
    this.hoursLost = 0,
    this.responsibility = 'Client',
    this.actionRequired = '',
  });

  String category;
  String description;
  double hoursLost;
  String responsibility;
  String actionRequired;

  Map<String, dynamic> toJson() => {
        'category': category,
        'description': description,
        'hoursLost': hoursLost,
        'responsibility': responsibility,
        'actionRequired': actionRequired,
      };

  factory Hindrance.fromJson(Map<String, dynamic> j) => Hindrance(
        category: _s(j['category']),
        description: _s(j['description']),
        hoursLost: _d(j['hoursLost']),
        responsibility: _s(j['responsibility']),
        actionRequired: _s(j['actionRequired']),
      );
}

class Visitor {
  Visitor({this.name = '', this.organisation = '', this.purpose = ''});

  String name;
  String organisation;
  String purpose;

  Map<String, dynamic> toJson() =>
      {'name': name, 'organisation': organisation, 'purpose': purpose};

  factory Visitor.fromJson(Map<String, dynamic> j) => Visitor(
        name: _s(j['name']),
        organisation: _s(j['organisation']),
        purpose: _s(j['purpose']),
      );
}

enum MediaType { photo, video }

class MediaItem {
  MediaItem({
    String? id,
    required this.type,
    required this.localPath,
    this.remoteUrl = '',
    this.caption = '',
    this.category = 'Progress',
    List<String>? tags,
    this.activityId = '',
    this.location = '',
    this.latitude,
    this.longitude,
    DateTime? capturedAt,
  })  : id = id ?? newId(),
        tags = tags ?? [],
        capturedAt = capturedAt ?? DateTime.now();

  final String id;
  final MediaType type;
  String localPath;
  String remoteUrl;
  String caption;
  String category;
  List<String> tags;

  /// Optional link to a [WorkActivity] in the same report.
  String activityId;

  /// Free text site location, e.g. "Tower B / 3rd floor / Grid C-4".
  String location;
  double? latitude;
  double? longitude;
  DateTime capturedAt;

  bool get hasGeoTag => latitude != null && longitude != null;

  Map<String, dynamic> toJson() => {
        'id': id,
        'type': type.name,
        'localPath': localPath,
        'remoteUrl': remoteUrl,
        'caption': caption,
        'category': category,
        'tags': tags,
        'activityId': activityId,
        'location': location,
        'latitude': latitude,
        'longitude': longitude,
        'capturedAt': capturedAt.toIso8601String(),
      };

  factory MediaItem.fromJson(Map<String, dynamic> j) => MediaItem(
        id: _s(j['id']),
        type: j['type'] == 'video' ? MediaType.video : MediaType.photo,
        localPath: _s(j['localPath']),
        remoteUrl: _s(j['remoteUrl']),
        caption: _s(j['caption']),
        category: _s(j['category']),
        tags: _strings(j['tags']),
        activityId: _s(j['activityId']),
        location: _s(j['location']),
        latitude: (j['latitude'] as num?)?.toDouble(),
        longitude: (j['longitude'] as num?)?.toDouble(),
        capturedAt: _dt(j['capturedAt']),
      );
}

class SignOff {
  SignOff({this.name = '', this.designation = '', this.signaturePath = '', this.signedAt});

  String name;
  String designation;
  String signaturePath;
  DateTime? signedAt;

  bool get isSigned => signaturePath.isNotEmpty;

  Map<String, dynamic> toJson() => {
        'name': name,
        'designation': designation,
        'signaturePath': signaturePath,
        'signedAt': signedAt?.toIso8601String(),
      };

  factory SignOff.fromJson(Map<String, dynamic> j) => SignOff(
        name: _s(j['name']),
        designation: _s(j['designation']),
        signaturePath: _s(j['signaturePath']),
        signedAt: _dt(j['signedAt']),
      );
}

class DailyReport {
  DailyReport({
    String? id,
    required this.projectId,
    required this.reportNo,
    required this.date,
    this.status = ReportStatus.draft,
    this.shift = 'General (9:00 - 18:00)',
    WeatherInfo? weather,
    List<ManpowerEntry>? manpower,
    List<MachineryEntry>? machinery,
    List<WorkActivity>? activities,
    List<MaterialEntry>? materials,
    List<QualityEntry>? quality,
    SafetyInfo? safety,
    List<Hindrance>? hindrances,
    List<Visitor>? visitors,
    List<MediaItem>? media,
    this.instructions = '',
    this.remarks = '',
    List<String>? tomorrowPlan,
    SignOff? preparedBy,
    SignOff? checkedBy,
    SignOff? approvedBy,
    DateTime? updatedAt,
  })  : id = id ?? newId(),
        weather = weather ?? WeatherInfo(),
        manpower = manpower ?? [],
        machinery = machinery ?? [],
        activities = activities ?? [],
        materials = materials ?? [],
        quality = quality ?? [],
        safety = safety ?? SafetyInfo(),
        hindrances = hindrances ?? [],
        visitors = visitors ?? [],
        media = media ?? [],
        tomorrowPlan = tomorrowPlan ?? [],
        preparedBy = preparedBy ?? SignOff(designation: 'Site Engineer'),
        checkedBy = checkedBy ?? SignOff(designation: 'Project Manager'),
        approvedBy = approvedBy ?? SignOff(designation: 'Client / PMC Representative'),
        updatedAt = updatedAt ?? DateTime.now();

  final String id;
  final String projectId;
  int reportNo;
  DateTime date;
  ReportStatus status;
  String shift;
  WeatherInfo weather;
  List<ManpowerEntry> manpower;
  List<MachineryEntry> machinery;
  List<WorkActivity> activities;
  List<MaterialEntry> materials;
  List<QualityEntry> quality;
  SafetyInfo safety;
  List<Hindrance> hindrances;
  List<Visitor> visitors;
  List<MediaItem> media;

  /// Instructions received from client / consultant.
  String instructions;
  String remarks;
  List<String> tomorrowPlan;
  SignOff preparedBy;
  SignOff checkedBy;
  SignOff approvedBy;
  DateTime updatedAt;

  int get totalManpower => manpower.fold(0, (s, e) => s + e.total);
  double get totalManHours => manpower.fold(0.0, (s, e) => s + e.manHours);
  double get hindranceHours => hindrances.fold(0.0, (s, e) => s + e.hoursLost);
  int get photoCount => media.where((m) => m.type == MediaType.photo).length;
  int get videoCount => media.where((m) => m.type == MediaType.video).length;
  bool get isLocked => status == ReportStatus.approved;

  Map<String, dynamic> toJson() => {
        'id': id,
        'projectId': projectId,
        'reportNo': reportNo,
        'date': date.toIso8601String(),
        'status': status.name,
        'shift': shift,
        'weather': weather.toJson(),
        'manpower': manpower.map((e) => e.toJson()).toList(),
        'machinery': machinery.map((e) => e.toJson()).toList(),
        'activities': activities.map((e) => e.toJson()).toList(),
        'materials': materials.map((e) => e.toJson()).toList(),
        'quality': quality.map((e) => e.toJson()).toList(),
        'safety': safety.toJson(),
        'hindrances': hindrances.map((e) => e.toJson()).toList(),
        'visitors': visitors.map((e) => e.toJson()).toList(),
        'media': media.map((e) => e.toJson()).toList(),
        'instructions': instructions,
        'remarks': remarks,
        'tomorrowPlan': tomorrowPlan,
        'preparedBy': preparedBy.toJson(),
        'checkedBy': checkedBy.toJson(),
        'approvedBy': approvedBy.toJson(),
        'updatedAt': updatedAt.toIso8601String(),
      };

  factory DailyReport.fromJson(Map<String, dynamic> j) {
    Map<String, dynamic> m(dynamic v) => v is Map ? Map<String, dynamic>.from(v) : {};
    return DailyReport(
      id: _s(j['id']),
      projectId: _s(j['projectId']),
      reportNo: _i(j['reportNo']),
      date: _dt(j['date']) ?? DateTime.now(),
      status: ReportStatus.values.firstWhere((s) => s.name == j['status'],
          orElse: () => ReportStatus.draft),
      shift: _s(j['shift']),
      weather: WeatherInfo.fromJson(m(j['weather'])),
      manpower: _list(j['manpower'], ManpowerEntry.fromJson),
      machinery: _list(j['machinery'], MachineryEntry.fromJson),
      activities: _list(j['activities'], WorkActivity.fromJson),
      materials: _list(j['materials'], MaterialEntry.fromJson),
      quality: _list(j['quality'], QualityEntry.fromJson),
      safety: SafetyInfo.fromJson(m(j['safety'])),
      hindrances: _list(j['hindrances'], Hindrance.fromJson),
      visitors: _list(j['visitors'], Visitor.fromJson),
      media: _list(j['media'], MediaItem.fromJson),
      instructions: _s(j['instructions']),
      remarks: _s(j['remarks']),
      tomorrowPlan: _strings(j['tomorrowPlan']),
      preparedBy: SignOff.fromJson(m(j['preparedBy'])),
      checkedBy: SignOff.fromJson(m(j['checkedBy'])),
      approvedBy: SignOff.fromJson(m(j['approvedBy'])),
      updatedAt: _dt(j['updatedAt']),
    );
  }

  DailyReport copy() => DailyReport.fromJson(toJson());

  /// Starts the next day's report from this one: manpower, machinery and the
  /// activity list carry over, cumulative quantities roll forward and
  /// material closing stock becomes the next opening stock.
  DailyReport carryForward({required int reportNo, required DateTime date}) {
    return DailyReport(
      projectId: projectId,
      reportNo: reportNo,
      date: date,
      shift: shift,
      manpower: manpower.map((e) => ManpowerEntry.fromJson(e.toJson())).toList(),
      machinery: machinery
          .map((e) => MachineryEntry.fromJson(e.toJson())
            ..workingHrs = 0
            ..idleHrs = 0
            ..breakdownHrs = 0
            ..remarks = '')
          .toList(),
      activities: activities
          .map((a) => WorkActivity(
                boqItemNo: a.boqItemNo,
                description: a.description,
                location: a.location,
                unit: a.unit,
                totalScope: a.totalScope,
                previousQty: a.cumulativeQty,
              ))
          .toList(),
      materials: materials
          .map((mt) => MaterialEntry(
                name: mt.name,
                specification: mt.specification,
                unit: mt.unit,
                opening: mt.closing,
              ))
          .toList(),
      tomorrowPlan: [],
      preparedBy: SignOff(name: preparedBy.name, designation: preparedBy.designation),
      checkedBy: SignOff(name: checkedBy.name, designation: checkedBy.designation),
      approvedBy: SignOff(name: approvedBy.name, designation: approvedBy.designation),
    );
  }
}
