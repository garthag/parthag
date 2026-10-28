import '../models/models.dart';
import '../state/app_state.dart';

/// Creates a realistic sample project with three days of DPRs so new users
/// can explore the app before entering their own site data.
Future<Project> createSampleProject(AppState state) async {
  final today = DateTime.now();
  final start = DateTime(today.year, today.month, today.day).subtract(const Duration(days: 120));
  final p = Project(
    name: 'Sunrise Residency - Tower A (G+12)',
    code: 'SR-TA',
    client: 'Sunrise Housing Pvt. Ltd.',
    contractor: state.profile.company.isEmpty ? 'ABC Constructions' : state.profile.company,
    consultant: 'XYZ Project Management Consultants',
    workOrderNo: 'WO/SR/2026/014',
    location: 'Plot 21, Sector 5, Kharghar, Navi Mumbai',
    startDate: start,
    completionDate: start.add(const Duration(days: 540)),
    contractValueLakh: 2450,
    siteEngineer: state.profile.name,
  );
  await state.saveProject(p);

  DailyReport? prev;
  for (var d = 2; d >= 0; d--) {
    final date = DateTime(today.year, today.month, today.day).subtract(Duration(days: d));
    final r = prev?.carryForward(reportNo: 3 - d, date: date) ??
        DailyReport(
          projectId: p.id,
          reportNo: 1,
          date: date,
          manpower: [
            ManpowerEntry(agency: 'ABC Constructions', category: 'Site Engineer', skill: 'Highly skilled', male: 2),
            ManpowerEntry(agency: 'ABC Constructions', category: 'Mason', male: 8),
            ManpowerEntry(agency: 'Shree Ganesh Labour', category: 'Carpenter (Shuttering)', male: 12),
            ManpowerEntry(agency: 'Shree Ganesh Labour', category: 'Bar bender', male: 10),
            ManpowerEntry(agency: 'Shree Ganesh Labour', category: 'Beldar / Mazdoor', skill: 'Unskilled', male: 18, female: 6),
          ],
          machinery: [
            MachineryEntry(name: 'Tower crane', ownership: 'Owned'),
            MachineryEntry(name: 'Builder hoist / Passenger lift', ownership: 'Hired'),
            MachineryEntry(name: 'Needle vibrator', ownership: 'Owned', count: 4),
          ],
          activities: [
            WorkActivity(boqItemNo: '5.2', description: 'RCC slab', location: 'Tower A - 7th floor slab', unit: 'cum', totalScope: 1450, previousQty: 812),
            WorkActivity(boqItemNo: '5.8', description: 'Reinforcement cutting, bending & fixing', location: 'Tower A - 8th floor columns', unit: 'MT', totalScope: 410, previousQty: 236),
            WorkActivity(boqItemNo: '6.1', description: 'Block masonry (AAC)', location: 'Tower A - 3rd floor', unit: 'cum', totalScope: 980, previousQty: 145),
          ],
          materials: [
            MaterialEntry(name: 'Cement OPC 53 Grade', specification: 'IS 269:2015', unit: 'bags', opening: 640),
            MaterialEntry(name: 'TMT Steel Fe500D', specification: 'IS 1786:2008', unit: 'MT', opening: 38.5),
            MaterialEntry(name: 'AAC blocks', specification: 'IS 2185 (Part 3)', unit: 'nos', opening: 4200),
          ],
          preparedBy: SignOff(name: state.profile.name, designation: 'Site Engineer'),
        );
    for (final m in r.machinery) {
      m
        ..workingHrs = 7
        ..idleHrs = 1;
    }
    r.activities[0].todayQty = d == 1 ? 96 : 0;
    r.activities[1].todayQty = 4.2 + d;
    r.activities[2].todayQty = 18 + 2.5 * d;
    r.materials[0]
      ..received = d == 2 ? 600 : 0
      ..consumed = d == 1 ? 690 : 85;
    r.materials[1]
      ..received = d == 0 ? 25 : 0
      ..consumed = r.activities[1].todayQty;
    r.materials[2].consumed = 1100;
    r.weather = WeatherInfo(
      morning: 'Cloudy',
      afternoon: d == 0 ? 'Light rain' : 'Sunny',
      evening: 'Cloudy',
      minTempC: 24,
      maxTempC: 32,
      rainfallMm: d == 0 ? 6 : 0,
      hoursLost: d == 0 ? 1 : 0,
    );
    r.safety = SafetyInfo(
      toolboxTalk: true,
      toolboxTopic: d == 1 ? 'Work at height & harness use' : 'Housekeeping',
      toolboxAttendees: 48,
      ppeCompliancePct: 95,
      permits: d == 1 ? ['Work at height', 'Lifting / crane operation'] : ['Work at height'],
      ppeChecked: ['Safety helmet (IS 2925)', 'Safety shoes (IS 15298)', 'Full body harness (IS 3521)'],
      incidents: d == 2 ? [SafetyIncident(type: 'Near miss', description: 'Loose shuttering plank at slab edge', actionTaken: 'Plank secured; edge protection re-checked')] : [],
    );
    if (d == 1) {
      r.quality = [
        QualityEntry(testType: 'Pre-pour checklist', location: '7th floor slab', grade: 'M30', result: 'Pass', standard: 'IS 456 / Project QAP'),
        QualityEntry(testType: 'Slump test', location: '7th floor slab', grade: 'M30', result: 'Pass', standard: 'IS 1199 (Part 2)', remarks: 'Slump 110 mm'),
        QualityEntry(testType: 'Cube casting (7 / 28 day)', location: '7th floor slab', grade: 'M30', quantity: 96, samples: 12, standard: 'IS 516 (Part 1)'),
      ];
    }
    if (d == 0) {
      r.hindrances = [
        Hindrance(category: 'Drawing / GFC awaited', description: 'Revised staircase drawing for 8th floor', hoursLost: 2, responsibility: 'Consultant / PMC', actionRequired: 'PMC to issue GFC by tomorrow'),
      ];
      r.visitors = [Visitor(name: 'Mr. R. Kulkarni', organisation: 'PMC - Sr. Engineer', purpose: 'Slab inspection')];
      r.tomorrowPlan = ['Column casting 8th floor (M30)', 'De-shuttering 6th floor slab', 'AAC masonry 3rd & 4th floor'];
    }
    r.status = d == 0 ? ReportStatus.draft : ReportStatus.approved;
    await state.saveReport(r);
    prev = r;
  }
  return p;
}
