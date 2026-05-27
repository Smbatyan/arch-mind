"use client";

import * as React from "react";
import { GraphCanvas, lightTheme } from "reagraph";
import type { GraphNode, GraphEdge, GraphCanvasRef } from "reagraph";

import { api } from "@/lib/api";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------
export type VisNode = { id: string; label: string; name: string | null; repoId?: string | null };
export type VisEdge = {
  id: string;
  source: string;
  target: string;
  label: string;
};
export type VisData = {
  nodes: VisNode[];
  edges: VisEdge[];
  truncated: boolean;
};

type NodeDetail = {
  id: string;
  label: string;
  properties: Record<string, unknown>;
  incomingEdges: EdgeRef[];
  outgoingEdges: EdgeRef[];
};
type EdgeRef = {
  label: string;
  otherNodeId: string;
  otherNodeLabel: string;
  otherNodeName: string | null;
  properties: Record<string, unknown>;
};

// ---------------------------------------------------------------------------
// Per-label fill colours (hex — reagraph expects CSS colour strings)
// ---------------------------------------------------------------------------
const LABEL_COLOR: Record<string, string> = {
  Service: "#3b82f6",
  Endpoint: "#14b8a6",
  Database: "#a855f7",
  Queue: "#6366f1",
  Event: "#f97316",
  Concept: "#ef4444",
  File: "#6b7280",
  Convention: "#eab308",
  Capability: "#ec4899",
  Storage: "#22c55e",
};
const DEFAULT_COLOR = "#94a3b8";

// Repo-mode palette (stable order so colour stays consistent across reloads).
const REPO_PALETTE = [
  "#4e79a7",
  "#f28e2b",
  "#59a14f",
  "#e15759",
  "#76b7b2",
  "#edc948",
  "#b07aa1",
  "#ff9da7",
  "#9c755f",
  "#bab0ac",
];
const NO_REPO_COLOR = "#cbd5e1";

type RepoSummary = { id: string; name: string };

const customTheme = {
  ...lightTheme,
  canvas: {
    ...lightTheme.canvas,
    background: "#f1f5f9",
    fog: null,
  },
  edge: {
    ...lightTheme.edge,
    fill: "#64748b",
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

function nodeColor(label: string): string {
  return LABEL_COLOR[label] ?? DEFAULT_COLOR;
}

function repoColor(repoId: string | null | undefined, repoIndex: Map<string, number>): string {
  if (!repoId) return NO_REPO_COLOR;
  const idx = repoIndex.get(repoId);
  if (idx === undefined) return NO_REPO_COLOR;
  return REPO_PALETTE[idx % REPO_PALETTE.length];
}

// ---------------------------------------------------------------------------
// Main component
// ---------------------------------------------------------------------------
export function GraphCanvasView({
  slug,
  data,
  defaultColorMode = "label",
  highlightedNodeIds,
}: {
  slug: string;
  data: VisData;
  defaultColorMode?: "label" | "repo";
  highlightedNodeIds?: string[];
}) {
  // ---- search ----
  const [query, setQuery] = React.useState("");
  const [debouncedQuery, setDebouncedQuery] = React.useState("");
  const [searchHitIds, setSearchHitIds] = React.useState<string[]>([]);
  const [searching, setSearching] = React.useState(false);

  // ---- selection / detail ----
  const [selectedId, setSelectedId] = React.useState<string | null>(null);
  const [detail, setDetail] = React.useState<NodeDetail | null>(null);
  const [detailLoading, setDetailLoading] = React.useState(false);
  const [detailError, setDetailError] = React.useState<string | null>(null);
  const [hoveredId, setHoveredId] = React.useState<string | null>(null);
  const [tooltipPos, setTooltipPos] = React.useState<{ x: number; y: number } | null>(null);
  const canvasRef = React.useRef<GraphCanvasRef | null>(null);

  // ---- repo colouring + neighbour isolation ----
  const [colorMode, setColorMode] = React.useState<"label" | "repo">(defaultColorMode);
  const [isolatedId, setIsolatedId] = React.useState<string | null>(null);
  const [repos, setRepos] = React.useState<RepoSummary[]>([]);

  React.useEffect(() => {
    let cancelled = false;
    api<RepoSummary[]>(`/api/workspaces/${slug}/repos`)
      .then((rs) => {
        if (!cancelled) setRepos(rs.map((r) => ({ id: r.id, name: r.name })));
      })
      .catch(() => {
        // Non-fatal: repo-colour mode just falls back to grey.
      });
    return () => {
      cancelled = true;
    };
  }, [slug]);

  // Stable repo → palette-index map (sorted by name for deterministic colour).
  const repoIndex = React.useMemo(() => {
    const ids = Array.from(new Set(data.nodes.map((n) => n.repoId).filter((v): v is string => !!v)));
    const named = ids
      .map((id) => ({ id, name: repos.find((r) => r.id === id)?.name ?? id }))
      .sort((a, b) => a.name.localeCompare(b.name));
    return new Map(named.map((r, i) => [r.id, i] as const));
  }, [data.nodes, repos]);

  // Set of node ids visible in current isolation view (self + direct neighbours).
  const isolatedSet = React.useMemo(() => {
    if (!isolatedId) return null;
    const keep = new Set<string>([isolatedId]);
    for (const e of data.edges) {
      if (e.source === isolatedId) keep.add(e.target);
      if (e.target === isolatedId) keep.add(e.source);
    }
    return keep;
  }, [isolatedId, data.edges]);

  // Debounce the search query 300 ms
  React.useEffect(() => {
    const t = setTimeout(() => setDebouncedQuery(query.trim()), 300);
    return () => clearTimeout(t);
  }, [query]);

  // Fire search when debounced query changes
  React.useEffect(() => {
    if (!debouncedQuery) {
      setSearchHitIds([]);
      return;
    }
    let cancelled = false;
    setSearching(true);
    api<{ id: string; label: string; name: string | null }[]>(
      `/api/workspaces/${slug}/graph/search?q=${encodeURIComponent(debouncedQuery)}&limit=100`
    )
      .then((hits) => {
        if (cancelled) return;
        setSearchHitIds(hits.map((h) => h.id));
        setSearching(false);
      })
      .catch(() => {
        if (cancelled) return;
        setSearchHitIds([]);
        setSearching(false);
      });
    return () => {
      cancelled = true;
    };
  }, [slug, debouncedQuery]);

  // Auto-fit all nodes into view when data loads/changes
  React.useEffect(() => {
    if (data.nodes.length === 0) return;
    const t = setTimeout(() => canvasRef.current?.fitNodesInView?.(), 600);
    return () => clearTimeout(t);
  }, [data.nodes.length]);

  // Load node detail when selection changes
  React.useEffect(() => {
    if (!selectedId) {
      setDetail(null);
      return;
    }
    const node = data.nodes.find((n) => n.id === selectedId);
    if (!node) return;

    let cancelled = false;
    setDetailLoading(true);
    setDetailError(null);

    api<NodeDetail>(
      `/api/workspaces/${slug}/graph/nodes/${encodeURIComponent(node.label)}/${encodeURIComponent(node.id)}`
    )
      .then((d) => {
        if (cancelled) return;
        setDetail(d);
        setDetailLoading(false);
      })
      .catch((err) => {
        if (cancelled) return;
        setDetailError(err instanceof Error ? err.message : "Failed to load.");
        setDetailLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [slug, selectedId, data.nodes]);

  // Build reagraph-shaped arrays
  const degreeMap = React.useMemo(() => {
    const map = new Map<string, number>();
    for (const e of data.edges) {
      map.set(e.source, (map.get(e.source) ?? 0) + 1);
      map.set(e.target, (map.get(e.target) ?? 0) + 1);
    }
    return map;
  }, [data.edges]);

  const maxDegree = React.useMemo(
    () => Math.max(1, ...(degreeMap.size > 0 ? degreeMap.values() : [1])),
    [degreeMap]
  );

  const rgNodes: GraphNode[] = React.useMemo(
    () =>
      data.nodes
        .filter((n) => !isolatedSet || isolatedSet.has(n.id))
        .map((n) => {
          const degree = degreeMap.get(n.id) ?? 0;
          const baseSize = 4 + Math.round((degree / maxDegree) * 8);
          const size = n.id === hoveredId ? baseSize + 3 : baseSize;
          const fill =
            colorMode === "repo"
              ? repoColor(n.repoId, repoIndex)
              : nodeColor(n.label);
          return {
            id: n.id,
            label: n.name ?? n.label,
            fill,
            size,
            data: { label: n.label, name: n.name },
          };
        }),
    [data.nodes, hoveredId, degreeMap, maxDegree, colorMode, repoIndex, isolatedSet]
  );

  const rgEdges: GraphEdge[] = React.useMemo(
    () =>
      data.edges
        .filter((e) => !isolatedSet || (isolatedSet.has(e.source) && isolatedSet.has(e.target)))
        .map((e) => ({
          id: e.id,
          source: e.source,
          target: e.target,
          label: e.label,
          labelVisible: false,
        })),
    [data.edges, isolatedSet]
  );

  const selections = React.useMemo(
    () => (selectedId ? [selectedId] : []),
    [selectedId]
  );

  return (
    <div className="relative flex h-[calc(100vh-180px)] min-h-[500px] w-full gap-0">
      {/* Canvas */}
      <div className="relative flex-1 overflow-hidden rounded-md border border-border">
        {/* Search bar */}
        <div className="absolute left-3 right-3 top-3 z-10 flex items-center gap-2">
          <Input
            type="search"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search nodes…"
            className="h-8 bg-background/90 text-sm backdrop-blur"
          />
          {searching && (
            <span className="shrink-0 text-xs text-muted-foreground">…</span>
          )}
          {searchHitIds.length > 0 && !searching && (
            <Badge variant="secondary" className="shrink-0">
              {searchHitIds.length}
            </Badge>
          )}
        </div>

        {/* Colour-mode toggle */}
        <div className="absolute right-3 top-14 z-10 flex gap-1 rounded-md border border-border bg-background/90 p-0.5 text-xs backdrop-blur">
          {(["label", "repo"] as const).map((m) => (
            <button
              key={m}
              type="button"
              onClick={() => setColorMode(m)}
              className={`rounded px-2 py-0.5 capitalize transition-colors ${
                colorMode === m
                  ? "bg-muted text-foreground"
                  : "text-muted-foreground hover:text-foreground"
              }`}
            >
              {m}
            </button>
          ))}
        </div>

        {/* Isolation banner */}
        {isolatedId && (
          <div className="absolute left-1/2 top-14 z-10 flex -translate-x-1/2 items-center gap-2 rounded-md border border-border bg-background/95 px-2.5 py-1 text-xs backdrop-blur">
            <span className="text-muted-foreground">
              Showing neighbours of {data.nodes.find((n) => n.id === isolatedId)?.name ?? isolatedId.slice(0, 8)}
            </span>
            <button
              type="button"
              onClick={() => setIsolatedId(null)}
              className="rounded border border-border px-1.5 py-0.5 text-[0.65rem] uppercase tracking-wide text-muted-foreground hover:bg-muted hover:text-foreground"
            >
              clear
            </button>
          </div>
        )}

        {/* Legend */}
        <div className="absolute bottom-3 left-3 z-10 flex flex-col gap-1 rounded-md border border-border bg-background/90 px-2 py-1.5 backdrop-blur">
          {colorMode === "label"
            ? Object.entries(LABEL_COLOR).map(([label, color]) => {
                const count = data.nodes.filter((n) => n.label === label).length;
                if (count === 0) return null;
                return (
                  <div key={label} className="flex items-center gap-1.5">
                    <span
                      className="inline-block h-2 w-2 shrink-0 rounded-full"
                      style={{ backgroundColor: color }}
                    />
                    <span className="text-[0.65rem] text-muted-foreground">
                      {label}
                      <span className="ml-1 opacity-60">{count}</span>
                    </span>
                  </div>
                );
              })
            : Array.from(repoIndex.entries())
                .sort((a, b) => a[1] - b[1])
                .map(([repoId]) => {
                  const name = repos.find((r) => r.id === repoId)?.name ?? repoId.slice(0, 8);
                  const count = data.nodes.filter((n) => n.repoId === repoId).length;
                  return (
                    <div key={repoId} className="flex items-center gap-1.5">
                      <span
                        className="inline-block h-2 w-2 shrink-0 rounded-full"
                        style={{ backgroundColor: repoColor(repoId, repoIndex) }}
                      />
                      <span className="text-[0.65rem] text-muted-foreground">
                        {name}
                        <span className="ml-1 opacity-60">{count}</span>
                      </span>
                    </div>
                  );
                })}
        </div>

        {/* Zoom controls */}
        <div className="absolute bottom-3 right-3 z-10 flex flex-col gap-0.5">
          <button
            type="button"
            onClick={() => canvasRef.current?.fitNodesInView?.()}
            title="Fit all nodes"
            className="flex h-7 w-7 items-center justify-center rounded-md border border-border bg-background/90 text-xs font-medium text-muted-foreground backdrop-blur transition-colors hover:bg-muted hover:text-foreground"
          >
            ⊡
          </button>
          <button
            type="button"
            onClick={() => canvasRef.current?.zoomIn()}
            className="flex h-7 w-7 items-center justify-center rounded-md border border-border bg-background/90 text-sm font-medium text-muted-foreground backdrop-blur transition-colors hover:bg-muted hover:text-foreground"
          >
            +
          </button>
          <button
            type="button"
            onClick={() => canvasRef.current?.zoomOut()}
            className="flex h-7 w-7 items-center justify-center rounded-md border border-border bg-background/90 text-sm font-medium text-muted-foreground backdrop-blur transition-colors hover:bg-muted hover:text-foreground"
          >
            −
          </button>
        </div>

        {data.truncated && (
          <div className="absolute bottom-3 right-14 z-10 rounded-md border border-border bg-background/90 px-2 py-1 text-[0.65rem] text-muted-foreground backdrop-blur">
            Showing first 500 nodes
          </div>
        )}

        <GraphCanvas
          ref={canvasRef}
          nodes={rgNodes}
          edges={rgEdges}
          layoutType="forceDirected2d"
          theme={customTheme}
          selections={selections}
          actives={
            highlightedNodeIds && highlightedNodeIds.length > 0
              ? highlightedNodeIds
              : searchHitIds.length > 0
                ? searchHitIds
                : undefined
          }
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
          onNodeDoubleClick={(node) => {
            setHoveredId(null);
            setTooltipPos(null);
            setIsolatedId((prev) => (prev === node.id ? null : node.id));
          }}
          onCanvasClick={() => setSelectedId(null)}
        />
      </div>

      {/* Hover tooltip */}
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
            {node.repoId && (
              <span className="text-[0.6rem] text-muted-foreground opacity-70">
                repo:{node.repoId.slice(0, 8)}
              </span>
            )}
          </div>
        );
      })()}

      {/* Detail panel */}
      {selectedId && (
        <aside className="ml-3 flex w-72 shrink-0 flex-col gap-3 overflow-y-auto rounded-md border border-border bg-card/60 p-3">
          {detailLoading ? (
            <p className="text-xs text-muted-foreground">Loading…</p>
          ) : detailError ? (
            <Alert variant="destructive">
              <AlertDescription>{detailError}</AlertDescription>
            </Alert>
          ) : detail ? (
            <DetailCard
              detail={detail}
              onNavigate={(label, id) => setSelectedId(id)}
            />
          ) : null}
        </aside>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Detail card (self-contained, no scroll context dependency)
// ---------------------------------------------------------------------------
function DetailCard({
  detail,
  onNavigate,
}: {
  detail: NodeDetail;
  onNavigate: (label: string, id: string) => void;
}) {
  const name =
    typeof detail.properties.name === "string"
      ? detail.properties.name
      : detail.id.slice(0, 8);

  const entries = Object.entries(detail.properties)
    .filter(([k]) => k !== "workspace_id")
    .sort(([a], [b]) => a.localeCompare(b));

  return (
    <>
      <header className="flex flex-col gap-1">
        <div className="flex flex-wrap items-center gap-2">
          <span
            className="inline-block h-2.5 w-2.5 shrink-0 rounded-full"
            style={{ backgroundColor: nodeColor(detail.label) }}
          />
          <Badge variant="secondary">{detail.label}</Badge>
        </div>
        <p className="font-medium leading-snug">{name}</p>
        <p className="font-mono text-[0.65rem] text-muted-foreground break-all">
          {detail.id}
        </p>
      </header>

      <section className="flex flex-col gap-1">
        <h3 className="text-[0.65rem] font-medium uppercase tracking-wide text-muted-foreground">
          Properties
        </h3>
        <table className="w-full table-fixed border-collapse text-xs">
          <tbody>
            {entries.map(([k, v]) => (
              <tr key={k} className="border-b border-border/50">
                <td className="w-2/5 py-1 pr-2 align-top font-mono text-[0.65rem] text-muted-foreground">
                  {k}
                </td>
                <td className="break-words py-1 align-top">
                  {formatVal(v)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      <EdgeSection
        title="Outgoing"
        edges={detail.outgoingEdges}
        onNavigate={onNavigate}
      />
      <EdgeSection
        title="Incoming"
        edges={detail.incomingEdges}
        onNavigate={onNavigate}
      />
    </>
  );
}

function EdgeSection({
  title,
  edges,
  onNavigate,
}: {
  title: string;
  edges: EdgeRef[];
  onNavigate: (label: string, id: string) => void;
}) {
  if (edges.length === 0) return null;
  return (
    <section className="flex flex-col gap-1">
      <h3 className="text-[0.65rem] font-medium uppercase tracking-wide text-muted-foreground">
        {title}
      </h3>
      <ul className="flex flex-col gap-0.5">
        {edges.map((e, i) => (
          <li key={`${e.label}-${e.otherNodeId}-${i}`}>
            <Button
              variant="ghost"
              size="sm"
              className="h-auto w-full justify-start gap-1.5 px-1 py-1 text-left text-xs"
              onClick={() => onNavigate(e.otherNodeLabel, e.otherNodeId)}
            >
              <Badge variant="outline" className="font-mono text-[0.6rem]">
                {e.label}
              </Badge>
              <span
                className="inline-block h-1.5 w-1.5 shrink-0 rounded-full"
                style={{ backgroundColor: nodeColor(e.otherNodeLabel) }}
              />
              <span className="truncate">
                {e.otherNodeName ?? e.otherNodeId.slice(0, 8)}
              </span>
            </Button>
          </li>
        ))}
      </ul>
    </section>
  );
}

function formatVal(v: unknown): string {
  if (v === null || v === undefined) return "—";
  if (typeof v === "string") return v;
  if (typeof v === "number" || typeof v === "boolean") return String(v);
  try {
    return JSON.stringify(v);
  } catch {
    return String(v);
  }
}
