import 'dart:async';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:printing/printing.dart';
import 'package:provider/provider.dart';

import '../models/drawing.dart';
import '../services/drawing_service.dart';
import '../state/app_state.dart';
import '../widgets/common.dart';
import 'drawing_register.dart';

/// Full-screen drawing viewer: PDF and image drawings with pinch / double-tap
/// zoom, rotation, page navigation, revision history and a clear warning when
/// the open sheet is not the latest revision.
class DrawingViewerScreen extends StatefulWidget {
  const DrawingViewerScreen({super.key, required this.drawingId, this.sequence = const []});

  final String drawingId;

  /// Register order of current drawings, for previous / next.
  final List<String> sequence;

  @override
  State<DrawingViewerScreen> createState() => _DrawingViewerScreenState();
}

class _DrawingViewerScreenState extends State<DrawingViewerScreen> {
  late String _id = widget.drawingId;
  int _turns = 0;
  Future<Uint8List?>? _bytes;
  String? _loadedId;

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final d = state.drawing(_id);
    if (d == null) return const Scaffold(body: Center(child: Text('Drawing not found')));
    if (_loadedId != d.id) {
      _loadedId = d.id;
      _bytes = context.read<DrawingService>().readBytes(d);
    }
    final revisions = state.revisionsOf(d);
    final latest = revisions.first;
    final isLatest = latest.id == d.id;
    final seqIndex = widget.sequence.indexOf(isLatest ? d.id : latest.id);
    final t = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(d.number, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 16)),
            Text(
              'Rev ${d.revision} · ${d.status.code} · ${d.type.code} · ${[plantTitle(d.plant), unitTitle(d.unit)].join(' › ')}',
              overflow: TextOverflow.ellipsis,
              style: t.textTheme.bodySmall?.copyWith(color: t.appBarTheme.foregroundColor),
            ),
          ],
        ),
        actions: [
          IconButton(
            tooltip: 'Rotate',
            onPressed: () => setState(() => _turns = (_turns + 1) % 4),
            icon: const Icon(Icons.rotate_90_degrees_cw_outlined),
          ),
          IconButton(
            tooltip: 'Details & revisions',
            onPressed: () => _showDetails(context, d, revisions),
            icon: const Icon(Icons.info_outline),
          ),
          PopupMenuButton<String>(
            onSelected: (v) => drawingAction(context, d, v),
            itemBuilder: (_) => [
              const PopupMenuItem(value: 'share', child: Text('Share / open in another app')),
              const PopupMenuItem(value: 'edit', child: Text('Edit details')),
              if (revisions.length > 1)
                PopupMenuItem(value: 'delete-one', child: Text('Delete Rev ${d.revision} only')),
            ],
          ),
        ],
      ),
      body: Column(
        children: [
          if (!isLatest)
            MaterialBanner(
              backgroundColor: Colors.red.shade50,
              leading: Icon(Icons.warning_amber_rounded, color: Colors.red.shade700),
              content: Text(
                'SUPERSEDED – Rev ${d.revision} is not the latest. Rev ${latest.revision} '
                '(${latest.status.code}) is current. Do not use for construction.',
                style: TextStyle(color: Colors.red.shade900),
              ),
              actions: [
                TextButton(
                  onPressed: () => setState(() => _id = latest.id),
                  child: Text('Open Rev ${latest.revision}'),
                ),
              ],
            ),
          Expanded(
            child: FutureBuilder<Uint8List?>(
              future: _bytes,
              builder: (context, snap) {
                if (snap.connectionState != ConnectionState.done) {
                  return const Center(child: CircularProgressIndicator());
                }
                final bytes = snap.data;
                if (bytes == null) {
                  return const EmptyState(
                    icon: Icons.cloud_off_outlined,
                    title: 'File not on this device',
                    message: 'Import the drawing ZIP again on this device to view it.',
                  );
                }
                return switch (d.format) {
                  DrawingFormat.pdf => _PdfView(key: ValueKey(d.id), bytes: bytes, turns: _turns),
                  DrawingFormat.image => _ZoomView(
                      turns: _turns,
                      child: Image.memory(bytes,
                          fit: BoxFit.contain,
                          filterQuality: FilterQuality.medium,
                          errorBuilder: (_, _, _) => const _Unsupported(
                              message: 'This image format cannot be shown. Share it to open in another app.')),
                    ),
                  _ => _Unsupported(
                      message: '${d.fileName.split('.').last.toUpperCase()} files cannot be previewed here. '
                          'Tap Share to open in AutoCAD, DWG FastView or another CAD viewer.',
                      onShare: () => shareDrawing(context, d),
                    ),
                };
              },
            ),
          ),
          if (widget.sequence.length > 1 && seqIndex >= 0)
            Material(
              color: t.colorScheme.surfaceContainer,
              child: SafeArea(
                top: false,
                child: Row(
                  children: [
                    IconButton(
                      tooltip: 'Previous drawing',
                      onPressed: seqIndex > 0 ? () => _go(widget.sequence[seqIndex - 1]) : null,
                      icon: const Icon(Icons.skip_previous),
                    ),
                    Expanded(
                      child: Text(
                        '${seqIndex + 1} of ${widget.sequence.length}  ·  ${d.displayTitle}',
                        textAlign: TextAlign.center,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: t.textTheme.bodySmall,
                      ),
                    ),
                    IconButton(
                      tooltip: 'Next drawing',
                      onPressed: seqIndex < widget.sequence.length - 1
                          ? () => _go(widget.sequence[seqIndex + 1])
                          : null,
                      icon: const Icon(Icons.skip_next),
                    ),
                  ],
                ),
              ),
            ),
        ],
      ),
    );
  }

  void _go(String id) => setState(() {
        _id = id;
        _turns = 0;
      });

  void _showDetails(BuildContext context, Drawing d, List<Drawing> revisions) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (c) {
        final t = Theme.of(c);
        Widget row(String k, String v) => Padding(
              padding: const EdgeInsets.symmetric(vertical: 3),
              child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                SizedBox(width: 110, child: Text(k, style: t.textTheme.bodySmall)),
                Expanded(child: Text(v.isEmpty ? '–' : v)),
              ]),
            );
        return DraggableScrollableSheet(
          expand: false,
          initialChildSize: .6,
          maxChildSize: .92,
          builder: (c, scroll) => ListView(
            controller: scroll,
            padding: const EdgeInsets.fromLTRB(16, 0, 16, 24),
            children: [
              Text(d.number, style: t.textTheme.titleLarge),
              Text(d.displayTitle, style: t.textTheme.bodyMedium),
              const SizedBox(height: 12),
              Text('Revision history (${revisions.length})', style: t.textTheme.titleMedium),
              for (final (i, r) in revisions.indexed)
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: RevBadge(r, superseded: i > 0),
                  title: Text(i == 0 ? 'Current revision' : 'Superseded'),
                  subtitle: Text('${r.status.label} · received ${dateFmt.format(r.importedAt)}\n${r.fileName}'),
                  selected: r.id == d.id,
                  trailing: r.id == d.id ? const Icon(Icons.visibility) : null,
                  onTap: () {
                    Navigator.pop(c);
                    setState(() => _id = r.id);
                  },
                ),
              const Divider(),
              Text('Details', style: t.textTheme.titleMedium),
              row('Plant', plantTitle(d.plant)),
              row('Unit / Area', unitTitle(d.unit)),
              row('Drawing type', d.type.label),
              row('Sheet kind', d.kind),
              row('Revision', d.revision),
              row('Status', '${d.status.code} – ${d.status.label}'),
              row('Received', timeFmt.format(d.importedAt)),
              row('File', '${d.fileName}  ${fileSizeLabel(d.sizeBytes)}'),
              row('ZIP folder', d.sourcePath),
              row('Remarks', d.remarks),
            ],
          ),
        );
      },
    );
  }
}

class _Unsupported extends StatelessWidget {
  const _Unsupported({required this.message, this.onShare});
  final String message;
  final VoidCallback? onShare;

  @override
  Widget build(BuildContext context) => EmptyState(
        icon: Icons.architecture,
        title: 'No preview',
        message: message,
        action: onShare == null
            ? null
            : FilledButton.icon(onPressed: onShare, icon: const Icon(Icons.open_in_new), label: const Text('Open with…')),
      );
}

/// Pinch-zoom / pan / double-tap-zoom wrapper.
class _ZoomView extends StatefulWidget {
  const _ZoomView({required this.child, required this.turns});
  final Widget child;
  final int turns;

  @override
  State<_ZoomView> createState() => _ZoomViewState();
}

class _ZoomViewState extends State<_ZoomView> {
  final _ctrl = TransformationController();
  TapDownDetails? _tap;

  @override
  void didUpdateWidget(covariant _ZoomView old) {
    super.didUpdateWidget(old);
    if (old.turns != widget.turns) _ctrl.value = Matrix4.identity();
  }

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return ColoredBox(
      color: Colors.grey.shade300,
      child: GestureDetector(
        onDoubleTapDown: (d) => _tap = d,
        onDoubleTap: () {
          if (_ctrl.value.getMaxScaleOnAxis() > 1.01) {
            _ctrl.value = Matrix4.identity();
          } else {
            final p = _tap?.localPosition ?? Offset.zero;
            const s = 3.0;
            _ctrl.value = Matrix4.identity()
              ..translateByDouble(-p.dx * (s - 1), -p.dy * (s - 1), 0, 1)
              ..scaleByDouble(s, s, 1, 1);
          }
        },
        child: InteractiveViewer(
          transformationController: _ctrl,
          minScale: 1,
          maxScale: 16,
          child: SizedBox.expand(
            child: RotatedBox(quarterTurns: widget.turns, child: widget.child),
          ),
        ),
      ),
    );
  }
}

/// Renders PDF pages with the PDFium engine bundled in the `printing` package.
/// A quick low-resolution pass gives the page count and sizes; the visible
/// page is then rendered sharp enough for zooming into A1/A0 sheets.
class _PdfView extends StatefulWidget {
  const _PdfView({super.key, required this.bytes, required this.turns});
  final Uint8List bytes;
  final int turns;

  @override
  State<_PdfView> createState() => _PdfViewState();
}

class _PdfViewState extends State<_PdfView> {
  final List<Size> _pageInches = [];
  final Map<int, ui.Image> _pages = {};
  int _page = 0;
  bool _scanning = true;
  Object? _error;

  @override
  void initState() {
    super.initState();
    _scan();
  }

  @override
  void dispose() {
    for (final i in _pages.values) {
      i.dispose();
    }
    super.dispose();
  }

  Future<void> _scan() async {
    try {
      const dpi = 18.0;
      await for (final r in Printing.raster(widget.bytes, dpi: dpi)) {
        _pageInches.add(Size(r.width / dpi, r.height / dpi));
        if (_pageInches.length == 1) {
          // Show the first page as soon as it is known.
          unawaited(_render(0));
        }
      }
    } catch (e) {
      _error = e;
    }
    if (mounted) setState(() => _scanning = false);
  }

  Future<void> _render(int page) async {
    if (_pages.containsKey(page)) {
      setState(() => _page = page);
      return;
    }
    // Aim for ~4000 px on the long side, within 72–300 dpi.
    final inches = _pageInches[page];
    final longSide = inches.width > inches.height ? inches.width : inches.height;
    final dpi = (4000 / (longSide <= 0 ? 11.7 : longSide)).clamp(72, 300).toDouble();
    try {
      await for (final r in Printing.raster(widget.bytes, pages: [page], dpi: dpi)) {
        final img = await r.toImage();
        if (!mounted) {
          img.dispose();
          return;
        }
        // Keep at most 3 rendered pages in memory.
        if (_pages.length >= 3) {
          final drop = _pages.keys.firstWhere((k) => k != _page, orElse: () => _pages.keys.first);
          _pages.remove(drop)?.dispose();
        }
        setState(() {
          _pages[page] = img;
          _page = page;
        });
      }
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_error != null && _pages.isEmpty) {
      return _Unsupported(message: 'This PDF could not be rendered ($_error).');
    }
    final img = _pages[_page];
    final count = _pageInches.length;
    return Stack(
      children: [
        Positioned.fill(
          child: img == null
              ? const Center(child: CircularProgressIndicator())
              : _ZoomView(
                  turns: widget.turns,
                  child: RawImage(image: img, fit: BoxFit.contain, filterQuality: FilterQuality.medium),
                ),
        ),
        if (count > 1 || _scanning)
          Positioned(
            bottom: 12,
            left: 0,
            right: 0,
            child: Center(
              child: Material(
                elevation: 3,
                borderRadius: BorderRadius.circular(24),
                color: Theme.of(context).colorScheme.surfaceContainerHigh,
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    IconButton(
                      onPressed: _page > 0 ? () => _render(_page - 1) : null,
                      icon: const Icon(Icons.chevron_left),
                    ),
                    Text('Sheet ${_page + 1} / ${_scanning ? '…' : count}'),
                    IconButton(
                      onPressed: _page < count - 1 ? () => _render(_page + 1) : null,
                      icon: const Icon(Icons.chevron_right),
                    ),
                  ],
                ),
              ),
            ),
          ),
      ],
    );
  }
}
