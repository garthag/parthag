import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:pdf/pdf.dart';
import 'package:pdf/widgets.dart' as pw;

import '../models/models.dart';
import '../services/drawing_service.dart';
import '../state/app_state.dart';

/// (path inside the ZIP, title) of the sample drawing package. It mixes the
/// usual ways drawings arrive: unit folders, discipline folders, codes in the
/// drawing number and several revisions of the same drawing.
const _sampleSheets = [
  ('SR-GN-PP-001_R0 Site Plot Plan.pdf', 'Site Plot Plan'),
  ('Block-A/SR-A-ST-FDN-001_R0 Foundation Layout.pdf', 'Foundation Layout'),
  ('Block-A/SR-A-ST-FDN-001_R1 Foundation Layout.pdf', 'Foundation Layout'),
  ('Block-A/SR-A-ST-COL-002_R2 Column Schedule.pdf', 'Column Schedule'),
  ('Block-A/SR-A-ST-SLB-010_RevA 8th Floor Slab (IFA).pdf', '8th Floor Slab Reinforcement'),
  ('Block-A/SR-A-AR-PLN-101_R0 Typical Floor Plan.pdf', 'Typical Floor Plan'),
  ('Block-A/SR-A-AR-SEC-201_R0 Section A-A.pdf', 'Section A-A'),
  ('Block-A/Electrical/SR-A-SLD-001_R1 Main LT Panel SLD.pdf', 'Main LT Panel SLD'),
  ('Block-A/SR-A-PL-WS-001_R0 Water Supply Riser.pdf', 'Water Supply Riser'),
  ('Block-A/SR-A-FP-HYD-001_R0 Hydrant Layout.pdf', 'Hydrant Layout'),
  ('Block-B/SR-B-ST-FDN-001_R0 Foundation Layout.pdf', 'Foundation Layout'),
  ('Block-B/SR-B-AR-PLN-101_RevB Typical Floor Plan.pdf', 'Typical Floor Plan'),
  ('External Works/SR-EX-CV-DRN-001_R0 Storm Water Drainage.pdf', 'Storm Water Drainage'),
];

Future<Uint8List> _sheet(String number, String rev, String title, String projectName) async {
  final doc = pw.Document();
  doc.addPage(pw.Page(
    pageFormat: PdfPageFormat.a3.landscape,
    margin: const pw.EdgeInsets.all(20),
    build: (ctx) => pw.Container(
      decoration: pw.BoxDecoration(border: pw.Border.all(width: 2)),
      child: pw.Stack(children: [
        // Simple column grid so the viewer has something to zoom into.
        pw.Positioned.fill(
          child: pw.Padding(
            padding: const pw.EdgeInsets.fromLTRB(60, 50, 60, 140),
            child: pw.GridView(
              crossAxisCount: 6,
              childAspectRatio: 1.4,
              children: [
                for (var i = 0; i < 24; i++)
                  pw.Container(
                    margin: const pw.EdgeInsets.all(14),
                    decoration: pw.BoxDecoration(border: pw.Border.all(color: PdfColors.blueGrey700, width: .8)),
                    child: pw.Center(
                      child: pw.Text('C${i + 1}',
                          style: const pw.TextStyle(fontSize: 8, color: PdfColors.blueGrey700)),
                    ),
                  ),
              ],
            ),
          ),
        ),
        pw.Positioned(
          right: 0,
          bottom: 0,
          child: pw.Container(
            width: 360,
            decoration: pw.BoxDecoration(border: pw.Border.all(width: 1.2)),
            padding: const pw.EdgeInsets.all(8),
            child: pw.Column(crossAxisAlignment: pw.CrossAxisAlignment.start, children: [
              pw.Text(projectName, style: pw.TextStyle(fontSize: 10, fontWeight: pw.FontWeight.bold)),
              pw.SizedBox(height: 4),
              pw.Text(title.toUpperCase(), style: pw.TextStyle(fontSize: 14, fontWeight: pw.FontWeight.bold)),
              pw.Divider(),
              pw.Row(mainAxisAlignment: pw.MainAxisAlignment.spaceBetween, children: [
                pw.Text('DRG. NO.: $number', style: const pw.TextStyle(fontSize: 9)),
                pw.Text('REV: $rev', style: pw.TextStyle(fontSize: 9, fontWeight: pw.FontWeight.bold)),
              ]),
              pw.Text('SAMPLE DRAWING - NOT FOR CONSTRUCTION', style: const pw.TextStyle(fontSize: 7)),
            ]),
          ),
        ),
      ]),
    ),
  ));
  return doc.save();
}

/// Builds a sample drawing ZIP and files it into [project]'s register.
Future<void> createSampleDrawings(AppState state, Project project) async {
  final archive = Archive();
  for (final (path, title) in _sampleSheets) {
    final name = path.split('/').last;
    final number = name.split('_').first;
    final rev = RegExp(r'_R(?:ev)?([0-9A-Z]+)').firstMatch(name)?.group(1) ?? '0';
    final bytes = await _sheet(number, rev, title, project.name);
    archive.add(ArchiveFile.bytes('Sunrise_Drawings/$path', bytes));
  }
  final zip = Uint8List.fromList(ZipEncoder().encodeBytes(archive));
  final result = await DrawingService(state.store).importZipBytes(
    zip,
    projectId: project.id,
    defaultPlant: 'Sunrise Residency',
    existing: state.drawingsFor(project.id),
  );
  await state.addDrawings(result.added);
}
