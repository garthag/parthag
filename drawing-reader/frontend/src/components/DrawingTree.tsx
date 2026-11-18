import { Box, Chip, InputAdornment, LinearProgress, TextField, Typography } from '@mui/material';
import SearchIcon from '@mui/icons-material/Search';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import ChevronRightIcon from '@mui/icons-material/ChevronRight';
import DescriptionIcon from '@mui/icons-material/DescriptionOutlined';
import { useEffect, useMemo, useState } from 'react';
import { useTree } from '../api/hooks';
import type { TreeNode } from '../api/types';

interface Props {
  projectId: number | null;
  selectedId: number | null;
  onSelect: (drawingId: number) => void;
}

function useDebounced<T>(value: T, ms = 250): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

/** Project > Discipline > Area > Drawing navigation tree with server-side search. */
export function DrawingTree({ projectId, selectedId, onSelect }: Props) {
  const [search, setSearch] = useState('');
  const query = useDebounced(search);
  const tree = useTree(projectId, query);
  const [open, setOpen] = useState<Set<string>>(new Set());

  const nodes = useMemo(() => tree.data ?? [], [tree.data]);
  // expand everything while searching; expand the first project by default
  useEffect(() => {
    if (query) {
      const all = new Set<string>();
      const walk = (n: TreeNode) => {
        if (n.children.length) all.add(n.id);
        n.children.forEach(walk);
      };
      nodes.forEach(walk);
      setOpen(all);
    } else if (nodes.length && open.size === 0) {
      setOpen(new Set([nodes[0].id]));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [nodes, query]);

  const toggle = (id: string) =>
    setOpen((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const render = (node: TreeNode, depth: number): React.ReactNode => {
    const isDrawing = node.type === 'drawing';
    const expanded = open.has(node.id);
    const selected = isDrawing && node.entity_id === selectedId;
    return (
      <Box key={node.id}>
        <Box
          role="treeitem"
          aria-expanded={isDrawing ? undefined : expanded}
          aria-selected={selected}
          onClick={() => (isDrawing && node.entity_id ? onSelect(node.entity_id) : toggle(node.id))}
          sx={{
            display: 'flex',
            alignItems: 'center',
            gap: 0.5,
            pl: depth * 1.5 + 0.5,
            pr: 1,
            py: 0.35,
            cursor: 'pointer',
            borderRadius: 1,
            bgcolor: selected ? 'action.selected' : undefined,
            '&:hover': { bgcolor: 'action.hover' },
          }}
        >
          {isDrawing ? (
            <DescriptionIcon sx={{ fontSize: 16, color: 'text.secondary' }} />
          ) : expanded ? (
            <ExpandMoreIcon sx={{ fontSize: 18 }} />
          ) : (
            <ChevronRightIcon sx={{ fontSize: 18 }} />
          )}
          <Typography variant="body2" noWrap sx={{ flex: 1, fontWeight: isDrawing ? 400 : 600 }} title={String(node.meta.title ?? node.label)}>
            {node.label}
          </Typography>
          {isDrawing ? (
            node.meta.revision ? <Chip size="small" label={String(node.meta.revision)} sx={{ height: 18, fontSize: 11 }} /> : null
          ) : (
            <Typography variant="caption" color="text.secondary">
              {node.count}
            </Typography>
          )}
        </Box>
        {!isDrawing && expanded && node.children.map((c) => render(c, depth + 1))}
      </Box>
    );
  };

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      <TextField
        size="small"
        placeholder="Search number, title, discipline"
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        sx={{ m: 1 }}
        slotProps={{ input: { startAdornment: <InputAdornment position="start"><SearchIcon fontSize="small" /></InputAdornment> } }}
      />
      {tree.isFetching && <LinearProgress sx={{ height: 2 }} />}
      <Box role="tree" sx={{ flex: 1, overflow: 'auto', px: 0.5, pb: 1 }}>
        {nodes.map((n) => render(n, 0))}
        {!tree.isLoading && nodes.length === 0 && (
          <Typography color="text.secondary" variant="body2" sx={{ p: 2 }}>
            No drawings found
          </Typography>
        )}
      </Box>
    </Box>
  );
}
