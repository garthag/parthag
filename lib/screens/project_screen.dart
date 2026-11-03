import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../models/models.dart';
import '../state/app_state.dart';
import '../widgets/common.dart';
import 'media_gallery.dart';
import 'project_form_screen.dart';
import 'report_screen.dart';

class ProjectScreen extends StatelessWidget {
  const ProjectScreen({super.key, required this.projectId});

  final String projectId;

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final project = state.project(projectId);
    if (project == null) return const Scaffold(body: SizedBox());

    return DefaultTabController(
      length: 3,
      child: Scaffold(
        appBar: AppBar(
          title: Text(project.name, overflow: TextOverflow.ellipsis),
          actions: [
            PopupMenuButton<String>(
              onSelected: (v) async {
                if (v == 'edit') {
                  Navigator.push(
                      context, MaterialPageRoute(builder: (_) => ProjectFormScreen(project: project)));
                } else if (v == 'delete') {
                  final ok = await confirm(context, 'Delete project?',
                      'This deletes "${project.name}" with all its DPRs, photos and videos from this device.');
                  if (ok && context.mounted) {
                    Navigator.pop(context);
                    await state.deleteProject(project.id);
                  }
                }
              },
              itemBuilder: (_) => const [
                PopupMenuItem(value: 'edit', child: Text('Edit project details')),
                PopupMenuItem(value: 'delete', child: Text('Delete project')),
              ],
            ),
          ],
          bottom: const TabBar(tabs: [
            Tab(icon: Icon(Icons.description_outlined), text: 'DPRs'),
            Tab(icon: Icon(Icons.insights_outlined), text: 'Dashboard'),
            Tab(icon: Icon(Icons.photo_library_outlined), text: 'Media'),
          ]),
        ),
        body: TabBarView(children: [
          _ReportList(project: project),
          _Dashboard(project: project),
          ProjectMediaLibrary(projectId: project.id),
        ]),
        floatingActionButton: FloatingActionButton.extended(
          onPressed: () => newReport(context, project.id),
          icon: const Icon(Icons.note_add_outlined),
          label: const Text('New DPR'),
        ),
      ),
    );
  }

  static Future<void> newReport(BuildContext context, String projectId) async {
    final state = context.read<AppState>();
    final now = DateTime.now();
    final date = await showDatePicker(
      context: context,
      helpText: 'Report date',
      firstDate: DateTime(2000),
      lastDate: DateTime(now.year, now.month, now.day),
      initialDate: DateTime(now.year, now.month, now.day),
    );
    if (date == null || !context.mounted) return;

    final existing = state.reportOn(projectId, date);
    if (existing != null) {
      final open = await confirm(context, 'DPR already exists',
          'DPR-${existing.reportNo} is already recorded for ${dateFmt.format(date)}. Open it?',
          action: 'Open');
      if (open && context.mounted) _open(context, existing.id);
      return;
    }

    var carry = true;
    if (state.reportsFor(projectId).any((r) => r.date.isBefore(date))) {
      final choice = await showDialog<bool>(
        context: context,
        builder: (c) => AlertDialog(
          title: const Text('Start from previous DPR?'),
          content: const Text(
              'Copy manpower, machinery and work items from the last report. Cumulative quantities and '
              'material closing stock are carried forward automatically.'),
          actions: [
            TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Blank report')),
            FilledButton(onPressed: () => Navigator.pop(c, true), child: const Text('Carry forward')),
          ],
        ),
      );
      if (choice == null) return;
      carry = choice;
    }
    final r = await state.createReport(projectId, date, carryForward: carry);
    if (context.mounted) _open(context, r.id);
  }

  static void _open(BuildContext context, String id) =>
      Navigator.push(context, MaterialPageRoute(builder: (_) => ReportScreen(reportId: id)));
}

class _ReportList extends StatelessWidget {
  const _ReportList({required this.project});

  final Project project;

  @override
  Widget build(BuildContext context) {
    final reports = context.watch<AppState>().reportsFor(project.id);
    if (reports.isEmpty) {
      return EmptyState(
        icon: Icons.assignment_outlined,
        title: 'No DPRs yet',
        message: 'Tap "New DPR" to record today\'s progress.',
      );
    }
    return ListView.separated(
      padding: const EdgeInsets.fromLTRB(12, 12, 12, 96),
      itemCount: reports.length,
      separatorBuilder: (_, _) => const SizedBox(height: 8),
      itemBuilder: (context, i) {
        final r = reports[i];
        return Card(
          child: ListTile(
            leading: CircleAvatar(child: Text('${r.reportNo}', style: const TextStyle(fontSize: 13))),
            title: Row(
              children: [
                Expanded(child: Text(dateLongFmt.format(r.date))),
                StatusChip(r.status),
              ],
            ),
            subtitle: Padding(
              padding: const EdgeInsets.only(top: 4),
              child: Wrap(
                spacing: 12,
                children: [
                  Text('👷 ${r.totalManpower}'),
                  Text('🏗 ${r.activities.where((a) => a.todayQty > 0).length} items'),
                  Text('📷 ${r.photoCount}'),
                  if (r.videoCount > 0) Text('🎥 ${r.videoCount}'),
                  if (r.hindrances.isNotEmpty) Text('⚠ ${fmtNum(r.hindranceHours)} h lost'),
                  if (r.safety.incidents.isNotEmpty) Text('🚑 ${r.safety.incidents.length}'),
                ],
              ),
            ),
            onTap: () => ProjectScreen._open(context, r.id),
          ),
        );
      },
    );
  }
}

class _Dashboard extends StatelessWidget {
  const _Dashboard({required this.project});

  final Project project;

  @override
  Widget build(BuildContext context) {
    final s = context.watch<AppState>().summary(project.id);
    final t = Theme.of(context);
    final elapsed = _elapsed(project);
    return ListView(
      padding: const EdgeInsets.fromLTRB(12, 12, 12, 96),
      children: [
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('Overall physical progress', style: t.textTheme.titleSmall),
                const SizedBox(height: 8),
                Row(
                  children: [
                    Text('${s.overallProgress.toStringAsFixed(1)}%',
                        style: t.textTheme.headlineMedium?.copyWith(fontWeight: FontWeight.bold)),
                    const SizedBox(width: 16),
                    Expanded(
                      child: LinearProgressIndicator(
                          value: s.overallProgress / 100, minHeight: 10, borderRadius: BorderRadius.circular(5)),
                    ),
                  ],
                ),
                if (elapsed != null) ...[
                  const SizedBox(height: 8),
                  Text('Time elapsed: ${elapsed.toStringAsFixed(1)}% of contract period',
                      style: t.textTheme.bodySmall),
                  const SizedBox(height: 4),
                  LinearProgressIndicator(
                    value: elapsed / 100,
                    minHeight: 4,
                    color: elapsed > s.overallProgress + 10 ? Colors.red : Colors.green,
                  ),
                ],
                const SizedBox(height: 8),
                Text('Average of activity-wise % completion (activities with a defined scope).',
                    style: t.textTheme.bodySmall),
              ],
            ),
          ),
        ),
        const SizedBox(height: 12),
        GridView.count(
          crossAxisCount: 3,
          shrinkWrap: true,
          physics: const NeverScrollableScrollPhysics(),
          mainAxisSpacing: 8,
          crossAxisSpacing: 8,
          childAspectRatio: .95,
          children: [
            StatTile(label: 'DPRs filed', value: '${s.reportCount}', icon: Icons.description_outlined),
            StatTile(label: 'Manpower (latest)', value: '${s.latestManpower}', icon: Icons.groups_outlined),
            StatTile(label: 'Total man-days', value: fmtNum(s.totalManDays), icon: Icons.calendar_month),
            StatTile(
                label: 'Safe man-hours',
                value: fmtNum(s.safeManHours),
                icon: Icons.health_and_safety_outlined,
                color: Colors.green),
            StatTile(
                label: 'Hours lost (hindrance)',
                value: fmtNum(s.hindranceHours),
                icon: Icons.report_problem_outlined,
                color: Colors.orange),
            StatTile(
                label: 'Safety incidents',
                value: '${s.incidents}',
                icon: Icons.local_hospital_outlined,
                color: s.incidents > 0 ? Colors.red : Colors.green),
            StatTile(label: 'Photos', value: '${s.photos}', icon: Icons.photo_camera_outlined),
            StatTile(label: 'Videos', value: '${s.videos}', icon: Icons.videocam_outlined),
            StatTile(
                label: 'Contract value (Rs. lakh)',
                value: project.contractValueLakh > 0 ? fmtNum(project.contractValueLakh) : '-',
                icon: Icons.currency_rupee),
          ],
        ),
        const SizedBox(height: 16),
        Text('Activity-wise progress', style: t.textTheme.titleMedium),
        const SizedBox(height: 8),
        if (s.activities.isEmpty) const Text('Work items appear here once recorded in a DPR.'),
        for (final a in s.activities)
          Card(
            margin: const EdgeInsets.only(bottom: 8),
            child: Padding(
              padding: const EdgeInsets.all(12),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text([if (a.boqItemNo.isNotEmpty) a.boqItemNo, a.description].join(' - '),
                      style: t.textTheme.titleSmall),
                  if (a.location.isNotEmpty) Text(a.location, style: t.textTheme.bodySmall),
                  const SizedBox(height: 6),
                  LinearProgressIndicator(value: a.percentComplete / 100, minHeight: 6),
                  const SizedBox(height: 4),
                  Text(
                    a.totalScope > 0
                        ? '${fmtNum(a.cumulativeQty)} / ${fmtNum(a.totalScope)} ${a.unit}  (${a.percentComplete.toStringAsFixed(1)}%)'
                        : '${fmtNum(a.cumulativeQty)} ${a.unit} done (scope not set)',
                    style: t.textTheme.bodySmall,
                  ),
                ],
              ),
            ),
          ),
      ],
    );
  }

  double? _elapsed(Project p) {
    if (p.startDate == null || p.completionDate == null) return null;
    final total = p.completionDate!.difference(p.startDate!).inDays;
    if (total <= 0) return null;
    final done = DateTime.now().difference(p.startDate!).inDays;
    return (done / total * 100).clamp(0, 100).toDouble();
  }
}
