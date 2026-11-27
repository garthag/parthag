import 'dart:math' as math;
import 'dart:typed_data';

import 'package:intl/intl.dart';
import 'package:material_ui/material_ui.dart';
import 'package:share_plus/share_plus.dart';

import '../cad/canvas_controller.dart';
import '../cad/painter.dart';
import '../models/drawing.dart';

const kAccent = Color(0xFFF2B200);

ThemeData buildTheme(Brightness b) {
  final light = b == Brightness.light;
  final scheme = ColorScheme.fromSeed(
    seedColor: kAccent,
    brightness: b,
    dynamicSchemeVariant: DynamicSchemeVariant.fidelity,
  ).copyWith(
    primary: light ? const Color(0xFFB07D00) : const Color(0xFFFFC933),
    onPrimary: light ? Colors.white : const Color(0xFF2A1E00),
    surface: light ? const Color(0xFFFFFFFF) : const Color(0xFF1C1C1E),
    surfaceContainerLowest: light ? const Color(0xFFF2F2F7) : const Color(0xFF000000),
    surfaceContainerLow: light ? const Color(0xFFF2F2F7) : const Color(0xFF161618),
    surfaceContainer: light ? const Color(0xFFEDEDF2) : const Color(0xFF232326),
    surfaceContainerHigh: light ? const Color(0xFFE5E5EA) : const Color(0xFF2C2C2E),
    onSurfaceVariant: light ? const Color(0xFF6E6E73) : const Color(0xFF98989F),
    outlineVariant: light ? const Color(0xFFDADADF) : const Color(0xFF38383A),
  );
  return ThemeData(
    colorScheme: scheme,
    useMaterial3: true,
    scaffoldBackgroundColor: scheme.surfaceContainerLowest,
    appBarTheme: AppBarTheme(
      backgroundColor: scheme.surfaceContainerLowest,
      surfaceTintColor: Colors.transparent,
      foregroundColor: scheme.onSurface,
      elevation: 0,
      scrolledUnderElevation: 0,
    ),
    dividerTheme: DividerThemeData(color: scheme.outlineVariant, space: 1, thickness: .6),
    iconButtonTheme: IconButtonThemeData(style: IconButton.styleFrom(foregroundColor: scheme.primary)),
    inputDecorationTheme: const InputDecorationTheme(isDense: true),
    tooltipTheme: const TooltipThemeData(waitDuration: Duration(milliseconds: 500)),
  );
}

/// Thumbnail of a drawing on its paper.
class DrawingPreview extends StatelessWidget {
  const DrawingPreview({super.key, required this.drawing, this.radius = 8, this.paperLines = true});

  final Drawing drawing;
  final double radius;
  final bool paperLines;

  @override
  Widget build(BuildContext context) {
    return ClipRRect(
      borderRadius: BorderRadius.circular(radius),
      child: RepaintBoundary(
        child: CustomPaint(
          painter: DrawingPreviewPainter(drawing, paperLines: paperLines),
          size: Size.infinite,
        ),
      ),
    );
  }
}

/// Apple Notes style list date: time today, "Yesterday", weekday this week,
/// otherwise a short date.
String noteTimeLabel(DateTime d, {DateTime? now}) {
  now ??= DateTime.now();
  final today = DateTime(now.year, now.month, now.day);
  final day = DateTime(d.year, d.month, d.day);
  final diff = today.difference(day).inDays;
  if (diff <= 0) return DateFormat.jm().format(d);
  if (diff == 1) return 'Yesterday';
  if (diff < 7) return DateFormat.EEEE().format(d);
  return DateFormat('d/M/yy').format(d);
}

String noteFullDate(DateTime d) => DateFormat("d MMMM y 'at' h:mm a").format(d);

Future<String?> promptText(
  BuildContext context, {
  required String title,
  String initial = '',
  String? hint,
  String confirm = 'OK',
}) {
  final ctrl = TextEditingController(text: initial);
  return showDialog<String>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: Text(title),
      content: TextField(
        controller: ctrl,
        autofocus: true,
        decoration: InputDecoration(hintText: hint),
        onSubmitted: (v) => Navigator.pop(ctx, v.trim()),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Cancel')),
        FilledButton(onPressed: () => Navigator.pop(ctx, ctrl.text.trim()), child: Text(confirm)),
      ],
    ),
  );
}

Future<bool> confirmDialog(BuildContext context, String title, String message, {String confirm = 'Delete'}) async {
  final ok = await showDialog<bool>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: Text(title),
      content: Text(message),
      actions: [
        TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text('Cancel')),
        FilledButton(
          style: FilledButton.styleFrom(backgroundColor: Theme.of(ctx).colorScheme.error),
          onPressed: () => Navigator.pop(ctx, true),
          child: Text(confirm),
        ),
      ],
    ),
  );
  return ok ?? false;
}

void toast(BuildContext context, String message) {
  ScaffoldMessenger.maybeOf(context)
    ?..hideCurrentSnackBar()
    ..showSnackBar(SnackBar(content: Text(message), behavior: SnackBarBehavior.floating, duration: const Duration(seconds: 2)));
}

Future<void> shareText(BuildContext context, String text, {String? subject}) async {
  try {
    await SharePlus.instance.share(ShareParams(text: text, subject: subject));
  } catch (e) {
    if (context.mounted) toast(context, 'Sharing is not available here.');
  }
}

Future<void> shareBytes(BuildContext context, Uint8List bytes, String name, String mime) async {
  try {
    await SharePlus.instance.share(ShareParams(
      files: [XFile.fromData(bytes, name: name, mimeType: mime)],
      fileNameOverrides: [name],
      subject: name,
    ));
  } catch (e) {
    if (context.mounted) toast(context, 'Sharing is not available here.');
  }
}

/// Ink colour choices, Samsung Notes style.
const penColors = <int>[
  0xFF1B1B1F,
  0xFF6E6E73,
  0xFFF2F4F7,
  0xFFE5484D,
  0xFFF76B15,
  0xFFF5A524,
  0xFF30A46C,
  0xFF2F6FED,
  0xFF8E4EC6,
  0xFFD6409F,
];

const highlighterColors = <int>[0xFFFFE066, 0xFF9BE58F, 0xFF8FD3FF, 0xFFFFA3C7, 0xFFFFB86B];

/// Layer and object colours, close to AutoCAD's standard colours.
const cadColors = <int>[
  kAdaptiveColor,
  0xFFFF4D4D,
  0xFFFFE14D,
  0xFF4DFF6A,
  0xFF4DFFF6,
  0xFF4D7CFF,
  0xFFFF4DF0,
  0xFFFF9F40,
  0xFF9E9E9E,
];

class ColorDot extends StatelessWidget {
  const ColorDot({super.key, required this.color, this.selected = false, this.size = 26, this.onTap, this.paper});

  final int color;
  final bool selected;
  final double size;
  final VoidCallback? onTap;

  /// Paper that decides how the adaptive colour looks.
  final PaperStyle? paper;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final c = color == kAdaptiveColor ? (paper?.foreground ?? scheme.onSurface) : Color(color);
    return GestureDetector(
      onTap: onTap,
      child: Container(
        width: size,
        height: size,
        margin: const EdgeInsets.all(3),
        decoration: BoxDecoration(
          color: c,
          shape: BoxShape.circle,
          border: Border.all(
            color: selected ? scheme.primary : scheme.outlineVariant,
            width: selected ? 3 : 1,
          ),
        ),
        child: color == kAdaptiveColor
            ? Center(child: Text('A', style: TextStyle(fontSize: size * .42, color: c.computeLuminance() > .5 ? Colors.black : Colors.white)))
            : null,
      ),
    );
  }
}

/// Icon for a canvas tool. CAD shapes are drawn so they show the real
/// geometry (end points, centre, dimension arrows).
class ToolIcon extends StatelessWidget {
  const ToolIcon(this.tool, {super.key, this.size = 22, this.color});

  final Tool tool;
  final double size;
  final Color? color;

  static IconData? material(Tool t) => switch (t) {
        Tool.select => Icons.near_me_outlined,
        Tool.lasso => Icons.gesture,
        Tool.pan => Icons.back_hand_outlined,
        Tool.pen => Icons.draw_outlined,
        Tool.pencil => Icons.create_outlined,
        Tool.highlighter => Icons.border_color_outlined,
        Tool.eraser => Icons.cleaning_services_outlined,
        Tool.text => Icons.text_fields,
        Tool.measure => Icons.straighten,
        _ => null,
      };

  @override
  Widget build(BuildContext context) {
    final col = color ?? IconTheme.of(context).color ?? Colors.black;
    final icon = material(tool);
    if (icon != null) return Icon(icon, size: size, color: col);
    return CustomPaint(size: Size.square(size), painter: _ToolIconPainter(tool, col));
  }
}

class _ToolIconPainter extends CustomPainter {
  _ToolIconPainter(this.tool, this.color);

  final Tool tool;
  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    final s = size.width;
    final p = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = s / 12
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round;
    final dot = Paint()..color = color;
    Offset o(double x, double y) => Offset(x * s, y * s);
    void node(Offset c) => canvas.drawRect(Rect.fromCenter(center: c, width: s / 6, height: s / 6), dot);
    switch (tool) {
      case Tool.line:
        canvas.drawLine(o(.2, .8), o(.8, .2), p);
        node(o(.2, .8));
        node(o(.8, .2));
      case Tool.polyline:
        final pts = [o(.12, .78), o(.38, .3), o(.62, .62), o(.88, .2)];
        canvas.drawPath(Path()..addPolygon(pts, false), p);
        pts.forEach(node);
      case Tool.rect:
        canvas.drawRect(Rect.fromLTRB(.16 * s, .26 * s, .84 * s, .74 * s), p);
      case Tool.circle:
        canvas.drawCircle(o(.5, .5), .34 * s, p);
        canvas.drawCircle(o(.5, .5), s / 16, dot);
      case Tool.arc:
        canvas.drawArc(Rect.fromCircle(center: o(.5, .72), radius: .38 * s), math.pi * 1.08, math.pi * .84, false, p);
        node(o(.15, .6));
        node(o(.85, .6));
      case Tool.dimension:
        final t = Paint()
          ..color = color
          ..strokeWidth = s / 16;
        canvas.drawLine(o(.12, .25), o(.12, .78), t);
        canvas.drawLine(o(.88, .25), o(.88, .78), t);
        canvas.drawLine(o(.14, .52), o(.86, .52), t);
        for (final (tip, dir) in [(o(.14, .52), 1.0), (o(.86, .52), -1.0)]) {
          canvas.drawPath(
            Path()
              ..moveTo(tip.dx, tip.dy)
              ..lineTo(tip.dx + dir * s * .2, tip.dy - s * .09)
              ..lineTo(tip.dx + dir * s * .2, tip.dy + s * .09)
              ..close(),
            dot,
          );
        }
      case Tool.area:
        final pts = [o(.15, .7), o(.3, .2), o(.8, .3), o(.86, .78)];
        canvas.drawPath(Path()..addPolygon(pts, true), Paint()..color = color.withValues(alpha: .25));
        canvas.drawPath(Path()..addPolygon(pts, true), p);
      default:
        break;
    }
  }

  @override
  bool shouldRepaint(_ToolIconPainter old) => old.tool != tool || old.color != color;
}
