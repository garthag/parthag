import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../data/sample_data.dart';
import '../models/models.dart';
import '../services/cloud_sync.dart';
import '../state/app_state.dart';
import '../widgets/common.dart';
import 'project_form_screen.dart';
import 'project_screen.dart';
import 'settings_screen.dart';

class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final cloud = context.watch<CloudSync>();
    final projects = [...state.projects]..sort((a, b) => b.updatedAt.compareTo(a.updatedAt));

    return Scaffold(
      appBar: AppBar(
        title: const Text('Site DPR'),
        actions: [
          if (cloud.user != null)
            IconButton(
              tooltip: 'Sync with Google cloud',
              onPressed: cloud.syncing ? null : () => cloud.sync(state),
              icon: cloud.syncing
                  ? const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2))
                  : const Icon(Icons.cloud_sync_outlined),
            ),
          IconButton(
            tooltip: 'Settings',
            icon: const Icon(Icons.settings_outlined),
            onPressed: () => Navigator.push(context, MaterialPageRoute(builder: (_) => const SettingsScreen())),
          ),
        ],
      ),
      body: projects.isEmpty
          ? EmptyState(
              icon: Icons.apartment,
              title: 'No projects yet',
              message: 'Create a project to start recording Daily Progress Reports.',
              action: Column(
                children: [
                  FilledButton.icon(
                    onPressed: () => _newProject(context),
                    icon: const Icon(Icons.add),
                    label: const Text('New project'),
                  ),
                  const SizedBox(height: 8),
                  TextButton.icon(
                    onPressed: () => createSampleProject(state),
                    icon: const Icon(Icons.auto_awesome_outlined),
                    label: const Text('Try with a sample project'),
                  ),
                ],
              ),
            )
          : ListView.separated(
              padding: const EdgeInsets.fromLTRB(12, 12, 12, 96),
              itemCount: projects.length,
              separatorBuilder: (_, _) => const SizedBox(height: 10),
              itemBuilder: (context, i) => _ProjectCard(project: projects[i]),
            ),
      floatingActionButton: projects.isEmpty
          ? null
          : FloatingActionButton.extended(
              onPressed: () => _newProject(context),
              icon: const Icon(Icons.add_business_outlined),
              label: const Text('New project'),
            ),
    );
  }

  Future<void> _newProject(BuildContext context) async {
    final p = await Navigator.push<Project>(
      context,
      MaterialPageRoute(builder: (_) => const ProjectFormScreen()),
    );
    if (p != null && context.mounted) {
      Navigator.push(context, MaterialPageRoute(builder: (_) => ProjectScreen(projectId: p.id)));
    }
  }
}

class _ProjectCard extends StatelessWidget {
  const _ProjectCard({required this.project});

  final Project project;

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final s = state.summary(project.id);
    final reports = state.reportsFor(project.id);
    final t = Theme.of(context);
    return Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: () => Navigator.push(
          context,
          MaterialPageRoute(builder: (_) => ProjectScreen(projectId: project.id)),
        ),
        child: Padding(
          padding: const EdgeInsets.all(14),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  CircleAvatar(
                    backgroundColor: t.colorScheme.primaryContainer,
                    child: Icon(Icons.apartment, color: t.colorScheme.onPrimaryContainer),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(project.name, style: t.textTheme.titleMedium),
                        if (project.location.isNotEmpty)
                          Text(project.location, style: t.textTheme.bodySmall),
                      ],
                    ),
                  ),
                  if (project.code.isNotEmpty) Chip(label: Text(project.code), visualDensity: VisualDensity.compact),
                ],
              ),
              const SizedBox(height: 12),
              LinearProgressIndicator(value: s.overallProgress / 100, minHeight: 6, borderRadius: BorderRadius.circular(3)),
              const SizedBox(height: 8),
              Wrap(
                spacing: 16,
                runSpacing: 4,
                children: [
                  _meta(Icons.trending_up, '${s.overallProgress.toStringAsFixed(1)}% done'),
                  _meta(Icons.description_outlined, '${s.reportCount} DPRs'),
                  _meta(Icons.groups_outlined, '${s.latestManpower} on site'),
                  _meta(Icons.photo_library_outlined, '${s.photos + s.videos} media'),
                  if (reports.isNotEmpty) _meta(Icons.event, 'Last: ${dateFmt.format(reports.first.date)}'),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _meta(IconData i, String text) => Row(
        mainAxisSize: MainAxisSize.min,
        children: [Icon(i, size: 14), const SizedBox(width: 4), Text(text, style: const TextStyle(fontSize: 12))],
      );
}
