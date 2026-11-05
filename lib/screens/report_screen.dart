import 'dart:async';

import 'package:flutter/material.dart';
import 'package:printing/printing.dart';
import 'package:provider/provider.dart';
import 'package:share_plus/share_plus.dart';

import '../models/models.dart';
import '../models/steel_takeoff.dart';
import '../services/pdf_service.dart';
import '../services/report_text.dart';
import '../state/app_state.dart';
import '../widgets/common.dart';
import '../widgets/report_sections.dart';
import 'steel_takeoff_screen.dart';

class ReportScreen extends StatefulWidget {
  const ReportScreen({super.key, required this.reportId});

  final String reportId;

  @override
  State<ReportScreen> createState() => _ReportScreenState();
}

class _ReportScreenState extends State<ReportScreen> {
  Timer? _debounce;
  late final AppState _state;

  static const _tabs = [
    (Icons.info_outline, 'General'),
    (Icons.engineering_outlined, 'Manpower'),
    (Icons.precision_manufacturing_outlined, 'Machinery'),
    (Icons.construction, 'Work done'),
    (Icons.inventory_2_outlined, 'Materials'),
    (Icons.verified_outlined, 'Quality'),
    (Icons.health_and_safety_outlined, 'Safety'),
    (Icons.report_problem_outlined, 'Hindrance'),
    (Icons.photo_camera_outlined, 'Photos & videos'),
    (Icons.draw_outlined, 'Notes & sign'),
  ];

  @override
  void initState() {
    super.initState();
    _state = context.read<AppState>();
  }

  /// Edits are saved shortly after the user stops typing.
  void _changed() {
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 600), _flush);
  }

  void _flush() {
    _debounce?.cancel();
    _debounce = null;
    final r = _state.report(widget.reportId);
    if (r != null) _state.saveReport(r);
  }

  @override
  void dispose() {
    if (_debounce != null) _flush();
    super.dispose();
  }

  Future<void> _setStatus(DailyReport r, ReportStatus s) async {
    if (s == ReportStatus.approved && !r.preparedBy.isSigned) {
      final go = await confirm(context, 'Not signed yet',
          'The report has not been signed by the preparer. Approve anyway?', action: 'Approve');
      if (!go) return;
    }
    r.status = s;
    _flush();
    setState(() {});
  }

  Future<void> _menu(String v, Project p, DailyReport r) async {
    _flush();
    switch (v) {
      case 'pdf':
        Navigator.push(context, MaterialPageRoute(builder: (_) => PdfPreviewScreen(project: p, report: r)));
      case 'share_pdf':
        final bytes = await PdfService.build(p, r);
        await Printing.sharePdf(bytes: bytes, filename: _fileName(p, r));
      case 'share_text':
        await SharePlus.instance.share(ShareParams(text: ReportText.summary(p, r), subject: 'DPR-${r.reportNo} ${p.name}'));
      case 'steel':
        final takeoffs = _state.takeoffsFor(p.id);
        if (takeoffs.isEmpty) {
          ScaffoldMessenger.of(context).showSnackBar(const SnackBar(
            content: Text('No steel take-offs in this project yet. Add one in the project\'s Steel tab.'),
          ));
          return;
        }
        final picked = await showDialog<String>(
          context: context,
          builder: (c) => SimpleDialog(
            title: const Text('Add steel from take-off'),
            children: [
              for (final t in takeoffs)
                SimpleDialogOption(
                  onPressed: () => Navigator.pop(c, t.id),
                  child: ListTile(
                    title: Text(t.title.isEmpty ? 'Untitled take-off' : t.title),
                    subtitle: Text('${fmtNum(t.netMt)} MT · ${t.status.label}'),
                  ),
                ),
            ],
          ),
        );
        final takeoff = picked == null ? null : _state.takeoff(picked);
        if (takeoff != null && mounted) {
          await postTakeoffDialog(context, takeoff, report: r);
          if (mounted) setState(() {});
        }
      case 'delete':
        if (await confirm(context, 'Delete DPR-${r.reportNo}?', 'All photos and videos in this report will be removed.')) {
          _debounce?.cancel();
          _debounce = null;
          if (mounted) Navigator.pop(context);
          await _state.deleteReport(r.id);
        }
    }
  }

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final r = state.report(widget.reportId);
    final p = r == null ? null : state.project(r.projectId);
    if (r == null || p == null) return const Scaffold(body: SizedBox());

    return DefaultTabController(
      length: _tabs.length,
      child: Scaffold(
        appBar: AppBar(
          title: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('DPR-${r.reportNo.toString().padLeft(3, '0')}'),
              Text(dateLongFmt.format(r.date), style: Theme.of(context).textTheme.bodySmall),
            ],
          ),
          actions: [
            Center(child: StatusChip(r.status)),
            IconButton(
              tooltip: 'PDF preview',
              icon: const Icon(Icons.picture_as_pdf_outlined),
              onPressed: () => _menu('pdf', p, r),
            ),
            PopupMenuButton<String>(
              onSelected: (v) => _menu(v, p, r),
              itemBuilder: (_) => const [
                PopupMenuItem(value: 'pdf', child: ListTile(leading: Icon(Icons.print_outlined), title: Text('Preview / Print PDF'))),
                PopupMenuItem(value: 'share_pdf', child: ListTile(leading: Icon(Icons.share), title: Text('Share PDF'))),
                PopupMenuItem(value: 'share_text', child: ListTile(leading: Icon(Icons.chat_outlined), title: Text('Share summary (WhatsApp)'))),
                PopupMenuItem(value: 'steel', child: ListTile(leading: Icon(Icons.view_column_outlined), title: Text('Add steel from take-off'))),
                PopupMenuItem(value: 'delete', child: ListTile(leading: Icon(Icons.delete_outline), title: Text('Delete DPR'))),
              ],
            ),
          ],
          bottom: TabBar(
            isScrollable: true,
            tabAlignment: TabAlignment.start,
            tabs: [for (final (icon, label) in _tabs) Tab(icon: Icon(icon, size: 20), text: label)],
          ),
        ),
        body: Column(
          children: [
            if (r.isLocked)
              MaterialBanner(
                content: const Text('This DPR is approved and locked.'),
                leading: const Icon(Icons.lock_outline),
                actions: [
                  TextButton(onPressed: () => _setStatus(r, ReportStatus.draft), child: const Text('Unlock')),
                ],
              ),
            if (state.takeoffsPostedTo(r.id).isNotEmpty)
              Material(
                color: Theme.of(context).colorScheme.surfaceContainerHighest,
                child: SingleChildScrollView(
                  scrollDirection: Axis.horizontal,
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                  child: Row(
                    children: [
                      const Icon(Icons.link, size: 18),
                      const SizedBox(width: 6),
                      const Text('Steel take-offs:'),
                      for (final t in state.takeoffsPostedTo(r.id))
                        Padding(
                          padding: const EdgeInsets.only(left: 6),
                          child: ActionChip(
                            label: Text(t.title.isEmpty ? 'Untitled' : t.title),
                            onPressed: () => Navigator.push(
                              context,
                              MaterialPageRoute(builder: (_) => TakeoffScreen(takeoffId: t.id)),
                            ),
                          ),
                        ),
                    ],
                  ),
                ),
              ),
            Expanded(
              child: TabBarView(
                children: [
                  GeneralSection(report: r, project: p, onChanged: _changed),
                  ManpowerSection(report: r, onChanged: _changed),
                  MachinerySection(report: r, onChanged: _changed),
                  WorkSection(report: r, onChanged: _changed),
                  MaterialsSection(report: r, onChanged: _changed),
                  QualitySection(report: r, onChanged: _changed),
                  SafetySection(report: r, onChanged: _changed),
                  HindranceSection(report: r, onChanged: _changed),
                  MediaSection(report: r, onChanged: _changed),
                  SignOffSection(report: r, onChanged: _changed, onStatus: (s) => _setStatus(r, s)),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

String _fileName(Project p, DailyReport r) {
  final safe = p.name.replaceAll(RegExp(r'[^A-Za-z0-9]+'), '_');
  return 'DPR_${r.reportNo.toString().padLeft(3, '0')}_${safe}_${dateFmt.format(r.date)}.pdf';
}

class PdfPreviewScreen extends StatelessWidget {
  const PdfPreviewScreen({super.key, required this.project, required this.report});

  final Project project;
  final DailyReport report;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: Text('DPR-${report.reportNo} PDF')),
      body: PdfPreview(
        build: (_) => PdfService.build(project, report),
        pdfFileName: _fileName(project, report),
        canChangePageFormat: false,
        canChangeOrientation: false,
        canDebug: false,
      ),
    );
  }
}
