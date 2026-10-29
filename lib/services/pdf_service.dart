import 'dart:io';
import 'dart:typed_data';

import 'package:intl/intl.dart';
import 'package:pdf/pdf.dart';
import 'package:pdf/widgets.dart' as pw;

import '../models/models.dart';

/// Builds the DPR as an A4 PDF in the layout commonly used on Indian
/// projects (CPWD / PWD style): header block, weather, manpower, machinery,
/// work done, materials, quality, safety, hindrances, photos and sign-off.
class PdfService {
  static final _date = DateFormat('dd-MM-yyyy');
  static final _day = DateFormat('EEEE');
  static final _time = DateFormat('dd-MM-yyyy HH:mm');
  static final _num = NumberFormat('#,##,##0.##', 'en_IN');

  static const _accent = PdfColor.fromInt(0xFFE65100);
  static const _headerBg = PdfColor.fromInt(0xFFFFE0B2);

  static String n(num v) => _num.format(v);

  static Future<Uint8List> build(Project project, DailyReport r, {int maxPhotos = 24}) async {
    final doc = pw.Document(
      title: 'DPR ${r.reportNo} - ${project.name}',
      author: r.preparedBy.name,
      creator: 'Site DPR',
    );

    final photos = <(MediaItem, pw.ImageProvider)>[];
    for (final m in r.media.where((m) => m.type == MediaType.photo)) {
      if (photos.length >= maxPhotos) break;
      final img = await _image(m.localPath);
      if (img != null) photos.add((m, img));
    }
    final sigs = {
      for (final s in [r.preparedBy, r.checkedBy, r.approvedBy])
        s: await _image(s.signaturePath),
    };
    final activityName = {for (final a in r.activities) a.id: a.description};

    doc.addPage(
      pw.MultiPage(
        pageFormat: PdfPageFormat.a4,
        margin: const pw.EdgeInsets.all(28),
        theme: pw.ThemeData.withFont(base: pw.Font.helvetica(), bold: pw.Font.helveticaBold()),
        header: (ctx) => _pageHeader(project, r),
        footer: (ctx) => pw.Row(
          mainAxisAlignment: pw.MainAxisAlignment.spaceBetween,
          children: [
            pw.Text('Generated ${_time.format(DateTime.now())} | Status: ${r.status.label}',
                style: const pw.TextStyle(fontSize: 7, color: PdfColors.grey700)),
            pw.Text('Page ${ctx.pageNumber} of ${ctx.pagesCount}',
                style: const pw.TextStyle(fontSize: 7, color: PdfColors.grey700)),
          ],
        ),
        build: (ctx) => [
          _infoGrid([
            ['Project', project.name, 'Report No.', 'DPR-${r.reportNo.toString().padLeft(3, '0')}'],
            ['Client', project.client, 'Date', '${_date.format(r.date)} (${_day.format(r.date)})'],
            ['Contractor', project.contractor, 'Work Order No.', project.workOrderNo],
            ['PMC / Consultant', project.consultant, 'Shift', r.shift],
            ['Location', project.location, 'Day of contract', _contractDay(project, r)],
          ]),
          _section('1. Weather Conditions'),
          _table(
            ['Morning', 'Afternoon', 'Evening', 'Temp (min/max) C', 'Rainfall (mm)', 'Hours lost'],
            [
              [
                r.weather.morning,
                r.weather.afternoon,
                r.weather.evening,
                '${n(r.weather.minTempC)} / ${n(r.weather.maxTempC)}',
                n(r.weather.rainfallMm),
                n(r.weather.hoursLost),
              ]
            ],
          ),
          _section('2. Manpower Deployed  (Total: ${r.totalManpower}, Man-hours: ${n(r.totalManHours)})'),
          _table(
            ['Agency', 'Category', 'Skill', 'Male', 'Female', 'Total', 'Hrs'],
            [
              for (final m in r.manpower)
                [m.agency, m.category, m.skill, '${m.male}', '${m.female}', '${m.total}', n(m.hours)],
            ],
            empty: 'No manpower recorded',
          ),
          _section('3. Plant & Machinery'),
          _table(
            ['Equipment', 'Ownership', 'Nos', 'Working', 'Idle', 'Breakdown', 'Remarks'],
            [
              for (final m in r.machinery)
                [m.name, m.ownership, '${m.count}', n(m.workingHrs), n(m.idleHrs), n(m.breakdownHrs), m.remarks],
            ],
            empty: 'No machinery recorded',
          ),
          _section('4. Work Progress (Measurement as per IS 1200)'),
          _table(
            ['BOQ', 'Description', 'Location', 'Unit', 'Scope', 'Prev.', 'Today', 'Cum.', '%'],
            [
              for (final a in r.activities)
                [
                  a.boqItemNo,
                  a.description,
                  a.location,
                  a.unit,
                  n(a.totalScope),
                  n(a.previousQty),
                  n(a.todayQty),
                  n(a.cumulativeQty),
                  a.totalScope > 0 ? '${a.percentComplete.toStringAsFixed(1)}%' : '-',
                ],
            ],
            empty: 'No work recorded',
            flex: const [1, 4, 2, 1, 1, 1, 1, 1, 1],
          ),
          _section('5. Material Status'),
          _table(
            ['Material', 'Spec.', 'Unit', 'Opening', 'Received', 'Consumed', 'Closing', 'Supplier / Challan'],
            [
              for (final m in r.materials)
                [
                  m.name,
                  m.specification,
                  m.unit,
                  n(m.opening),
                  n(m.received),
                  n(m.consumed),
                  n(m.closing),
                  [m.supplier, m.challanNo].where((s) => s.isNotEmpty).join(' / '),
                ],
            ],
            empty: 'No material movement',
            flex: const [3, 2, 1, 1, 1, 1, 1, 2],
          ),
          _section('6. Quality Control'),
          _table(
            ['Test / Activity', 'Location', 'Grade', 'Qty', 'Samples', 'Result', 'Standard'],
            [
              for (final q in r.quality)
                [q.testType, q.location, q.grade, n(q.quantity), '${q.samples}', q.result, q.standard],
            ],
            empty: 'No QC activity',
          ),
          _section('7. Safety (IS 7969 / BOCW Act)'),
          _infoGrid([
            [
              'Toolbox talk',
              r.safety.toolboxTalk
                  ? 'Yes - ${r.safety.toolboxTopic} (${r.safety.toolboxAttendees} attended)'
                  : 'No',
              'PPE compliance',
              '${n(r.safety.ppeCompliancePct)} %',
            ],
            [
              'Permits issued',
              r.safety.permits.isEmpty ? '-' : r.safety.permits.join(', '),
              'Safe day',
              r.safety.isSafeDay ? 'Yes' : 'No',
            ],
            ['PPE checked', r.safety.ppeChecked.isEmpty ? '-' : r.safety.ppeChecked.join(', '), '', ''],
          ]),
          if (r.safety.incidents.isNotEmpty)
            _table(
              ['Incident type', 'Description', 'Action taken'],
              [for (final i in r.safety.incidents) [i.type, i.description, i.actionTaken]],
            ),
          if (r.safety.observations.isNotEmpty) _para('Observations: ${r.safety.observations}'),
          _section('8. Hindrances / Delays  (Hours lost: ${n(r.hindranceHours)})'),
          _table(
            ['Category', 'Description', 'Hrs lost', 'Responsibility', 'Action required'],
            [
              for (final h in r.hindrances)
                [h.category, h.description, n(h.hoursLost), h.responsibility, h.actionRequired],
            ],
            empty: 'No hindrance',
          ),
          _section('9. Visitors'),
          _table(
            ['Name', 'Organisation', 'Purpose'],
            [for (final v in r.visitors) [v.name, v.organisation, v.purpose]],
            empty: 'No visitors',
          ),
          _section('10. Instructions, Remarks & Plan for Tomorrow'),
          if (r.instructions.isEmpty && r.remarks.isEmpty && r.tomorrowPlan.isEmpty) _para('Nil'),
          if (r.instructions.isNotEmpty) _para('Instructions received: ${r.instructions}'),
          if (r.remarks.isNotEmpty) _para('Remarks: ${r.remarks}'),
          if (r.tomorrowPlan.isNotEmpty)
            pw.Column(
              crossAxisAlignment: pw.CrossAxisAlignment.start,
              children: [
                _para('Plan for tomorrow:'),
                for (final (i, t) in r.tomorrowPlan.indexed) _para('  ${i + 1}. $t'),
              ],
            ),
          if (photos.isNotEmpty) ...[
            _section('11. Site Photographs'),
            pw.Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final (m, img) in photos)
                  pw.Container(
                    width: 166,
                    decoration: pw.BoxDecoration(border: pw.Border.all(color: PdfColors.grey400, width: .5)),
                    child: pw.Column(
                      crossAxisAlignment: pw.CrossAxisAlignment.start,
                      children: [
                        pw.Image(img, width: 166, height: 120, fit: pw.BoxFit.cover),
                        pw.Padding(
                          padding: const pw.EdgeInsets.all(3),
                          child: pw.Column(
                            crossAxisAlignment: pw.CrossAxisAlignment.start,
                            children: [
                              pw.Text(m.caption.isEmpty ? m.category : m.caption,
                                  style: pw.TextStyle(fontSize: 7, fontWeight: pw.FontWeight.bold)),
                              pw.Text(
                                [
                                  m.category,
                                  ...m.tags,
                                  if (activityName[m.activityId] != null) activityName[m.activityId]!,
                                ].join(' | '),
                                style: const pw.TextStyle(fontSize: 6, color: PdfColors.grey800),
                              ),
                              pw.Text(
                                [
                                  _time.format(m.capturedAt),
                                  if (m.location.isNotEmpty) m.location,
                                  if (m.hasGeoTag)
                                    '${m.latitude!.toStringAsFixed(5)}, ${m.longitude!.toStringAsFixed(5)}',
                                ].join(' | '),
                                style: const pw.TextStyle(fontSize: 6, color: PdfColors.grey700),
                              ),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
              ],
            ),
          ],
          if (r.videoCount > 0) ...[
            _section('Video Records'),
            _table(
              ['Caption', 'Category / Tags', 'Captured', 'Link'],
              [
                for (final m in r.media.where((m) => m.type == MediaType.video))
                  [
                    m.caption,
                    [m.category, ...m.tags].join(', '),
                    _time.format(m.capturedAt),
                    m.remoteUrl.isEmpty ? 'Stored on device' : m.remoteUrl,
                  ],
              ],
            ),
          ],
          pw.SizedBox(height: 18),
          pw.Row(
            crossAxisAlignment: pw.CrossAxisAlignment.end,
            children: [
              for (final (label, s) in [
                ('Prepared by', r.preparedBy),
                ('Checked by', r.checkedBy),
                ('Approved by', r.approvedBy),
              ])
                pw.Expanded(
                  child: pw.Padding(
                    padding: const pw.EdgeInsets.symmetric(horizontal: 6),
                    child: pw.Column(
                      crossAxisAlignment: pw.CrossAxisAlignment.start,
                      children: [
                        pw.SizedBox(
                          height: 44,
                          child: sigs[s] == null ? pw.SizedBox() : pw.Image(sigs[s]!, height: 44),
                        ),
                        pw.Divider(thickness: .5),
                        pw.Text(label, style: pw.TextStyle(fontSize: 8, fontWeight: pw.FontWeight.bold)),
                        pw.Text(s.name, style: const pw.TextStyle(fontSize: 8)),
                        pw.Text(s.designation, style: const pw.TextStyle(fontSize: 7, color: PdfColors.grey700)),
                        if (s.signedAt != null)
                          pw.Text(_time.format(s.signedAt!),
                              style: const pw.TextStyle(fontSize: 6, color: PdfColors.grey700)),
                      ],
                    ),
                  ),
                ),
            ],
          ),
        ],
      ),
    );
    return doc.save();
  }

  static String _contractDay(Project p, DailyReport r) {
    if (p.startDate == null) return '-';
    final day = DateTime(r.date.year, r.date.month, r.date.day)
            .difference(DateTime(p.startDate!.year, p.startDate!.month, p.startDate!.day))
            .inDays +
        1;
    if (p.completionDate == null) return 'Day $day';
    final total = p.completionDate!.difference(p.startDate!).inDays + 1;
    return 'Day $day of $total';
  }

  static Future<pw.ImageProvider?> _image(String path) async {
    if (path.isEmpty) return null;
    try {
      final f = File(path);
      if (!await f.exists()) return null;
      return pw.MemoryImage(await f.readAsBytes());
    } catch (_) {
      return null;
    }
  }

  static pw.Widget _pageHeader(Project p, DailyReport r) => pw.Container(
        margin: const pw.EdgeInsets.only(bottom: 8),
        padding: const pw.EdgeInsets.only(bottom: 4),
        decoration: const pw.BoxDecoration(border: pw.Border(bottom: pw.BorderSide(color: _accent, width: 2))),
        child: pw.Row(
          mainAxisAlignment: pw.MainAxisAlignment.spaceBetween,
          children: [
            pw.Column(crossAxisAlignment: pw.CrossAxisAlignment.start, children: [
              pw.Text('DAILY PROGRESS REPORT',
                  style: pw.TextStyle(fontSize: 14, fontWeight: pw.FontWeight.bold, color: _accent)),
              pw.Text(p.name, style: const pw.TextStyle(fontSize: 9)),
            ]),
            pw.Column(crossAxisAlignment: pw.CrossAxisAlignment.end, children: [
              pw.Text('DPR-${r.reportNo.toString().padLeft(3, '0')}',
                  style: pw.TextStyle(fontSize: 11, fontWeight: pw.FontWeight.bold)),
              pw.Text(_date.format(r.date), style: const pw.TextStyle(fontSize: 9)),
            ]),
          ],
        ),
      );

  static pw.Widget _section(String title) => pw.Container(
        width: double.infinity,
        margin: const pw.EdgeInsets.only(top: 10, bottom: 4),
        padding: const pw.EdgeInsets.symmetric(horizontal: 4, vertical: 3),
        color: _headerBg,
        child: pw.Text(title, style: pw.TextStyle(fontSize: 9, fontWeight: pw.FontWeight.bold)),
      );

  static pw.Widget _para(String text) =>
      pw.Padding(padding: const pw.EdgeInsets.only(bottom: 2), child: pw.Text(text, style: const pw.TextStyle(fontSize: 8)));

  static pw.Widget _infoGrid(List<List<String>> rows) => pw.Table(
        border: pw.TableBorder.all(color: PdfColors.grey500, width: .5),
        columnWidths: const {
          0: pw.FlexColumnWidth(1.3),
          1: pw.FlexColumnWidth(3),
          2: pw.FlexColumnWidth(1.3),
          3: pw.FlexColumnWidth(2),
        },
        children: [
          for (final row in rows)
            pw.TableRow(children: [
              for (final (i, c) in row.indexed)
                pw.Container(
                  color: i.isEven ? PdfColors.grey200 : null,
                  padding: const pw.EdgeInsets.all(3),
                  child: pw.Text(c,
                      style: pw.TextStyle(fontSize: 8, fontWeight: i.isEven ? pw.FontWeight.bold : null)),
                ),
            ]),
        ],
      );

  static pw.Widget _table(List<String> headers, List<List<String>> rows,
      {String? empty, List<int>? flex}) {
    if (rows.isEmpty && empty != null) return _para(empty);
    return pw.TableHelper.fromTextArray(
      headers: headers,
      data: rows,
      border: pw.TableBorder.all(color: PdfColors.grey500, width: .5),
      headerStyle: pw.TextStyle(fontSize: 7.5, fontWeight: pw.FontWeight.bold),
      headerDecoration: const pw.BoxDecoration(color: PdfColors.grey200),
      cellStyle: const pw.TextStyle(fontSize: 7.5),
      cellPadding: const pw.EdgeInsets.all(3),
      cellAlignment: pw.Alignment.centerLeft,
      columnWidths: flex == null
          ? null
          : {for (final (i, f) in flex.indexed) i: pw.FlexColumnWidth(f.toDouble())},
    );
  }
}
