# Graph Visualization Beautification Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Improve the graph canvas with a polished theme, hover feedback with tooltip, and hidden edge labels.

**Architecture:** All changes are confined to a single file (`graph-canvas.tsx`). Three additive changes: (1) custom reagraph theme via spread, (2) `hoveredId` state driving per-node size bump and a fixed-position tooltip, (3) `labelVisible: false` on all edges in the `rgEdges` memo.

**Tech Stack:** React 19, Next.js 16, reagraph ^4.30.8, Tailwind CSS v4, TypeScript 5

**Spec:** `docs/superpowers/specs/2026-05-26-graph-beautification-design.md`

---

## Chunk 1: Theme + Edge Labels + Hover Tooltip

### Task 1: Custom Theme

**Files:**
- Modify: `admin-ui/app/(authed)/workspaces/[slug]/graph/graph-canvas.tsx`

- [ ] **Step 1: Add `customTheme` constant after the `DEFAULT_COLOR` line**

Open `graph-canvas.tsx`. After line:
```ts
const DEFAULT_COLOR = "#94a3b8";
```

Insert:
```ts
const customTheme = {
  ...lightTheme,
  canvas: {
    ...lightTheme.canvas,
    background: "#f1f5f9",
    fog: "#f1f5f9",
  },
  edge: {
    ...lightTheme.edge,
    fill: "#94a3b8",
    activeFill: "#6366f1",
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
      color: "#334155",
      fontSize: 11,
    },
    activeFill: "#6366f1",
  },
};
```

- [ ] **Step 2: Replace `theme={lightTheme}` with `theme={customTheme}` in `<GraphCanvas>`**

Find:
```tsx
theme={lightTheme}
```
Replace with:
```tsx
theme={customTheme}
```

- [ ] **Step 3: Verify TypeScript compiles**

```bash
cd admin-ui && npx tsc --noEmit
```
Expected: no errors.

---

### Task 2: Hide Edge Labels

**Files:**
- Modify: `admin-ui/app/(authed)/workspaces/[slug]/graph/graph-canvas.tsx`

- [ ] **Step 1: Add `labelVisible: false` to each edge in `rgEdges` memo**

Find the `rgEdges` useMemo:
```ts
const rgEdges: GraphEdge[] = React.useMemo(
  () =>
    data.edges.map((e) => ({
      id: e.id,
      source: e.source,
      target: e.target,
      label: e.label,
    })),
  [data.edges]
);
```

Replace with:
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

- [ ] **Step 2: Verify TypeScript compiles**

```bash
npx tsc --noEmit
```
Expected: no errors.

---

### Task 3: Hover State + Tooltip

**Files:**
- Modify: `admin-ui/app/(authed)/workspaces/[slug]/graph/graph-canvas.tsx`

- [ ] **Step 1: Add hover state inside `GraphCanvasView` component**

Find the existing state declarations block (around line 76–86). After:
```ts
const [detailError, setDetailError] = React.useState<string | null>(null);
```

Add:
```ts
const [hoveredId, setHoveredId] = React.useState<string | null>(null);
const [tooltipPos, setTooltipPos] = React.useState<{ x: number; y: number } | null>(null);
```

- [ ] **Step 2: Update `rgNodes` memo to bump size on hover**

Find:
```ts
const rgNodes: GraphNode[] = React.useMemo(
  () =>
    data.nodes.map((n) => ({
      id: n.id,
      label: n.name ?? n.label,
      fill: nodeColor(n.label),
      data: { label: n.label, name: n.name },
    })),
  [data.nodes]
);
```

Replace with:
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

- [ ] **Step 3: Add hover callbacks to `<GraphCanvas>` and update `onNodeClick`**

Find the existing `<GraphCanvas>` block:
```tsx
<GraphCanvas
  nodes={rgNodes}
  edges={rgEdges}
  layoutType="forceDirected2d"
  theme={customTheme}
  selections={selections}
  actives={searchHitIds}
  onNodeClick={(node) => {
    setSelectedId((prev) => (prev === node.id ? null : node.id));
  }}
  onCanvasClick={() => setSelectedId(null)}
/>
```

Replace with:
```tsx
<GraphCanvas
  nodes={rgNodes}
  edges={rgEdges}
  layoutType="forceDirected2d"
  theme={customTheme}
  selections={selections}
  actives={searchHitIds}
  onNodePointerOver={(node, event) => {
    setHoveredId(node.id);
    setTooltipPos({ x: event.nativeEvent.clientX, y: event.nativeEvent.clientY });
  }}
  onNodePointerOut={() => {
    setHoveredId(null);
    setTooltipPos(null);
  }}
  onNodeClick={(node) => {
    setHoveredId(null);
    setTooltipPos(null);
    setSelectedId((prev) => (prev === node.id ? null : node.id));
  }}
  onCanvasClick={() => setSelectedId(null)}
/>
```

- [ ] **Step 4: Add tooltip markup in the outer wrapper div**

Find the closing tag of the **outer** wrapper div (the div with `className="relative flex h-[calc(100vh-180px)] min-h-[500px] w-full gap-0"`). Before its closing `</div>`, add:

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

- [ ] **Step 5: Verify TypeScript compiles**

```bash
npx tsc --noEmit
```
Expected: no errors.

- [ ] **Step 6: Start dev server and visually verify**

```bash
npm run dev
```

Open the graph page. Check:
- Canvas background is slate-100 (light gray, not white)
- Edge labels are gone
- Hovering a node makes it grow and shows tooltip with colored dot, type, name
- Clicking a node clears the tooltip and opens the detail panel
- Search highlighting still works (search for a node name — matching nodes activate)

- [ ] **Step 7: Commit**

```bash
cd .. && git add admin-ui/app/\(authed\)/workspaces/\[slug\]/graph/graph-canvas.tsx
git commit -m "feat(graph): polish canvas with custom theme, hover tooltip, hidden edge labels"
```
