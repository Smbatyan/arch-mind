"use client";

import {
  AlertCircleIcon,
  ArrowUpRightIcon,
  BoxIcon,
  CircleDollarSignIcon,
  FilesIcon,
  GitBranchIcon,
  MessageCircleQuestionIcon,
  NetworkIcon,
  PlusIcon,
  TrendingUpIcon,
} from "lucide-react";
import Link from "next/link";
import * as React from "react";
import {
  Area,
  AreaChart,
  Bar,
  BarChart,
  CartesianGrid,
  Line,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";

import { api } from "@/lib/api";
import type {
  DailyLlmSpend,
  DailyMcpActivity,
  ReportSummary,
  ScanDetail,
  ScanSummary,
} from "@/lib/types";
import { ActivityFeed } from "@/components/activity-feed";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

// ── Formatters ───────────────────────────────────────────────────────

function formatRelative(value: string | null): string {
  if (!value) return "never";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  const diffMs = Date.now() - date.getTime();
  const sec = Math.floor(diffMs / 1000);
  if (sec < 60) return `${sec}s ago`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min}min ago`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr}h ago`;
  const day = Math.floor(hr / 24);
  if (day < 30) return `${day}d ago`;
  const month = Math.floor(day / 30);
  if (month < 12) return `${month}mo ago`;
  return `${Math.floor(month / 12)}y ago`;
}

function formatDuration(ms: number | null): string {
  if (ms === null || ms === undefined) return "—";
  if (ms < 1000) return `${ms}ms`;
  const sec = Math.round(ms / 1000);
  if (sec < 60) return `${sec}s`;
  const min = Math.floor(sec / 60);
  const rem = sec % 60;
  return rem ? `${min}m ${rem}s` : `${min}m`;
}

function formatUsd(value: string | number): string {
  const num = typeof value === "string" ? Number(value) : value;
  if (Number.isNaN(num)) return "$0.00";
  if (num >= 1000) return `$${num.toFixed(0)}`;
  return `$${num.toFixed(2)}`;
}

function formatNumber(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 10_000) return `${(n / 1000).toFixed(0)}k`;
  if (n >= 1000) return `${(n / 1000).toFixed(1)}k`;
  return n.toLocaleString();
}

function formatDayShort(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

function repoName(url: string | null | undefined): string {
  if (!url) return "—";
  const cleaned = url.replace(/^https?:\/\/[^/]+\//, "").replace(/\.git$/, "");
  return cleaned || url;
}

// ── Chart tooltip style ──────────────────────────────────────────────

const TOOLTIP_STYLE: React.CSSProperties = {
  background: "oklch(0.205 0.014 275)",
  border: "1px solid oklch(0.290 0.018 275)",
  borderRadius: "8px",
  fontSize: 12,
  color: "oklch(0.965 0.006 264)",
  boxShadow: "0 8px 32px oklch(0 0 0 / 0.5)",
};

// ── View ─────────────────────────────────────────────────────────────

export function DashboardView({
  slug,
  summary,
  llmSpend,
  mcpActivity,
  recentScans,
}: {
  slug: string;
  summary: ReportSummary;
  llmSpend: DailyLlmSpend[];
  mcpActivity: DailyMcpActivity[];
  recentScans: ScanSummary[];
}) {
  const hasNoRepos = summary.repos.total === 0;

  return (
    <div className="flex flex-col gap-6">
      {hasNoRepos && <EmptyDashboardCard slug={slug} />}

      {/* KPI bento grid */}
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <KpiCard
          title="Repositories"
          value={summary.repos.total}
          subtext={`${summary.repos.active} active · Last scan: ${formatRelative(summary.repos.lastScanAt)}`}
          icon={<BoxIcon className="size-4" />}
          color="violet"
          delay={0}
        />
        <KpiCard
          title="Graph nodes"
          value={summary.graph.totalNodes}
          subtext={
            summary.graph.topLabels.length > 0
              ? summary.graph.topLabels
                  .slice(0, 3)
                  .map((l) => `${l.label} (${formatNumber(l.count)})`)
                  .join(" · ")
              : `${summary.graph.totalEdges} edges`
          }
          icon={<NetworkIcon className="size-4" />}
          color="indigo"
          delay={60}
        />
        <KpiCard
          title="Files extracted"
          value={summary.extractions.totalFiles}
          subtext={`${summary.extractions.cachedPct.toFixed(0)}% cache hit rate`}
          icon={<FilesIcon className="size-4" />}
          color="sky"
          delay={120}
        />
        <KpiCard
          title="Open clarifications"
          value={summary.clarifications.open}
          subtext={`${summary.clarifications.answered} resolved`}
          icon={<MessageCircleQuestionIcon className="size-4" />}
          color={summary.clarifications.open > 0 ? "rose" : "emerald"}
          accent={summary.clarifications.open > 0 ? "attention" : undefined}
          delay={180}
        />
        <KpiCard
          title="LLM spend (30d)"
          value={formatUsd(summary.llmSpend.totalUsd)}
          subtext={`${formatNumber(summary.llmSpend.totalCalls)} calls · ${summary.llmSpend.cacheHitPct.toFixed(0)}% cached`}
          icon={<CircleDollarSignIcon className="size-4" />}
          color="amber"
          delay={240}
        />
        <KpiCard
          title="MCP calls (7d)"
          value={summary.mcpActivity.totalCalls}
          subtext={`p95 ${summary.mcpActivity.p95LatencyMs}ms · ${summary.mcpActivity.errorRatePct.toFixed(1)}% errors`}
          icon={<GitBranchIcon className="size-4" />}
          color="teal"
          delay={300}
        />
      </div>

      {/* Charts + activity */}
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">
        <div className="flex flex-col gap-4 lg:col-span-2">
          <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
            <LlmSpendChart data={llmSpend} />
            <McpActivityChart data={mcpActivity} />
          </div>
        </div>
        <div className="lg:col-span-1">
          <ActivityFeed slug={slug} />
        </div>
      </div>

      {/* Recent scans */}
      <RecentScansTable slug={slug} scans={recentScans} />
    </div>
  );
}

// ── KPI Card ─────────────────────────────────────────────────────────

type KpiColor = "violet" | "indigo" | "sky" | "emerald" | "rose" | "amber" | "teal";

const COLOR_MAP: Record<KpiColor, { icon: string; bg: string; border: string }> = {
  violet: {
    icon: "text-violet-400",
    bg: "bg-violet-500/10",
    border: "hover:border-violet-500/30",
  },
  indigo: {
    icon: "text-indigo-400",
    bg: "bg-indigo-500/10",
    border: "hover:border-indigo-500/30",
  },
  sky: {
    icon: "text-sky-400",
    bg: "bg-sky-500/10",
    border: "hover:border-sky-500/30",
  },
  emerald: {
    icon: "text-emerald-400",
    bg: "bg-emerald-500/10",
    border: "hover:border-emerald-500/30",
  },
  rose: {
    icon: "text-rose-400",
    bg: "bg-rose-500/10",
    border: "hover:border-rose-500/30",
  },
  amber: {
    icon: "text-amber-400",
    bg: "bg-amber-500/10",
    border: "hover:border-amber-500/30",
  },
  teal: {
    icon: "text-teal-400",
    bg: "bg-teal-500/10",
    border: "hover:border-teal-500/30",
  },
};

function KpiCard({
  title,
  value,
  subtext,
  icon,
  color,
  accent,
  delay,
}: {
  title: string;
  value: number | string;
  subtext: string;
  icon: React.ReactNode;
  color: KpiColor;
  accent?: string;
  delay: number;
}) {
  const displayValue = typeof value === "number" ? formatNumber(value) : value;
  const c = COLOR_MAP[color];

  return (
    <div
      className={`animate-fade-up relative overflow-hidden rounded-xl border border-border bg-card p-4 transition-all duration-200 ${c.border}`}
      style={{ "--delay": `${delay}ms` } as React.CSSProperties}
    >
      {/* Subtle gradient top stripe */}
      <div
        className="pointer-events-none absolute inset-x-0 top-0 h-px opacity-60"
        style={{
          background:
            color === "violet"
              ? "linear-gradient(90deg, transparent, oklch(0.63 0.245 295), transparent)"
              : color === "indigo"
              ? "linear-gradient(90deg, transparent, oklch(0.59 0.200 265), transparent)"
              : color === "rose"
              ? "linear-gradient(90deg, transparent, oklch(0.64 0.220 015), transparent)"
              : "linear-gradient(90deg, transparent, oklch(0.66 0.180 155), transparent)",
        }}
        aria-hidden
      />

      <div className="flex items-start justify-between gap-3">
        <div className="flex flex-col gap-1.5">
          <p className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
            {title}
          </p>
          <div className="flex items-baseline gap-2">
            <span className="text-2xl font-bold tabular-nums tracking-tight">
              {displayValue}
            </span>
            {accent === "attention" && (
              <Badge variant="destructive" className="text-[10px] px-1.5 py-0">
                needs attention
              </Badge>
            )}
          </div>
          <p className="text-xs text-muted-foreground leading-relaxed">{subtext}</p>
        </div>

        <div className={`rounded-lg p-2 ${c.bg} ${c.icon} shrink-0`}>{icon}</div>
      </div>
    </div>
  );
}

// ── Charts ───────────────────────────────────────────────────────────

function LlmSpendChart({ data }: { data: DailyLlmSpend[] }) {
  const chartData = data.map((d) => ({
    day: formatDayShort(d.day),
    cost: Number(d.costUsd),
    calls: d.calls,
  }));

  return (
    <Card className="animate-fade-up" style={{ "--delay": "360ms" } as React.CSSProperties}>
      <CardHeader className="pb-2">
        <div className="flex items-center justify-between">
          <div>
            <CardTitle className="text-sm font-medium">LLM Spend</CardTitle>
            <CardDescription className="text-xs">Last 30 days · USD</CardDescription>
          </div>
          <TrendingUpIcon className="size-4 text-muted-foreground/50" />
        </div>
      </CardHeader>
      <CardContent className="px-2 pb-3">
        {chartData.length === 0 ? (
          <EmptyChart label="No spend recorded yet" />
        ) : (
          <ResponsiveContainer width="100%" height={180}>
            <BarChart data={chartData} margin={{ top: 4, right: 4, bottom: 0, left: -16 }}>
              <defs>
                <linearGradient id="spendGrad" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0%" stopColor="oklch(0.63 0.245 295)" stopOpacity={0.9} />
                  <stop offset="100%" stopColor="oklch(0.59 0.200 265)" stopOpacity={0.6} />
                </linearGradient>
              </defs>
              <CartesianGrid
                strokeDasharray="3 3"
                stroke="oklch(0.290 0.018 275)"
                vertical={false}
              />
              <XAxis
                dataKey="day"
                stroke="oklch(0.660 0.018 264)"
                fontSize={10}
                tickLine={false}
                axisLine={false}
                interval="preserveStartEnd"
              />
              <YAxis
                stroke="oklch(0.660 0.018 264)"
                fontSize={10}
                tickLine={false}
                axisLine={false}
                tickFormatter={(v) => `$${v}`}
              />
              <Tooltip
                contentStyle={TOOLTIP_STYLE}
                formatter={(v) => [`$${Number(v ?? 0).toFixed(2)}`, "Cost"]}
                cursor={{ fill: "oklch(1 0 0 / 0.04)" }}
              />
              <Bar dataKey="cost" fill="url(#spendGrad)" radius={[3, 3, 0, 0]} />
            </BarChart>
          </ResponsiveContainer>
        )}
      </CardContent>
    </Card>
  );
}

function McpActivityChart({ data }: { data: DailyMcpActivity[] }) {
  const chartData = data.map((d) => ({
    day: formatDayShort(d.day),
    calls: d.calls,
    errors: d.errors,
  }));

  return (
    <Card className="animate-fade-up" style={{ "--delay": "420ms" } as React.CSSProperties}>
      <CardHeader className="pb-2">
        <div className="flex items-center justify-between">
          <div>
            <CardTitle className="text-sm font-medium">MCP Activity</CardTitle>
            <CardDescription className="text-xs">Last 7 days · calls & errors</CardDescription>
          </div>
          <GitBranchIcon className="size-4 text-muted-foreground/50" />
        </div>
      </CardHeader>
      <CardContent className="px-2 pb-3">
        {chartData.length === 0 ? (
          <EmptyChart label="No MCP activity yet" />
        ) : (
          <ResponsiveContainer width="100%" height={180}>
            <AreaChart data={chartData} margin={{ top: 4, right: 4, bottom: 0, left: -16 }}>
              <defs>
                <linearGradient id="callsGrad" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0%" stopColor="oklch(0.59 0.200 265)" stopOpacity={0.4} />
                  <stop offset="100%" stopColor="oklch(0.59 0.200 265)" stopOpacity={0.02} />
                </linearGradient>
              </defs>
              <CartesianGrid
                strokeDasharray="3 3"
                stroke="oklch(0.290 0.018 275)"
                vertical={false}
              />
              <XAxis
                dataKey="day"
                stroke="oklch(0.660 0.018 264)"
                fontSize={10}
                tickLine={false}
                axisLine={false}
              />
              <YAxis
                stroke="oklch(0.660 0.018 264)"
                fontSize={10}
                tickLine={false}
                axisLine={false}
              />
              <Tooltip
                contentStyle={TOOLTIP_STYLE}
                cursor={{ stroke: "oklch(0.63 0.245 295 / 0.3)", strokeWidth: 1 }}
              />
              <Area
                type="monotone"
                dataKey="calls"
                stroke="oklch(0.59 0.200 265)"
                fill="url(#callsGrad)"
                strokeWidth={2}
                dot={false}
                activeDot={{ r: 3, fill: "oklch(0.59 0.200 265)" }}
              />
              <Line
                type="monotone"
                dataKey="errors"
                stroke="oklch(0.64 0.220 015)"
                strokeWidth={1.5}
                dot={false}
                strokeDasharray="4 2"
              />
            </AreaChart>
          </ResponsiveContainer>
        )}
      </CardContent>
    </Card>
  );
}

function EmptyChart({ label }: { label: string }) {
  return (
    <div className="flex h-[180px] items-center justify-center text-xs text-muted-foreground/60">
      {label}
    </div>
  );
}

// ── Recent Scans Table ───────────────────────────────────────────────

function statusBadgeVariant(
  status: ScanSummary["status"] | string
): "default" | "secondary" | "destructive" | "outline" {
  switch (status) {
    case "succeeded":
    case "Completed":
      return "default";
    case "failed":
    case "Failed":
      return "destructive";
    case "cancelled":
    case "Cancelled":
      return "outline";
    default:
      return "secondary";
  }
}

function RecentScansTable({
  slug,
  scans,
}: {
  slug: string;
  scans: ScanSummary[];
}) {
  const [openScanId, setOpenScanId] = React.useState<string | null>(null);
  const [detail, setDetail] = React.useState<ScanDetail | null>(null);
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    if (!openScanId) {
      setDetail(null);
      setError(null);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    api<Record<string, unknown>>(`/api/workspaces/${slug}/report/scans/${openScanId}`)
      .then((raw) => {
        if (!cancelled) {
          const finishedAt =
            (raw.finishedAt as string | null) ??
            (raw.completedAt as string | null) ??
            null;
          const startedAt = String(raw.startedAt ?? "");
          const durationMs =
            (raw.durationMs as number | null) ??
            (startedAt && finishedAt
              ? Math.max(
                  0,
                  new Date(finishedAt).getTime() - new Date(startedAt).getTime()
                )
              : null);
          const normalized: ScanDetail = {
            id: String(raw.id ?? openScanId),
            repoId: String(raw.repoId ?? ""),
            repoUrl: (raw.repoUrl as string | null) ?? null,
            startedAt,
            finishedAt,
            durationMs,
            fileCount: Number(raw.fileCount ?? raw.filesScanned ?? 0),
            costUsd: String(raw.costUsd ?? raw.totalCostUsd ?? "0"),
            status: (raw.status as ScanDetail["status"]) ?? "Running",
            defaultBranch: (raw.defaultBranch as string | null) ?? null,
            commitSha:
              (raw.commitSha as string | null) ??
              (raw.toSha as string | null) ??
              null,
            nodesAdded: Number(raw.nodesAdded ?? raw.graphifyNodes ?? 0),
            edgesAdded: Number(raw.edgesAdded ?? raw.graphifyEdges ?? 0),
            cachedFiles: Number(raw.cachedFiles ?? raw.filesEnqueued ?? 0),
            errorMessage: (raw.errorMessage as string | null) ?? null,
            logs: (raw.logs as string | null) ?? null,
          };
          setDetail(normalized);
        }
      })
      .catch((e: unknown) => {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : "Failed to load scan");
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [openScanId, slug]);

  return (
    <Card
      className="animate-fade-up overflow-hidden"
      style={{ "--delay": "480ms" } as React.CSSProperties}
    >
      <CardHeader className="pb-3">
        <div className="flex items-center justify-between">
          <div>
            <CardTitle className="text-sm font-medium">Recent Scans</CardTitle>
            <CardDescription className="text-xs">Latest 10 scan runs</CardDescription>
          </div>
          {scans.length > 0 && (
            <Link
              href={`/workspaces/${slug}/repos`}
              className="flex items-center gap-1 text-xs text-muted-foreground transition-colors hover:text-foreground"
            >
              View repos
              <ArrowUpRightIcon className="size-3" />
            </Link>
          )}
        </div>
      </CardHeader>
      <CardContent className="p-0">
        {scans.length === 0 ? (
          <div className="px-6 py-10 text-center text-sm text-muted-foreground">
            No scans yet. Add a repo and trigger a scan.
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-border/60 text-left">
                  <th className="px-4 py-2.5 text-xs font-medium text-muted-foreground">
                    Repository
                  </th>
                  <th className="px-4 py-2.5 text-xs font-medium text-muted-foreground">
                    Started
                  </th>
                  <th className="px-4 py-2.5 text-xs font-medium text-muted-foreground">
                    Duration
                  </th>
                  <th className="px-4 py-2.5 text-xs font-medium text-muted-foreground">
                    Files
                  </th>
                  <th className="px-4 py-2.5 text-xs font-medium text-muted-foreground">
                    Cost
                  </th>
                  <th className="px-4 py-2.5 text-xs font-medium text-muted-foreground">
                    Status
                  </th>
                </tr>
              </thead>
              <tbody>
                {scans.map((scan) => (
                  <tr
                    key={scan.id}
                    onClick={() => setOpenScanId(scan.id)}
                    className="cursor-pointer border-b border-border/40 last:border-0 transition-colors hover:bg-muted/30"
                  >
                    <td className="px-4 py-3 font-mono text-xs text-muted-foreground">
                      {repoName(scan.repoUrl)}
                    </td>
                    <td className="px-4 py-3 text-xs text-muted-foreground">
                      {formatRelative(scan.startedAt)}
                    </td>
                    <td className="px-4 py-3 text-xs tabular-nums">
                      {formatDuration(scan.durationMs)}
                    </td>
                    <td className="px-4 py-3 text-xs tabular-nums">
                      {scan.fileCount.toLocaleString()}
                    </td>
                    <td className="px-4 py-3 text-xs tabular-nums">
                      {formatUsd(scan.costUsd)}
                    </td>
                    <td className="px-4 py-3">
                      <Badge
                        variant={statusBadgeVariant(scan.status)}
                        className="text-[10px] px-1.5 py-0"
                      >
                        {scan.status.toLowerCase()}
                      </Badge>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </CardContent>

      {/* Scan detail dialog */}
      <Dialog
        open={openScanId !== null}
        onOpenChange={(o) => !o && setOpenScanId(null)}
      >
        <DialogContent className="max-w-2xl">
          <DialogHeader>
            <DialogTitle>Scan details</DialogTitle>
            <DialogDescription>
              {detail
                ? `${repoName(detail.repoUrl)} · ${formatRelative(detail.startedAt)}`
                : "Loading…"}
            </DialogDescription>
          </DialogHeader>
          {loading ? (
            <div className="space-y-2 py-2">
              {[...Array(5)].map((_, i) => (
                <div
                  key={i}
                  className="h-8 rounded-md bg-muted/40 animate-pulse"
                  style={{ animationDelay: `${i * 60}ms` }}
                />
              ))}
            </div>
          ) : error ? (
            <p className="text-sm text-destructive">{error}</p>
          ) : detail ? (
            <div className="flex flex-col gap-0 divide-y divide-border/60 text-sm">
              <DetailRow label="Status">
                <Badge
                  variant={statusBadgeVariant(detail.status)}
                  className="text-[11px]"
                >
                  {detail.status.toLowerCase()}
                </Badge>
              </DetailRow>
              <DetailRow label="Branch">
                <span className="font-mono text-xs">
                  {detail.defaultBranch ?? "—"}
                </span>
              </DetailRow>
              <DetailRow label="Commit">
                <span className="font-mono text-xs text-muted-foreground">
                  {detail.commitSha ? detail.commitSha.slice(0, 12) : "—"}
                </span>
              </DetailRow>
              <DetailRow label="Duration">{formatDuration(detail.durationMs)}</DetailRow>
              <DetailRow label="Files">
                {detail.fileCount.toLocaleString()}{" "}
                <span className="text-muted-foreground">
                  ({detail.cachedFiles.toLocaleString()} cached)
                </span>
              </DetailRow>
              <DetailRow label="Graph delta">
                <span className="text-emerald-400">
                  +{detail.nodesAdded.toLocaleString()} nodes
                </span>{" "}
                ·{" "}
                <span className="text-sky-400">
                  +{detail.edgesAdded.toLocaleString()} edges
                </span>
              </DetailRow>
              <DetailRow label="Cost">{formatUsd(detail.costUsd)}</DetailRow>
            </div>
          ) : null}

          {detail?.errorMessage && (
            <div className="rounded-lg border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
              {detail.errorMessage}
            </div>
          )}

          {detail?.logs && (
            <details className="rounded-lg border border-border bg-muted/20">
              <summary className="cursor-pointer px-3 py-2 text-xs font-medium text-muted-foreground hover:text-foreground">
                Logs
              </summary>
              <pre className="max-h-64 overflow-auto px-3 pb-3 font-mono text-[11px] leading-relaxed text-muted-foreground whitespace-pre-wrap">
                {detail.logs}
              </pre>
            </details>
          )}

          <DialogFooter>
            <Button variant="outline" size="sm" onClick={() => setOpenScanId(null)}>
              Close
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  );
}

function DetailRow({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div className="flex items-center justify-between gap-4 py-2.5">
      <span className="text-xs font-medium text-muted-foreground uppercase tracking-wide w-28 shrink-0">
        {label}
      </span>
      <span className="text-sm">{children}</span>
    </div>
  );
}

// ── Empty state ──────────────────────────────────────────────────────

function EmptyDashboardCard({ slug }: { slug: string }) {
  return (
    <div className="animate-fade-up rounded-xl border border-dashed border-border/60 bg-card/50 p-6">
      <div className="flex flex-col items-start gap-3 sm:flex-row sm:items-center">
        <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-violet-500/10">
          <AlertCircleIcon className="size-5 text-violet-400" />
        </div>
        <div className="flex-1">
          <p className="font-medium">Add your first repository</p>
          <p className="mt-0.5 text-sm text-muted-foreground">
            ArchMind maps your codebase into a knowledge graph. Add a GitHub repo to start.
          </p>
        </div>
        <Link
          href={`/workspaces/${slug}/repos`}
          className="flex shrink-0 items-center gap-1.5 rounded-lg bg-primary px-3 py-1.5 text-sm font-medium text-primary-foreground transition-all hover:bg-primary/85 hover:scale-[0.98]"
        >
          <PlusIcon className="size-3.5" />
          Add repo
        </Link>
      </div>
    </div>
  );
}
