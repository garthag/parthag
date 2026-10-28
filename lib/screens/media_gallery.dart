import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import 'package:share_plus/share_plus.dart';
import 'package:video_player/video_player.dart';

import '../data/standards.dart';
import '../models/models.dart';
import '../services/media_service.dart';
import '../state/app_state.dart';
import '../widgets/common.dart';

/// A media item together with the report it belongs to.
typedef MediaRef = (DailyReport, MediaItem);

/// Capture or import photos/videos into [report], then ask for tags once
/// for the whole batch.
Future<void> captureMedia(BuildContext context, DailyReport report, CaptureMode mode) async {
  final media = context.read<MediaService>();
  final state = context.read<AppState>();
  final messenger = ScaffoldMessenger.of(context);
  late final List<(XFile, MediaType)> picked;
  try {
    picked = await media.pick(mode);
  } catch (e) {
    messenger.showSnackBar(SnackBar(content: Text('Could not open camera / gallery: $e')));
    return;
  }
  if (picked.isEmpty || !context.mounted) return;

  // Geo-tag in parallel with the user filling in tags.
  final geoFuture = media.currentLocation();
  final template = MediaItem(type: picked.first.$2, localPath: '');
  final ok = await showMediaTagSheet(context, template, report, count: picked.length);
  if (!ok) return;

  final geo = await geoFuture;
  for (final (file, type) in picked) {
    final path = await media.importFile(file, report.id);
    report.media.add(MediaItem(
      type: type,
      localPath: path,
      caption: template.caption,
      category: template.category,
      tags: [...template.tags],
      activityId: template.activityId,
      location: template.location,
      latitude: geo?.latitude,
      longitude: geo?.longitude,
    ));
  }
  await state.saveReport(report);
  messenger.showSnackBar(SnackBar(
    content: Text('${picked.length} item(s) added${geo == null ? ' (no GPS fix)' : ' with GPS tag'}'),
  ));
}

/// Edits caption, category, tags, linked activity and location of [item]
/// in place. Returns true when saved.
Future<bool> showMediaTagSheet(BuildContext context, MediaItem item, DailyReport report, {int count = 1}) async {
  final res = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    useSafeArea: true,
    builder: (_) => _TagSheet(item: item, report: report, count: count),
  );
  return res ?? false;
}

class _TagSheet extends StatefulWidget {
  const _TagSheet({required this.item, required this.report, required this.count});

  final MediaItem item;
  final DailyReport report;
  final int count;

  @override
  State<_TagSheet> createState() => _TagSheetState();
}

class _TagSheetState extends State<_TagSheet> {
  late final _caption = TextEditingController(text: widget.item.caption);
  late final _location = TextEditingController(text: widget.item.location);
  final _newTag = TextEditingController();
  late String _category = widget.item.category;
  late final Set<String> _tags = {...widget.item.tags};
  late String _activity = widget.item.activityId;

  @override
  void dispose() {
    _caption.dispose();
    _location.dispose();
    _newTag.dispose();
    super.dispose();
  }

  void _addTag() {
    final t = _newTag.text.trim();
    if (t.isEmpty) return;
    setState(() => _tags.add(t));
    _newTag.clear();
  }

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    final allTags = {...Standards.suggestedTags, ..._tags};
    final activities = widget.report.activities;
    return Padding(
      padding: EdgeInsets.only(bottom: MediaQuery.viewInsetsOf(context).bottom),
      child: ListView(
        shrinkWrap: true,
        padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
        children: [
          Text(widget.count > 1 ? 'Tag ${widget.count} items' : 'Tag media', style: t.textTheme.titleLarge),
          const SizedBox(height: 12),
          TextField(
            controller: _caption,
            textCapitalization: TextCapitalization.sentences,
            decoration: const InputDecoration(labelText: 'Caption / description'),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _location,
            decoration: const InputDecoration(
              labelText: 'Location on site',
              hintText: 'e.g. Tower B / 3rd floor / Grid C-4',
            ),
          ),
          const SizedBox(height: 12),
          if (activities.isNotEmpty)
            DropdownButtonFormField<String>(
              initialValue: activities.any((a) => a.id == _activity) ? _activity : '',
              isExpanded: true,
              decoration: const InputDecoration(labelText: 'Link to work item'),
              items: [
                const DropdownMenuItem(value: '', child: Text('None')),
                for (final a in activities)
                  DropdownMenuItem(
                    value: a.id,
                    child: Text([a.description, if (a.location.isNotEmpty) a.location].join(' - '),
                        overflow: TextOverflow.ellipsis),
                  ),
              ],
              onChanged: (v) => _activity = v ?? '',
            ),
          const SizedBox(height: 16),
          Text('Category', style: t.textTheme.titleSmall),
          const SizedBox(height: 6),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              for (final c in Standards.mediaCategories)
                ChoiceChip(
                  label: Text(c),
                  selected: _category == c,
                  onSelected: (_) => setState(() => _category = c),
                ),
            ],
          ),
          const SizedBox(height: 16),
          Text('Tags', style: t.textTheme.titleSmall),
          const SizedBox(height: 6),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              for (final tag in allTags)
                FilterChip(
                  label: Text(tag),
                  selected: _tags.contains(tag),
                  onSelected: (s) => setState(() => s ? _tags.add(tag) : _tags.remove(tag)),
                ),
            ],
          ),
          const SizedBox(height: 8),
          Row(
            children: [
              Expanded(
                child: TextField(
                  controller: _newTag,
                  decoration: const InputDecoration(labelText: 'Add custom tag'),
                  onSubmitted: (_) => _addTag(),
                ),
              ),
              IconButton.filledTonal(onPressed: _addTag, icon: const Icon(Icons.add)),
            ],
          ),
          const SizedBox(height: 20),
          FilledButton.icon(
            icon: const Icon(Icons.check),
            label: const Text('Save'),
            onPressed: () {
              widget.item
                ..caption = _caption.text.trim()
                ..location = _location.text.trim()
                ..category = _category
                ..tags = _tags.toList()
                ..activityId = _activity;
              Navigator.pop(context, true);
            },
          ),
        ],
      ),
    );
  }
}

/// Filterable grid of media. Used in the report editor and the project's
/// media library.
class MediaGrid extends StatefulWidget {
  const MediaGrid({super.key, required this.items, this.header, this.showReport = false});

  final List<MediaRef> items;
  final Widget? header;
  final bool showReport;

  @override
  State<MediaGrid> createState() => _MediaGridState();
}

class _MediaGridState extends State<MediaGrid> {
  String? _category;
  String? _tag;
  MediaType? _type;

  @override
  Widget build(BuildContext context) {
    final cats = {for (final (_, m) in widget.items) m.category}.toList()..sort();
    final tags = {for (final (_, m) in widget.items) ...m.tags}.toList()..sort();
    final filtered = widget.items.where((e) {
      final m = e.$2;
      return (_category == null || m.category == _category) &&
          (_tag == null || m.tags.contains(_tag)) &&
          (_type == null || m.type == _type);
    }).toList();

    Widget chip(String label, bool selected, VoidCallback onTap) => Padding(
          padding: const EdgeInsets.only(right: 6),
          child: FilterChip(label: Text(label), selected: selected, onSelected: (_) => onTap()),
        );

    return CustomScrollView(
      slivers: [
        if (widget.header != null) SliverToBoxAdapter(child: widget.header),
        if (widget.items.isNotEmpty)
          SliverToBoxAdapter(
            child: SizedBox(
              height: 48,
              child: ListView(
                scrollDirection: Axis.horizontal,
                padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
                children: [
                  chip('📷 Photos', _type == MediaType.photo,
                      () => setState(() => _type = _type == MediaType.photo ? null : MediaType.photo)),
                  chip('🎥 Videos', _type == MediaType.video,
                      () => setState(() => _type = _type == MediaType.video ? null : MediaType.video)),
                  for (final c in cats) chip(c, _category == c, () => setState(() => _category = _category == c ? null : c)),
                  for (final t in tags) chip('#$t', _tag == t, () => setState(() => _tag = _tag == t ? null : t)),
                ],
              ),
            ),
          ),
        if (filtered.isEmpty)
          SliverFillRemaining(
            hasScrollBody: false,
            child: EmptyState(
              icon: Icons.photo_library_outlined,
              title: widget.items.isEmpty ? 'No photos or videos yet' : 'Nothing matches the filter',
            ),
          )
        else
          SliverPadding(
            padding: const EdgeInsets.fromLTRB(12, 4, 12, 96),
            sliver: SliverGrid.builder(
              gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(
                maxCrossAxisExtent: 160,
                mainAxisSpacing: 6,
                crossAxisSpacing: 6,
              ),
              itemCount: filtered.length,
              itemBuilder: (context, i) {
                final (r, m) = filtered[i];
                return GestureDetector(
                  onTap: () => Navigator.push(
                    context,
                    MaterialPageRoute(builder: (_) => MediaViewerScreen(items: filtered, initialIndex: i)),
                  ),
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(8),
                    child: Stack(
                      fit: StackFit.expand,
                      children: [
                        MediaThumb(m),
                        Positioned(
                          left: 0,
                          right: 0,
                          bottom: 0,
                          child: Container(
                            padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 2),
                            color: Colors.black54,
                            child: Text(
                              widget.showReport ? 'DPR-${r.reportNo} · ${m.category}' : m.category,
                              style: const TextStyle(color: Colors.white, fontSize: 11),
                              overflow: TextOverflow.ellipsis,
                            ),
                          ),
                        ),
                        if (m.hasGeoTag)
                          const Positioned(
                            top: 4,
                            right: 4,
                            child: Icon(Icons.location_on, color: Colors.white, size: 16,
                                shadows: [Shadow(blurRadius: 3)]),
                          ),
                      ],
                    ),
                  ),
                );
              },
            ),
          ),
      ],
    );
  }
}

class ProjectMediaLibrary extends StatelessWidget {
  const ProjectMediaLibrary({super.key, required this.projectId});

  final String projectId;

  @override
  Widget build(BuildContext context) {
    final reports = context.watch<AppState>().reportsFor(projectId);
    final items = <MediaRef>[
      for (final r in reports)
        for (final m in r.media.reversed) (r, m),
    ];
    return MediaGrid(items: items, showReport: true);
  }
}

class MediaViewerScreen extends StatefulWidget {
  const MediaViewerScreen({super.key, required this.items, this.initialIndex = 0});

  final List<MediaRef> items;
  final int initialIndex;

  @override
  State<MediaViewerScreen> createState() => _MediaViewerScreenState();
}

class _MediaViewerScreenState extends State<MediaViewerScreen> {
  late final List<MediaRef> _items = [...widget.items];
  late final _page = PageController(initialPage: widget.initialIndex);
  late int _index = widget.initialIndex;

  @override
  void dispose() {
    _page.dispose();
    super.dispose();
  }

  Future<void> _share(MediaItem m) async {
    final f = File(m.localPath);
    final text = [m.caption, m.category, ...m.tags.map((t) => '#$t')].where((s) => s.isNotEmpty).join(' ');
    if (!kIsWeb && f.existsSync()) {
      await SharePlus.instance.share(ShareParams(files: [XFile(m.localPath)], text: text));
    } else if (m.remoteUrl.isNotEmpty) {
      await SharePlus.instance.share(ShareParams(text: '$text\n${m.remoteUrl}'));
    }
  }

  Future<void> _delete(DailyReport r, MediaItem m) async {
    final state = context.read<AppState>();
    if (!await confirm(context, 'Delete this ${m.type.name}?', 'It will be removed from DPR-${r.reportNo}.')) {
      return;
    }
    r.media.removeWhere((e) => e.id == m.id);
    await state.saveReport(r);
    try {
      final f = File(m.localPath);
      if (f.existsSync()) await f.delete();
    } catch (_) {}
    setState(() {
      _items.removeAt(_index);
      if (_index >= _items.length) _index = _items.length - 1;
    });
    if (_items.isEmpty && mounted) Navigator.pop(context);
  }

  @override
  Widget build(BuildContext context) {
    if (_items.isEmpty) return const Scaffold();
    final (r, m) = _items[_index];
    final activity = r.activities.where((a) => a.id == m.activityId).firstOrNull;
    return Scaffold(
      backgroundColor: Colors.black,
      appBar: AppBar(
        backgroundColor: Colors.black,
        foregroundColor: Colors.white,
        title: Text('${_index + 1} / ${_items.length}'),
        actions: [
          IconButton(icon: const Icon(Icons.share), tooltip: 'Share', onPressed: () => _share(m)),
          if (!r.isLocked) ...[
            IconButton(
              icon: const Icon(Icons.sell_outlined),
              tooltip: 'Edit tags',
              onPressed: () async {
                final state = context.read<AppState>();
                if (await showMediaTagSheet(context, m, r)) {
                  await state.saveReport(r);
                  setState(() {});
                }
              },
            ),
            IconButton(icon: const Icon(Icons.delete_outline), tooltip: 'Delete', onPressed: () => _delete(r, m)),
          ],
        ],
      ),
      body: Column(
        children: [
          Expanded(
            child: PageView.builder(
              controller: _page,
              itemCount: _items.length,
              onPageChanged: (i) => setState(() => _index = i),
              itemBuilder: (_, i) {
                final item = _items[i].$2;
                return item.type == MediaType.video
                    ? _VideoView(item: item)
                    : InteractiveViewer(child: Center(child: localImage(item.localPath, remote: item.remoteUrl, fit: BoxFit.contain)));
              },
            ),
          ),
          Container(
            width: double.infinity,
            color: Colors.black,
            padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
            child: DefaultTextStyle(
              style: const TextStyle(color: Colors.white70, fontSize: 13),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  if (m.caption.isNotEmpty)
                    Text(m.caption, style: const TextStyle(color: Colors.white, fontSize: 16)),
                  const SizedBox(height: 4),
                  Wrap(
                    spacing: 6,
                    runSpacing: 4,
                    children: [
                      _pill(m.category, Colors.orange),
                      for (final t in m.tags) _pill('#$t', Colors.blueGrey),
                    ],
                  ),
                  const SizedBox(height: 6),
                  Text('DPR-${r.reportNo} · ${timeFmt.format(m.capturedAt)}'),
                  if (activity != null) Text('Work item: ${activity.description}'),
                  if (m.location.isNotEmpty) Text('Location: ${m.location}'),
                  if (m.hasGeoTag)
                    Text('GPS: ${m.latitude!.toStringAsFixed(6)}, ${m.longitude!.toStringAsFixed(6)}'),
                  if (m.remoteUrl.isNotEmpty) const Text('☁ Backed up to cloud'),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _pill(String text, Color c) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
        decoration: BoxDecoration(color: c.withValues(alpha: .35), borderRadius: BorderRadius.circular(10)),
        child: Text(text, style: const TextStyle(color: Colors.white, fontSize: 12)),
      );
}

class _VideoView extends StatefulWidget {
  const _VideoView({required this.item});

  final MediaItem item;

  @override
  State<_VideoView> createState() => _VideoViewState();
}

class _VideoViewState extends State<_VideoView> {
  VideoPlayerController? _c;
  String? _error;

  @override
  void initState() {
    super.initState();
    final m = widget.item;
    final file = File(m.localPath);
    final c = !kIsWeb && file.existsSync()
        ? VideoPlayerController.file(file)
        : VideoPlayerController.networkUrl(Uri.parse(m.remoteUrl.isNotEmpty ? m.remoteUrl : m.localPath));
    _c = c;
    c.addListener(() {
      if (mounted) setState(() {});
    });
    c.initialize().then((_) {
      if (mounted) setState(() {});
    }).catchError((Object e) {
      if (mounted) setState(() => _error = '$e');
    });
  }

  @override
  void dispose() {
    _c?.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = _c!;
    if (_error != null) {
      return const Center(child: Text('Video unavailable', style: TextStyle(color: Colors.white)));
    }
    if (!c.value.isInitialized) return const Center(child: CircularProgressIndicator());
    return GestureDetector(
      onTap: () => setState(() => c.value.isPlaying ? c.pause() : c.play()),
      child: Stack(
        alignment: Alignment.center,
        children: [
          AspectRatio(aspectRatio: c.value.aspectRatio, child: VideoPlayer(c)),
          if (!c.value.isPlaying) const Icon(Icons.play_circle_fill, color: Colors.white70, size: 72),
          Positioned(
            left: 0,
            right: 0,
            bottom: 0,
            child: VideoProgressIndicator(c, allowScrubbing: true),
          ),
        ],
      ),
    );
  }
}
