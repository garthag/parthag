import 'dart:convert';
import 'dart:typed_data';

import 'package:intl/intl.dart';
import 'package:pdf/pdf.dart';
import 'package:pdf/widgets.dart' as pw;

import '../models/drawing.dart';
import '../models/models.dart';

/// Printable drawing register (A4 landscape PDF) and a CSV for Excel.
class DrawingRegisterExport {
  static final _date = DateFormat('dd-MM-yyyy');
  static final _time = DateFormat('dd-MM-yyyy HH:mm');

  static const _accent = PdfColor.fromInt(0xFFE65100);
  static const _plantBg = PdfColor.fromInt(0xFFFFCC80);
  static const _unitBg = PdfColor.fromInt(0xFFFFE0B2);
  static const _typeBg = PdfColor.fromInt(0xFFFFF3E0);

  /// The PDF fonts are Latin-1, so swap the few typographic characters we use.
  static String _t(String s) =>
      s.replaceAll('–', '-').replaceAll('—', '-').replaceAll('→', '>').replaceAll('·', '|');

  static String plantLabel(String plant) => plant.isEmpty ? 'Plant not specified' : plant;
  static String unitLabel(String unit) => unit.isEmpty ? 'General / All units' : unit;

  static Future<Uint8List> pdf(Project project, List<Drawing> drawings, {bool includeSuperseded = true}) async {
    final groups = organiseDrawings(drawings);
    final doc = pw.Document(title: 'Drawing Register - ${project.name}', creator: 'Site DPR');
    const cell = pw.TextStyle(fontSize: 7.5);
    final bold = pw.TextStyle(fontSize: 7.5, fontWeight: pw.FontWeight.bold);
    final current = groups.fold(0, (s, g) => s + g.count);

    pw.Widget band(String text, PdfColor bg, {double size = 9}) => pw.Container(
          width: double.infinity,
          color: bg,
          padding: const pw.EdgeInsets.symmetric(horizontal: 6, vertical: 3),
          margin: const pw.EdgeInsets.only(top: 4),
          child: pw.Text(_t(text), style: pw.TextStyle(fontSize: size, fontWeight: pw.FontWeight.bold)),
        );

    const widths = {
      0: pw.FixedColumnWidth(26),
      1: pw.FlexColumnWidth(2.4),
      2: pw.FlexColumnWidth(3.6),
      3: pw.FlexColumnWidth(1.6),
      4: pw.FixedColumnWidth(30),
      5: pw.FixedColumnWidth(34),
      6: pw.FlexColumnWidth(1.6),
      7: pw.FixedColumnWidth(52),
      8: pw.FlexColumnWidth(2),
    };
    const headers = ['S.No', 'Drawing No.', 'Title', 'Sheet kind', 'Rev', 'Status', 'Earlier revs', 'Received', 'File'];

    final content = <pw.Widget>[];
    var sn = 0;
    for (final g in groups) {
      content.add(band('PLANT: ${plantLabel(g.plant)}   (${g.count} drawings)', _plantBg, size: 10));
      for (final u in g.units) {
        content.add(band('UNIT / AREA: ${unitLabel(u.unit)}   (${u.count})', _unitBg));
        for (final (type, entries) in u.types) {
          content.add(band('${type.code} - ${type.name}   (${entries.length})', _typeBg, size: 8));
          content.add(pw.Table(
            columnWidths: widths,
            border: const pw.TableBorder(
              horizontalInside: pw.BorderSide(color: PdfColors.grey300, width: .5),
              bottom: pw.BorderSide(color: PdfColors.grey400, width: .5),
            ),
            children: [
              pw.TableRow(
                decoration: const pw.BoxDecoration(color: PdfColors.grey100),
                children: [for (final h in headers) _c(h, bold)],
              ),
              for (final e in entries) ...[
                pw.TableRow(children: [
                  _c('${++sn}', cell),
                  _c(e.current.number, bold),
                  _c(e.current.displayTitle, cell),
                  _c(e.current.kind, cell),
                  _c(e.current.revision, bold),
                  _c(e.current.status.code, cell),
                  _c(e.superseded.map((d) => d.revision).join(', '), cell),
                  _c(_date.format(e.current.importedAt), cell),
                  _c(e.current.fileName, const pw.TextStyle(fontSize: 6.5, color: PdfColors.grey700)),
                ]),
                if (includeSuperseded)
                  for (final s in e.superseded)
                    pw.TableRow(
                      decoration: const pw.BoxDecoration(color: PdfColors.grey50),
                      children: [
                        _c('', cell),
                        _c('   superseded', const pw.TextStyle(fontSize: 6.5, color: PdfColors.grey600)),
                        _c(s.displayTitle, const pw.TextStyle(fontSize: 6.5, color: PdfColors.grey600)),
                        _c('', cell),
                        _c(s.revision, const pw.TextStyle(fontSize: 6.5, color: PdfColors.grey600)),
                        _c(s.status.code, const pw.TextStyle(fontSize: 6.5, color: PdfColors.grey600)),
                        _c('', cell),
                        _c(_date.format(s.importedAt), const pw.TextStyle(fontSize: 6.5, color: PdfColors.grey600)),
                        _c(s.fileName, const pw.TextStyle(fontSize: 6.5, color: PdfColors.grey600)),
                      ],
                    ),
              ],
            ],
          ));
        }
      }
    }

    // Summary: drawings per plant/unit x type.
    final summary = <pw.TableRow>[
      pw.TableRow(
        decoration: const pw.BoxDecoration(color: PdfColors.grey200),
        children: [
          _c('Plant / Unit', bold),
          for (final t in DrawingType.all) _c(t.code, bold, center: true),
          _c('Total', bold, center: true),
        ],
      ),
      for (final g in groups)
        for (final u in g.units)
          pw.TableRow(children: [
            _c('${plantLabel(g.plant)} > ${unitLabel(u.unit)}', cell),
            for (final t in DrawingType.all)
              _c(
                  '${u.types.where((x) => x.$1 == t).fold(0, (s, x) => s + x.$2.length)}'
                      .replaceAll(RegExp(r'^0$'), '-'),
                  cell,
                  center: true),
            _c('${u.count}', bold, center: true),
          ]),
    ];

    doc.addPage(pw.MultiPage(
      pageFormat: PdfPageFormat.a4.landscape,
      margin: const pw.EdgeInsets.all(24),
      theme: pw.ThemeData.withFont(base: pw.Font.helvetica(), bold: pw.Font.helveticaBold()),
      header: (ctx) => pw.Container(
        padding: const pw.EdgeInsets.only(bottom: 6),
        margin: const pw.EdgeInsets.only(bottom: 6),
        decoration: const pw.BoxDecoration(border: pw.Border(bottom: pw.BorderSide(color: _accent, width: 2))),
        child: pw.Row(
          crossAxisAlignment: pw.CrossAxisAlignment.end,
          children: [
            pw.Expanded(
              child: pw.Column(crossAxisAlignment: pw.CrossAxisAlignment.start, children: [
                pw.Text('DRAWING REGISTER',
                    style: pw.TextStyle(fontSize: 14, fontWeight: pw.FontWeight.bold, color: _accent)),
                pw.Text(_t(project.name), style: pw.TextStyle(fontSize: 10, fontWeight: pw.FontWeight.bold)),
                pw.Text(
                    _t([
                      if (project.code.isNotEmpty) 'Code: ${project.code}',
                      if (project.client.isNotEmpty) 'Client: ${project.client}',
                      if (project.consultant.isNotEmpty) 'PMC: ${project.consultant}',
                      if (project.contractor.isNotEmpty) 'Contractor: ${project.contractor}',
                    ].join('   ')),
                    style: const pw.TextStyle(fontSize: 7.5)),
              ]),
            ),
            pw.Text('$current drawings | ${drawings.length} files\nOrder: Plant > Unit > Type > Drawing No.',
                textAlign: pw.TextAlign.right, style: const pw.TextStyle(fontSize: 7.5)),
          ],
        ),
      ),
      footer: (ctx) => pw.Row(
        mainAxisAlignment: pw.MainAxisAlignment.spaceBetween,
        children: [
          pw.Text(
              'Generated ${_time.format(DateTime.now())} | Status: IFR review, IFA approval, IFC construction, AB as-built, FI information',
              style: const pw.TextStyle(fontSize: 6.5, color: PdfColors.grey700)),
          pw.Text('Page ${ctx.pageNumber} of ${ctx.pagesCount}',
              style: const pw.TextStyle(fontSize: 7, color: PdfColors.grey700)),
        ],
      ),
      build: (ctx) => [
        pw.Text('Summary by unit and drawing type', style: pw.TextStyle(fontSize: 9, fontWeight: pw.FontWeight.bold)),
        pw.SizedBox(height: 4),
        pw.Table(
          border: pw.TableBorder.all(color: PdfColors.grey400, width: .5),
          columnWidths: {0: const pw.FlexColumnWidth(4)},
          children: summary,
        ),
        pw.SizedBox(height: 4),
        pw.Text(
            _t(DrawingType.all.map((t) => '${t.code} = ${t.name}').join('   ')),
            style: const pw.TextStyle(fontSize: 6.5, color: PdfColors.grey700)),
        pw.SizedBox(height: 8),
        ...content,
      ],
    ));
    return doc.save();
  }

  static pw.Widget _c(String s, pw.TextStyle style, {bool center = false}) => pw.Padding(
        padding: const pw.EdgeInsets.symmetric(horizontal: 3, vertical: 2.5),
        child: pw.Text(_t(s), style: style, textAlign: center ? pw.TextAlign.center : pw.TextAlign.left),
      );

  /// CSV of every file in the register (current and superseded), in register order.
  static String csv(List<Drawing> drawings) {
    String q(String v) => '"${v.replaceAll('"', '""')}"';
    final rows = <String>[
      'S.No,Plant,Unit/Area,Type code,Type,Drawing No.,Title,Sheet kind,Revision,Current,Status,Received,File name,ZIP folder,Remarks',
    ];
    var sn = 0;
    for (final g in organiseDrawings(drawings)) {
      for (final u in g.units) {
        for (final (type, entries) in u.types) {
          for (final e in entries) {
            for (final d in e.revisions) {
              rows.add([
                '${++sn}',
                q(plantLabel(g.plant)),
                q(unitLabel(u.unit)),
                type.code,
                q(type.name),
                q(d.number),
                q(d.displayTitle),
                q(d.kind),
                q(d.revision),
                d == e.current ? 'Yes' : 'Superseded',
                q(d.status.label),
                _date.format(d.importedAt),
                q(d.fileName),
                q(d.sourcePath),
                q(d.remarks),
              ].join(','));
            }
          }
        }
      }
    }
    // BOM so Excel opens it as UTF-8.
    return '﻿${rows.join('\r\n')}\r\n';
  }

  static Uint8List csvBytes(List<Drawing> drawings) => Uint8List.fromList(utf8.encode(csv(drawings)));
}
