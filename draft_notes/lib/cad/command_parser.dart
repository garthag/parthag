import 'dart:math' as math;
import 'dart:ui';

/// Parses AutoCAD style point input:
///  * `x,y` absolute
///  * `@dx,dy` relative to the last point
///  * `@dist<angle` polar, relative to the last point (angle in degrees)
///  * `dist<angle` polar from the origin
///  * `@` the last point itself
Offset? parsePoint(String input, {Offset? last}) {
  var s = input.replaceAll(' ', '');
  if (s.isEmpty) return null;
  var relative = false;
  if (s.startsWith('@')) {
    relative = true;
    s = s.substring(1);
    if (s.isEmpty) return last ?? Offset.zero;
  } else if (s.startsWith('#')) {
    s = s.substring(1);
  }
  Offset? p;
  if (s.contains('<')) {
    final parts = s.split('<');
    if (parts.length != 2) return null;
    final d = double.tryParse(parts[0]);
    final a = double.tryParse(parts[1]);
    if (d == null || a == null) return null;
    final rad = a * math.pi / 180;
    p = Offset(d * math.cos(rad), d * math.sin(rad));
  } else if (s.contains(',')) {
    final parts = s.split(',');
    if (parts.length != 2) return null;
    final x = double.tryParse(parts[0]);
    final y = double.tryParse(parts[1]);
    if (x == null || y == null) return null;
    p = Offset(x, y);
  } else {
    return null;
  }
  return relative ? (last ?? Offset.zero) + p : p;
}

/// Command names and their AutoCAD style aliases.
const Map<String, String> commandAliases = {
  'L': 'LINE',
  'LINE': 'LINE',
  'PL': 'PLINE',
  'PLINE': 'PLINE',
  'POLYLINE': 'PLINE',
  'REC': 'RECTANG',
  'RECT': 'RECTANG',
  'RECTANG': 'RECTANG',
  'RECTANGLE': 'RECTANG',
  'C': 'CIRCLE',
  'CIRCLE': 'CIRCLE',
  'A': 'ARC',
  'ARC': 'ARC',
  'T': 'TEXT',
  'DT': 'TEXT',
  'MT': 'TEXT',
  'TEXT': 'TEXT',
  'MTEXT': 'TEXT',
  'DIM': 'DIMALIGNED',
  'DAL': 'DIMALIGNED',
  'DLI': 'DIMALIGNED',
  'DIMALIGNED': 'DIMALIGNED',
  'DIMLINEAR': 'DIMALIGNED',
  'DI': 'DIST',
  'DIST': 'DIST',
  'MEA': 'DIST',
  'MEASURE': 'DIST',
  'AA': 'AREA',
  'AREA': 'AREA',
  'M': 'MOVE',
  'MOVE': 'MOVE',
  'CO': 'COPY',
  'CP': 'COPY',
  'COPY': 'COPY',
  'RO': 'ROTATE',
  'ROTATE': 'ROTATE',
  'SC': 'SCALE',
  'SCALE': 'SCALE',
  'MI': 'MIRROR',
  'MIRROR': 'MIRROR',
  'O': 'OFFSET',
  'OFFSET': 'OFFSET',
  'E': 'ERASE',
  'ERASE': 'ERASE',
  'DEL': 'ERASE',
  'DELETE': 'ERASE',
  'U': 'UNDO',
  'UNDO': 'UNDO',
  'REDO': 'REDO',
  'Z': 'ZOOM',
  'ZOOM': 'ZOOM',
  'ZE': 'ZOOMEXTENTS',
  'P': 'PAN',
  'PAN': 'PAN',
  'GRID': 'GRID',
  'SN': 'SNAP',
  'SNAP': 'SNAP',
  'ORTHO': 'ORTHO',
  'OS': 'OSNAP',
  'OSNAP': 'OSNAP',
  'LA': 'LAYER',
  'LAYER': 'LAYER',
  'PR': 'PROPERTIES',
  'PROPERTIES': 'PROPERTIES',
  'UN': 'UNITS',
  'UNITS': 'UNITS',
  'SEL': 'SELECT',
  'SELECT': 'SELECT',
  'AI_SELALL': 'SELECTALL',
  'SELECTALL': 'SELECTALL',
  'LASSO': 'LASSO',
  'PEN': 'PEN',
  'PENCIL': 'PENCIL',
  'HL': 'HIGHLIGHTER',
  'HIGHLIGHTER': 'HIGHLIGHTER',
  'ERASER': 'ERASER',
  'HELP': 'HELP',
  '?': 'HELP',
};

const helpText = <String>[
  'Draw: LINE (L), PLINE (PL), RECTANG (REC), CIRCLE (C), ARC (A), TEXT (T), DIMALIGNED (DIM)',
  'Modify: MOVE (M), COPY (CO), ROTATE (RO), SCALE (SC), MIRROR (MI), OFFSET (O), ERASE (E)',
  'Inquiry: DIST (DI), AREA (AA)   View: ZOOM (Z) E, PAN (P)',
  'Settings: GRID, SNAP, ORTHO, OSNAP, LAYER (LA), UNITS, UNDO (U), REDO',
  'Points: x,y  @dx,dy  @dist<angle  or a number for distance along the cursor',
];
