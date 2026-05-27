"use client";

import dynamic from "next/dynamic";
import Link from "next/link";
import * as React from "react";

import { api } from "@/lib/api";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { NodeClarificationsPanel } from "@/components/node-clarifications-panel";
import type { VisData } from "./graph-canvas";

// WebGL canvas — no SSR (Three.js requires browser)
const GraphCanvasView = dynamic(
  () => import("./graph-canvas").then((m) => ({ default: m.GraphCanvasView })),
  { ssr: false, loading: () => <CanvasPlaceholder text="Loading canvas…" /> }
);

// ---------------------------------------------------------------------------
// Types — mirror the backend DTOs from GraphEndpoints.cs.
// ---------------------------------------------------------------------------
export type LabelCount = { label: string; count: number };

export type GraphLabelsResponse = {
  vertices: LabelCount[];
  edges: LabelCount[];
};

type NodeSummary = {
  id: string;
  label: string;
  name: string | null;
};

type EdgeRef = {
  label: string;
  otherNodeId: string;
  otherNodeLabel: string;
  otherNodeName: string | null;
  properties: Record<string, unknown>;
};

type NodeDetail = {
  id: string;
  label: string;
  properties: Record<string, unknown>;
  incomingEdges: EdgeRef[];
  outgoingEdges: EdgeRef[];
};

// Hard cap matches the backend default; pagination is a Sprint 4 follow-up.
const NODE_LIMIT = 200;

function CanvasPlaceholder({ text }: { text: string }) {
  return (
    <div className="flex h-[500px] items-center justify-center rounded-md border border-dashed border-border text-sm text-muted-foreground">
      {text}
    </div>
  );
}

function shortId(id: string): string {
  return id.length > 8 ? id.slice(0, 8) : id;
}

function formatPropertyValue(value: unknown): string {
  if (value === null || value === undefined) return "—";
  if (typeof value === "string") return value;
  if (typeof value === "number" || typeof value === "boolean") {
    return String(value);
  }
  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

// ---------------------------------------------------------------------------
// Root component — tab bar + two modes (List / Visual).
// ---------------------------------------------------------------------------
type Tab = "list" | "visual" | "structural";

export function GraphBrowser({
  slug,
  initialLabels,
}: {
  slug: string;
  initialLabels: GraphLabelsResponse;
}) {
  const [tab, setTab] = React.useState<Tab>("visual");

  // List-mode state
  const [selectedLabel, setSelectedLabel] = React.useState<string | null>(null);
  const [selectedNode, setSelectedNode] = React.useState<
    { label: string; id: string } | null
  >(null);

  // Visual-mode data
  const [visData, setVisData] = React.useState<VisData | null>(null);
  const [visLoading, setVisLoading] = React.useState(false);
  const [visError, setVisError] = React.useState<string | null>(null);

  // Structural-mode data + search
  const [structData, setStructData] = React.useState<VisData | null>(null);
  const [structLoading, setStructLoading] = React.useState(false);
  const [structError, setStructError] = React.useState<string | null>(null);
  const [structQuery, setStructQuery] = React.useState("");
  const [structHitIds, setStructHitIds] = React.useState<string[]>([]);
  const [structKeywords, setStructKeywords] = React.useState<string[]>([]);
  const [structSearching, setStructSearching] = React.useState(false);
  const [structSearchError, setStructSearchError] = React.useState<string | null>(null);

  const handleSelectLabel = React.useCallback((label: string) => {
    setSelectedLabel(label);
    setSelectedNode(null);
  }, []);

  const handleSelectNode = React.useCallback(
    (label: string, id: string) => {
      setSelectedLabel((current) => current ?? label);
      setSelectedNode({ label, id });
    },
    []
  );

  // Lazy-load visualization data on first switch to visual tab
  const visLoaded = React.useRef(false);
  React.useEffect(() => {
    if (tab !== "visual" || visLoaded.current) return;
    visLoaded.current = true;
    setVisLoading(true);
    api<VisData>(`/api/workspaces/${slug}/graph/visualization?limit=500`)
      .then((d) => {
        setVisData(d);
        setVisLoading(false);
      })
      .catch((err) => {
        setVisError(err instanceof Error ? err.message : "Failed to load graph.");
        setVisLoading(false);
      });
  }, [tab, slug]);

  // Lazy-load structural (AST) data on first switch to structural tab.
  // Backend shape: StructuralDataDto → maps directly to VisData (id, label, name, repoId).
  const structLoaded = React.useRef(false);
  React.useEffect(() => {
    if (tab !== "structural" || structLoaded.current) return;
    structLoaded.current = true;
    setStructLoading(true);
    type StructuralNodeDto = {
      id: string;
      label: string;
      name: string;
      repoId: string | null;
      sourceFile: string | null;
      degree: number;
    };
    type StructuralEdgeDto = { id: string; source: string; target: string; label: string };
    type StructuralDataDto = {
      nodes: StructuralNodeDto[];
      edges: StructuralEdgeDto[];
      totalNodes: number;
      totalEdges: number;
      truncated: boolean;
    };
    api<StructuralDataDto>(`/api/workspaces/${slug}/graph/structural?limit=500`)
      .then((d) => {
        setStructData({
          nodes: d.nodes.map((n) => ({
            id: n.id,
            label: n.label,
            name: n.name,
            repoId: n.repoId,
          })),
          edges: d.edges,
          truncated: d.truncated,
        });
        setStructLoading(false);
      })
      .catch((err) => {
        setStructError(err instanceof Error ? err.message : "Failed to load graph.");
        setStructLoading(false);
      });
  }, [tab, slug]);

  // Submit NL search: one Haiku call → keywords + matched node IDs.
  const runStructuralSearch = React.useCallback(async () => {
    const q = structQuery.trim();
    if (!q) {
      setStructHitIds([]);
      setStructKeywords([]);
      setStructSearchError(null);
      return;
    }
    setStructSearching(true);
    setStructSearchError(null);
    try {
      const res = await api<{ keywords: string[]; nodeIds: string[] }>(
        `/api/workspaces/${slug}/graph/structural/search`,
        { method: "POST", body: JSON.stringify({ q }) }
      );
      setStructKeywords(res.keywords ?? []);
      setStructHitIds(res.nodeIds ?? []);
    } catch (err) {
      setStructSearchError(err instanceof Error ? err.message : "Search failed.");
      setStructHitIds([]);
    } finally {
      setStructSearching(false);
    }
  }, [slug, structQuery]);

  const graphIsEmpty =
    initialLabels.vertices.length === 0 && initialLabels.edges.length === 0;

  if (graphIsEmpty) {
    return (
      <div className="flex w-full justify-center px-4 py-12 md:px-6">
        <Card className="w-full max-w-md">
          <CardHeader>
            <CardTitle>Graph is empty</CardTitle>
            <CardDescription>Run a scan to populate the graph.</CardDescription>
          </CardHeader>
          <CardContent>
            <Link
              href={`/workspaces/${slug}/dashboard`}
              className="inline-flex h-8 items-center justify-center rounded-lg border border-border bg-background px-2.5 text-sm font-medium transition-colors hover:bg-muted hover:text-foreground"
            >
              Go to dashboard
            </Link>
          </CardContent>
        </Card>
      </div>
    );
  }

  return (
    <div className="flex w-full flex-col gap-4 px-4 md:px-6">
      {/* Tab bar */}
      <div className="flex gap-1 rounded-md border border-border bg-muted/40 p-0.5 w-fit">
        {(["visual", "structural", "list"] as Tab[]).map((t) => (
          <button
            key={t}
            type="button"
            onClick={() => setTab(t)}
            className={`rounded px-3 py-1 text-sm font-medium capitalize transition-colors ${
              tab === t
                ? "bg-background text-foreground shadow-sm"
                : "text-muted-foreground hover:text-foreground"
            }`}
          >
            {t === "list" ? "List" : t === "visual" ? "Semantic" : "Structural"}
          </button>
        ))}
      </div>

      {/* List mode */}
      {tab === "list" && (
        <div className="grid min-h-[600px] w-full grid-cols-1 gap-4 lg:grid-cols-[18rem_22rem_minmax(0,1fr)]">
          <LabelsPane
            labels={initialLabels}
            selectedLabel={selectedLabel}
            onSelect={handleSelectLabel}
          />
          <NodesPane
            slug={slug}
            label={selectedLabel}
            labelCount={
              selectedLabel
                ? initialLabels.vertices.find((v) => v.label === selectedLabel)?.count ?? 0
                : 0
            }
            selectedNodeId={selectedNode?.id ?? null}
            onSelectNode={handleSelectNode}
          />
          <DetailPane
            slug={slug}
            selectedNode={selectedNode}
            onSelectNode={handleSelectNode}
          />
        </div>
      )}

      {/* Visual mode */}
      {tab === "visual" && (
        <div className="w-full">
          {visLoading && <CanvasPlaceholder text="Loading graph data…" />}
          {visError && (
            <Alert variant="destructive">
              <AlertDescription>{visError}</AlertDescription>
            </Alert>
          )}
          {visData && <GraphCanvasView slug={slug} data={visData} />}
        </div>
      )}

      {/* Structural mode (AST from graphify, no LLM extraction) */}
      {tab === "structural" && (
        <div className="flex w-full flex-col gap-2">
          {/* NL search bar */}
          <div className="flex flex-wrap items-center gap-2 rounded-md border border-border bg-card/40 p-2">
            <Input
              type="search"
              value={structQuery}
              onChange={(e) => setStructQuery(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  e.preventDefault();
                  void runStructuralSearch();
                }
              }}
              placeholder="Ask in plain English (e.g. “where is auth handled?”) — one cheap Haiku call per search"
              className="h-8 flex-1 min-w-[260px] text-sm"
            />
            <Button
              type="button"
              size="sm"
              onClick={() => void runStructuralSearch()}
              disabled={structSearching || !structQuery.trim()}
            >
              {structSearching ? "Searching…" : "Search"}
            </Button>
            {structKeywords.length > 0 && (
              <div className="flex flex-wrap items-center gap-1 text-xs text-muted-foreground">
                <span>Keywords:</span>
                {structKeywords.map((k) => (
                  <Badge key={k} variant="outline" className="font-mono text-[0.65rem]">
                    {k}
                  </Badge>
                ))}
                <span className="ml-1 opacity-70">
                  {structHitIds.length} match{structHitIds.length === 1 ? "" : "es"}
                </span>
              </div>
            )}
            {(structHitIds.length > 0 || structKeywords.length > 0) && (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => {
                  setStructQuery("");
                  setStructHitIds([]);
                  setStructKeywords([]);
                  setStructSearchError(null);
                }}
              >
                Clear
              </Button>
            )}
          </div>
          {structSearchError && (
            <Alert variant="destructive">
              <AlertDescription>{structSearchError}</AlertDescription>
            </Alert>
          )}

          {structLoading && <CanvasPlaceholder text="Loading structural graph…" />}
          {structError && (
            <Alert variant="destructive">
              <AlertDescription>{structError}</AlertDescription>
            </Alert>
          )}
          {structData && (
            <GraphCanvasView
              slug={slug}
              data={structData}
              defaultColorMode="repo"
              highlightedNodeIds={structHitIds.length > 0 ? structHitIds : undefined}
            />
          )}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Left pane — vertex labels + (read-only) edge labels.
// ---------------------------------------------------------------------------
function LabelsPane({
  labels,
  selectedLabel,
  onSelect,
}: {
  labels: GraphLabelsResponse;
  selectedLabel: string | null;
  onSelect: (label: string) => void;
}) {
  const [filter, setFilter] = React.useState("");
  const needle = filter.trim().toLowerCase();

  const matches = React.useCallback(
    (label: string) =>
      needle.length === 0 || label.toLowerCase().includes(needle),
    [needle]
  );

  const vertices = labels.vertices.filter((v) => matches(v.label));
  const edges = labels.edges.filter((e) => matches(e.label));

  return (
    <aside className="flex flex-col gap-3 rounded-md border border-border bg-card/40 p-3">
      <Input
        type="search"
        value={filter}
        onChange={(e) => setFilter(e.target.value)}
        placeholder="Filter labels"
        aria-label="Filter labels"
      />

      <div className="flex flex-col gap-1">
        <h2 className="px-1 text-xs font-medium uppercase tracking-wide text-muted-foreground">
          Vertices
        </h2>
        {vertices.length === 0 ? (
          <p className="px-1 py-2 text-xs text-muted-foreground">No labels.</p>
        ) : (
          <ul className="flex flex-col gap-0.5">
            {vertices.map((v) => {
              const active = v.label === selectedLabel;
              return (
                <li key={v.label}>
                  <button
                    type="button"
                    onClick={() => onSelect(v.label)}
                    className={`flex w-full items-center justify-between rounded-md px-2 py-1.5 text-left text-sm transition-colors ${
                      active
                        ? "bg-muted text-foreground"
                        : "text-muted-foreground hover:bg-muted hover:text-foreground"
                    }`}
                  >
                    <span className="truncate">{v.label}</span>
                    <Badge variant="secondary" className="shrink-0">
                      {v.count}
                    </Badge>
                  </button>
                </li>
              );
            })}
          </ul>
        )}
      </div>

      <div className="flex flex-col gap-1">
        <h2 className="px-1 text-xs font-medium uppercase tracking-wide text-muted-foreground">
          Edges
        </h2>
        {edges.length === 0 ? (
          <p className="px-1 py-2 text-xs text-muted-foreground">
            No edge labels.
          </p>
        ) : (
          <ul className="flex flex-col gap-0.5">
            {edges.map((e) => (
              <li
                key={e.label}
                className="flex items-center justify-between px-2 py-1.5 text-sm text-muted-foreground"
              >
                <span className="truncate">{e.label}</span>
                <Badge variant="outline" className="shrink-0">
                  {e.count}
                </Badge>
              </li>
            ))}
          </ul>
        )}
      </div>
    </aside>
  );
}

// ---------------------------------------------------------------------------
// Center pane — node list for the selected label.
// ---------------------------------------------------------------------------
function NodesPane({
  slug,
  label,
  labelCount,
  selectedNodeId,
  onSelectNode,
}: {
  slug: string;
  label: string | null;
  labelCount: number;
  selectedNodeId: string | null;
  onSelectNode: (label: string, id: string) => void;
}) {
  const [nodes, setNodes] = React.useState<NodeSummary[]>([]);
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [filter, setFilter] = React.useState("");

  React.useEffect(() => {
    if (!label) {
      setNodes([]);
      setError(null);
      setFilter("");
      return;
    }

    let cancelled = false;
    setLoading(true);
    setError(null);
    setFilter("");

    api<NodeSummary[]>(
      `/api/workspaces/${slug}/graph/nodes?label=${encodeURIComponent(label)}&limit=${NODE_LIMIT}`
    )
      .then((res) => {
        if (cancelled) return;
        setNodes(res);
        setLoading(false);
      })
      .catch((err) => {
        if (cancelled) return;
        setError(err instanceof Error ? err.message : "Failed to load nodes.");
        setNodes([]);
        setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [slug, label]);

  if (!label) {
    return (
      <section className="flex min-h-[200px] flex-col items-center justify-center rounded-md border border-dashed border-border p-6 text-center text-sm text-muted-foreground">
        Select a label on the left to browse nodes.
      </section>
    );
  }

  const needle = filter.trim().toLowerCase();
  const visible =
    needle.length === 0
      ? nodes
      : nodes.filter((n) => {
          const haystack = (n.name ?? n.id).toLowerCase();
          return haystack.includes(needle);
        });

  return (
    <section className="flex flex-col gap-3 rounded-md border border-border bg-card/40 p-3">
      <div className="flex items-center justify-between gap-2">
        <div className="flex flex-col">
          <h2 className="text-sm font-medium">{label}</h2>
          <p className="text-xs text-muted-foreground">
            {labelCount} {labelCount === 1 ? "node" : "nodes"} total
          </p>
        </div>
      </div>

      <Input
        type="search"
        value={filter}
        onChange={(e) => setFilter(e.target.value)}
        placeholder="Filter by name"
        aria-label="Filter nodes by name"
      />

      {loading ? (
        <p className="px-1 py-4 text-xs text-muted-foreground">Loading…</p>
      ) : error ? (
        <Alert variant="destructive">
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : nodes.length === 0 ? (
        <p className="px-1 py-4 text-xs text-muted-foreground">
          No nodes with this label yet.
        </p>
      ) : visible.length === 0 ? (
        <p className="px-1 py-4 text-xs text-muted-foreground">
          No nodes match the filter.
        </p>
      ) : (
        <ul className="flex max-h-[60vh] flex-col gap-0.5 overflow-y-auto pr-1">
          {visible.map((n) => {
            const active = n.id === selectedNodeId;
            return (
              <li key={n.id}>
                <button
                  type="button"
                  onClick={() => onSelectNode(n.label, n.id)}
                  className={`flex w-full flex-col items-start rounded-md px-2 py-1.5 text-left text-sm transition-colors ${
                    active
                      ? "bg-muted text-foreground"
                      : "text-muted-foreground hover:bg-muted hover:text-foreground"
                  }`}
                >
                  <span className="truncate font-medium">
                    {n.name ?? shortId(n.id)}
                  </span>
                  <span className="font-mono text-[0.7rem] text-muted-foreground">
                    {shortId(n.id)}
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      )}

      {nodes.length >= NODE_LIMIT && labelCount > NODE_LIMIT ? (
        <p className="px-1 text-xs text-muted-foreground">
          Showing first {NODE_LIMIT}. {/* TODO: add offset pagination */}
        </p>
      ) : null}
    </section>
  );
}

// ---------------------------------------------------------------------------
// Right pane — node detail (properties + edges).
// ---------------------------------------------------------------------------
function DetailPane({
  slug,
  selectedNode,
  onSelectNode,
}: {
  slug: string;
  selectedNode: { label: string; id: string } | null;
  onSelectNode: (label: string, id: string) => void;
}) {
  const [detail, setDetail] = React.useState<NodeDetail | null>(null);
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [reloadKey, setReloadKey] = React.useState(0);

  React.useEffect(() => {
    if (!selectedNode) {
      setDetail(null);
      setError(null);
      return;
    }

    let cancelled = false;
    setLoading(true);
    setError(null);

    api<NodeDetail>(
      `/api/workspaces/${slug}/graph/nodes/${encodeURIComponent(
        selectedNode.label
      )}/${encodeURIComponent(selectedNode.id)}`
    )
      .then((res) => {
        if (cancelled) return;
        setDetail(res);
        setLoading(false);
      })
      .catch((err) => {
        if (cancelled) return;
        setError(
          err instanceof Error ? err.message : "Failed to load node detail."
        );
        setDetail(null);
        setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [slug, selectedNode, reloadKey]);

  if (!selectedNode) {
    return (
      <section className="flex min-h-[200px] flex-col items-center justify-center rounded-md border border-dashed border-border p-6 text-center text-sm text-muted-foreground">
        Select a node to see its properties and edges.
      </section>
    );
  }

  if (loading) {
    return (
      <section className="flex min-h-[200px] items-center justify-center rounded-md border border-border bg-card/40 p-6 text-sm text-muted-foreground">
        Loading…
      </section>
    );
  }

  if (error) {
    return (
      <section className="flex flex-col gap-3 rounded-md border border-border bg-card/40 p-3">
        <Alert variant="destructive">
          <AlertDescription>{error}</AlertDescription>
        </Alert>
        <div>
          <Button
            variant="outline"
            size="sm"
            onClick={() => setReloadKey((k) => k + 1)}
          >
            Retry
          </Button>
        </div>
      </section>
    );
  }

  if (!detail) {
    return (
      <section className="flex min-h-[200px] items-center justify-center rounded-md border border-border bg-card/40 p-6 text-sm text-muted-foreground">
        No detail available.
      </section>
    );
  }

  const name =
    (typeof detail.properties.name === "string"
      ? (detail.properties.name as string)
      : null) ?? shortId(detail.id);

  const propertyEntries = Object.entries(detail.properties)
    .filter(([key]) => key !== "workspace_id")
    .sort(([a], [b]) => a.localeCompare(b));

  return (
    <section className="flex flex-col gap-4 rounded-md border border-border bg-card/40 p-4">
      <header className="flex flex-col gap-1">
        <div className="flex flex-wrap items-center gap-2">
          <Badge variant="secondary">{detail.label}</Badge>
          <h2 className="font-medium">{name}</h2>
        </div>
        <p className="font-mono text-xs text-muted-foreground">
          {detail.id}
        </p>
      </header>

      <div className="flex flex-col gap-2">
        <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          Properties
        </h3>
        {propertyEntries.length === 0 ? (
          <p className="text-xs text-muted-foreground">No properties.</p>
        ) : (
          <table className="w-full table-fixed border-collapse text-sm">
            <tbody>
              {propertyEntries.map(([key, value]) => (
                <tr key={key} className="border-b border-border/50">
                  <td className="w-1/3 py-1.5 pr-3 align-top font-mono text-xs text-muted-foreground">
                    {key}
                  </td>
                  <td className="break-words py-1.5 align-top text-xs">
                    {formatPropertyValue(value)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <EdgeList
        title="Outgoing edges"
        edges={detail.outgoingEdges}
        direction="out"
        onSelectNode={onSelectNode}
      />
      <EdgeList
        title="Incoming edges"
        edges={detail.incomingEdges}
        direction="in"
        onSelectNode={onSelectNode}
      />

      {typeof detail.properties.name === "string" &&
      (detail.properties.name as string).length > 0 ? (
        <NodeClarificationsPanel
          slug={slug}
          nodeName={detail.properties.name as string}
        />
      ) : null}
    </section>
  );
}

function EdgeList({
  title,
  edges,
  direction,
  onSelectNode,
}: {
  title: string;
  edges: EdgeRef[];
  direction: "in" | "out";
  onSelectNode: (label: string, id: string) => void;
}) {
  return (
    <div className="flex flex-col gap-2">
      <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
        {title}
      </h3>
      {edges.length === 0 ? (
        <p className="text-xs text-muted-foreground">None.</p>
      ) : (
        <ul className="flex flex-col gap-1">
          {edges.map((e, idx) => {
            const otherLabel = e.otherNodeName ?? shortId(e.otherNodeId);
            return (
              <li key={`${e.label}-${e.otherNodeId}-${idx}`}>
                <button
                  type="button"
                  onClick={() =>
                    onSelectNode(e.otherNodeLabel, e.otherNodeId)
                  }
                  className="flex w-full flex-wrap items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
                >
                  {direction === "in" ? (
                    <span className="font-mono text-xs">←</span>
                  ) : null}
                  <Badge variant="outline" className="font-mono text-[0.7rem]">
                    {e.label}
                  </Badge>
                  {direction === "out" ? (
                    <span className="font-mono text-xs">→</span>
                  ) : null}
                  <Badge variant="secondary" className="text-[0.7rem]">
                    {e.otherNodeLabel}
                  </Badge>
                  <span className="truncate">{otherLabel}</span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
