import 'package:intl/intl.dart';

import '../models/models.dart';

/// Plain-text DPR summary formatted for WhatsApp / SMS / e-mail, which is
/// how most Indian sites circulate the daily report to the team.
abstract final class ReportText {
  static final _date = DateFormat('dd-MM-yyyy (EEEE)');
  static final _num = NumberFormat('#,##,##0.##', 'en_IN');

  static String summary(Project p, DailyReport r) {
    String n(num v) => _num.format(v);
    final b = StringBuffer()
      ..writeln('*DAILY PROGRESS REPORT*')
      ..writeln('*${p.name}*')
      ..writeln('DPR-${r.reportNo.toString().padLeft(3, '0')} | ${_date.format(r.date)}')
      ..writeln('Weather: ${r.weather.morning} / ${r.weather.afternoon} / ${r.weather.evening}'
          '${r.weather.rainfallMm > 0 ? ' | Rain ${n(r.weather.rainfallMm)} mm' : ''}')
      ..writeln();

    b.writeln('*Manpower: ${r.totalManpower}*');
    for (final m in r.manpower) {
      b.writeln('• ${m.category}: ${m.total}${m.agency.isNotEmpty ? ' (${m.agency})' : ''}');
    }

    final done = r.activities.where((a) => a.todayQty > 0).toList();
    if (done.isNotEmpty) {
      b
        ..writeln()
        ..writeln('*Work done today*');
      for (final a in done) {
        final pct = a.totalScope > 0 ? ' - ${a.percentComplete.toStringAsFixed(1)}%' : '';
        b.writeln('• ${a.description}${a.location.isNotEmpty ? ' @ ${a.location}' : ''}: '
            '${n(a.todayQty)} ${a.unit} (Cum. ${n(a.cumulativeQty)}$pct)');
      }
    }

    if (r.machinery.isNotEmpty) {
      b
        ..writeln()
        ..writeln('*Machinery*');
      for (final m in r.machinery) {
        b.writeln('• ${m.name} x${m.count}: ${n(m.workingHrs)} h'
            '${m.breakdownHrs > 0 ? ' (breakdown ${n(m.breakdownHrs)} h)' : ''}');
      }
    }

    final received = r.materials.where((m) => m.received > 0).toList();
    if (received.isNotEmpty) {
      b
        ..writeln()
        ..writeln('*Material received*');
      for (final m in received) {
        b.writeln('• ${m.name}: ${n(m.received)} ${m.unit}');
      }
    }

    if (r.quality.isNotEmpty) {
      b
        ..writeln()
        ..writeln('*Quality*');
      for (final q in r.quality) {
        b.writeln('• ${q.testType}${q.grade.isNotEmpty ? ' ${q.grade}' : ''}'
            '${q.location.isNotEmpty ? ' @ ${q.location}' : ''}: ${q.result}');
      }
    }

    b
      ..writeln()
      ..writeln('*Safety:* ${r.safety.isSafeDay ? 'Safe day ✅' : 'Incident reported ⚠️'}'
          '${r.safety.toolboxTalk ? ' | TBT: ${r.safety.toolboxTopic}' : ''}'
          ' | PPE ${n(r.safety.ppeCompliancePct)}%');
    for (final i in r.safety.incidents) {
      b.writeln('• ${i.type}: ${i.description}');
    }

    if (r.hindrances.isNotEmpty) {
      b
        ..writeln()
        ..writeln('*Hindrances (${n(r.hindranceHours)} h lost)*');
      for (final h in r.hindrances) {
        b.writeln('• ${h.category}${h.description.isNotEmpty ? ': ${h.description}' : ''} [${h.responsibility}]');
      }
    }

    if (r.tomorrowPlan.isNotEmpty) {
      b
        ..writeln()
        ..writeln('*Plan for tomorrow*');
      for (final (i, t) in r.tomorrowPlan.indexed) {
        b.writeln('${i + 1}. $t');
      }
    }

    if (r.media.isNotEmpty) {
      b
        ..writeln()
        ..writeln('Photos: ${r.photoCount} | Videos: ${r.videoCount}');
    }
    if (r.preparedBy.name.isNotEmpty) {
      b
        ..writeln()
        ..writeln('- ${r.preparedBy.name}, ${r.preparedBy.designation}');
    }
    return b.toString().trimRight();
  }
}
