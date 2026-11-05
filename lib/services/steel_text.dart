import '../models/models.dart';
import '../models/steel_takeoff.dart';
import '../widgets/common.dart';

const steelDisclaimer =
    'Nominal weights from IS 808 / SP 6(1) for rolled sections and BS 4-1 for UB/UC. Plates, flats, '
    'bars, pipes and hollow sections are worked out at 7850 kg/m³. Actual mill weight can vary within '
    'the IS 1852 rolling tolerance; check the supplier\'s chart before billing.';

String _kgm(double v) => v > 0 && v < 1 ? v.toStringAsFixed(3) : fmtNum(v);
String _mt(double kg) => (kg / 1000).toStringAsFixed(3);

/// Plain-text steel take-offs with WhatsApp bold markers.
abstract final class SteelText {
  static String _line(int no, SteelTakeoffItem l) {
    final mark = l.memberMark.isEmpty ? '' : '${l.memberMark}: ';
    final loc = l.location.isEmpty ? '' : ' [${l.location}]';
    return '$no. $mark${l.designation} – ${_kgm(l.kgPerM)} kg/m × ${fmtNum(l.lengthM)} m × '
        '${l.nos} nos = ${fmtNum(l.kg)} kg$loc';
  }

  /// A quick list from the calculator.
  static String items(List<SteelTakeoffItem> lines) {
    final b = StringBuffer('*Steel weight take-off*\n');
    for (final (i, l) in lines.indexed) {
      b.writeln(_line(i + 1, l));
    }
    final kg = lines.fold(0.0, (s, l) => s + l.kg);
    b.writeln('*Total: ${fmtNum(kg)} kg (${_mt(kg)} MT)*');
    b.write('_Nominal IS weights, steel @ 7850 kg/m³_');
    return b.toString();
  }

  /// A saved take-off, grouped family-wise with sub-totals.
  static String takeoff(SteelTakeoff t, Project? p) {
    final b = StringBuffer('*Steel take-off: ${t.title.isEmpty ? 'Untitled' : t.title}*\n');
    if (p != null) b.writeln('Project: ${p.name}');
    b.writeln('Date: ${dateFmt.format(t.date)} · Status: ${t.status.label}');
    if (t.drawingNo.isNotEmpty) b.writeln('Drawing: ${t.drawingNo}');
    var no = 0;
    for (final g in t.groups) {
      b.writeln('\n*${g.title}*');
      for (final l in g.items) {
        b.writeln(_line(++no, l));
      }
      b.writeln('Sub-total: ${g.nos} nos, ${fmtNum(g.lengthM)} m, ${fmtNum(g.kg)} kg (${_mt(g.kg)} MT)');
    }
    b.writeln('\n*Net weight: ${fmtNum(t.netKg)} kg (${_mt(t.netKg)} MT)*');
    if (t.wastagePercent > 0) {
      b.writeln('Wastage ${fmtNum(t.wastagePercent)}%: ${fmtNum(t.wastageKg)} kg');
      b.writeln('*Gross weight: ${fmtNum(t.grossKg)} kg (${_mt(t.grossKg)} MT)*');
    }
    b.write('_Nominal IS weights, steel @ 7850 kg/m³_');
    return b.toString();
  }
}
