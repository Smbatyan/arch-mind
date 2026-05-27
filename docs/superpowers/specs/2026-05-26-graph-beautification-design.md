# Graph Visualization Beautification

**Date:** 2026-05-26  
**File:** `admin-ui/app/(authed)/workspaces/[slug]/graph/graph-canvas.tsx`  
**Approach:** Option A — reagraph props + custom theme (no new dependencies)

---

## Problem

Current graph canvas has three pain points:
1. Nodes are plain colored circles with no visual weight or hierarchy
2. No hover feedback — mousing over nodes does nothing
3. Edge labels render permanently for all edges, creating visual noise

---

## Design

### 1. Custom Theme

Construct theme via spread so future reagraph updates inherit new defaults:

```ts
import { lightTheme } from "reagraph";

const customTheme = {
  ...lightTheme,
  canvas: {
    ...lightTheme.canvas,
    background: "#f1f5f9",     // slate-100
    fog: "#f1f5f9",
  },
  edge: {
    ...lightTheme.edge,
    fill: "#94a3b8",           // slate-400
    activeFill: "#6366f1",     // indigo-500
    opacity: 1,
    selectedOpacity: 1,
    inactiveOpacity: 0.1,
    label: {
      ...lightTheme.edge.label,
      stroke: "#f1f5f9",
      color: "#64748b",
      fontSize: 6,
    },
  },
  node: {
    ...lightTheme.node,
    label: {
      ...lightTheme.node.label,
      color: "#334155",         // slate-700
      fontSize: 11,
    },
    activeFill: "#6366f1",
  },
};
```

Pass `theme={customTheme}` to `<GraphCanvas>`. No changes to per-node `size` at the theme level — see hover section.

### 2. Node Hover Effects

**State additions:**
```ts
const [hoveredId, setHoveredId] = React.useState<string | null>(null);
const [tooltipPos, setTooltipPos] = React.useState<{ x: number; y: number } | null>(null);
```

**`rgNodes` memo:** hovered node gets `size: 10` (vs reagraph's default 7 — ~40% larger). All other nodes omit `size` (inherit default):

```ts
const rgNodes: GraphNode[] = React.useMemo(
  () =>
    data.nodes.map((n) => ({
      id: n.id,
      label: n.name ?? n.label,
      fill: nodeColor(n.label),
      ...(n.id === hoveredId ? { size: 10 } : {}),
      data: { label: n.label, name: n.name },
    })),
  [data.nodes, hoveredId]
);
```

**`actives` stays search-only** — do NOT add `hoveredId` to `actives`. Reagraph dims all non-active nodes when `actives` is non-empty; that dimming is desirable for search results but not for casual hover.

**GraphCanvas callbacks:**
```tsx
onNodePointerOver={(node, event) => {
  setHoveredId(node.id);
  setTooltipPos({ x: event.clientX, y: event.clientY });
}}
onNodePointerOut={() => {
  setHoveredId(null);
  setTooltipPos(null);
}}
onNodeClick={(node) => {
  setHoveredId(null);           // clear hover — pointer-out may not fire on click
  setTooltipPos(null);
  setSelectedId((prev) => (prev === node.id ? null : node.id));
}}
```

**Tooltip markup** — rendered outside the canvas `<div>`, at root of the outer wrapper, using `position: fixed` so `clientX/Y` coordinates are correct regardless of page scroll or container offset:

```tsx
{tooltipPos && hoveredId && (() => {
  const node = data.nodes.find((n) => n.id === hoveredId);
  if (!node) return null;
  return (
    <div
      style={{
        position: "fixed",
        left: tooltipPos.x + 12,
        top: tooltipPos.y - 28,
        pointerEvents: "none",
        zIndex: 50,
      }}
      className="flex items-center gap-1.5 rounded-md border border-border bg-background/95 px-2 py-1 text-xs shadow-md backdrop-blur"
    >
      <span
        className="inline-block h-2 w-2 shrink-0 rounded-full"
        style={{ backgroundColor: nodeColor(node.label) }}
      />
      <span className="font-medium text-muted-foreground">{node.label}</span>
      <span className="text-foreground">{node.name ?? node.id.slice(0, 8)}</span>
    </div>
  );
})()}
```

### 3. Edge Labels

Set `labelVisible: false` on every edge in `rgEdges` memo:

```ts
const rgEdges: GraphEdge[] = React.useMemo(
  () =>
    data.edges.map((e) => ({
      id: e.id,
      source: e.source,
      target: e.target,
      label: e.label,
      labelVisible: false,
    })),
  [data.edges]
);
```

Edge information remains fully accessible: clicking any node opens the detail panel showing all incoming/outgoing edges with their labels.

---

## Scope

**Single file change:** `graph-canvas.tsx` only.  
**No new dependencies.**  
**No backend changes.**

---

## Implementation Notes

- Reagraph's default node size is 7. Hover size 10 is ~43% larger — noticeable without being jarring.
- `position: fixed` on the tooltip avoids bounding-rect subtraction; works correctly at any scroll/inset.
- `onNodeClick` explicitly clears hover state because reagraph's WebGL layer does not guarantee a `pointerout` event fires on click.
- Theme spread pattern ensures forward-compatibility with reagraph theme-shape changes.

---

## Out of Scope

- Dark mode theme variant
- Animated edge particles / flow indicators
- Node shape differentiation per type
- Pagination / virtual rendering for large graphs
