"use client";

import {
  AlertCircleIcon,
  BoxIcon,
  CircleDollarSignIcon,
  FilesIcon,
  GitBranchIcon,
  MessageCircleQuestionIcon,
  NetworkIcon,
  PlusIcon,
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

// ---------------------------------------------------------------------------
// Formatters
// ---------------------------------------------------------------------------

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
  // ISO date "YYYY-MM-DD"
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

function repoName(url: string | null | undefined): string {
  if (!url) return "—";
  // strip protocol/host, drop trailing .git
  const cleaned = url.replace(/^https?:\/\/[^/]+\//, "").replace(/\.git$/, "");
  return cleaned || url;
}

// ---------------------------------------------------------------------------
// View
// ---------------------------------------------------------------------------

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
      {hasNoRepos ? <EmptyDashboardCard slug={slug} /> : null}

      {/* KPI grid */}
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-3">
        <KpiCard
          title="Repos"
          value={summary.repos.total}
          subtext={`${summary.repos.active} active · Last scan: ${formatRelative(summary.repos.lastScanAt)}`}
          icon={<BoxIcon className="size-4" />}
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
        />
        <KpiCard
          title="Files extracted"
          value={summary.extractions.totalFiles}
          subtext={`${summary.extractions.cachedPct.toFixed(0)}% cache hits`}
          icon={<FilesIcon className="size-4" />}
        />
        <KpiCard
          title="Open clarifications"
          value={summary.clarifications.open}
          subtext={`${summary.clarifications.answered} resolved`}
          icon={<MessageCircleQuestionIcon className="size-4" />}
          accent={summary.clarifications.open > 0 ? "destructive" : undefined}
        />
        <KpiCard
          title="LLM spend (30d)"
          value={formatUsd(summary.llmSpend.totalUsd)}
          subtext={`${formatNumber(summary.llmSpend.totalCalls)} calls · ${summary.llmSpend.cacheHitPct.toFixed(0)}% cached`}
          icon={<CircleDollarSignIcon className="size-4" />}
        />
        <KpiCard
          title="MCP calls (7d)"
          value={summary.mcpActivity.totalCalls}
          subtext={`p95 ${summary.mcpActivity.p95LatencyMs}ms · ${summary.mcpActivity.errorRatePct.toFixed(1)}% errors`}
          icon={<GitBranchIcon className="size-4" />}
        />
      </div>

      {/* Charts + activity feed */}
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">
        <div className="grid grid-cols-1 gap-4 lg:col-span-2 xl:grid-cols-2">
          <LlmSpendChart data={llmSpend} />
          <McpActivityChart data={mcpActivity} />
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

// ---------------------------------------------------------------------------
// KPI card
// ---------------------------------------------------------------------------

function KpiCard({
  title,
  value,
  subtext,
  icon,
  accent,
}: {
  title: string;
  value: number | string;
  subtext: string;
  icon: React.ReactNode;
  accent?: "destructive";
}) {
  const displayValue =
    typeof value === "number" ? formatNumber(value) : value;
  return (
    <Card>
      <CardHeader className="flex flex-row items-start justify-between gap-2 pb-1">
        <CardTitle className="text-xs font-medium text-muted-foreground uppercase tracking-wide">
          {title}
        </CardTitle>
        <span className="text-muted-foreground">{icon}</span>
      </CardHeader>
      <CardContent className="flex flex-col gap-1 px-4">
        <div className="flex items-baseline gap-2">
          <span className="font-heading text-2xl font-semibold tabular-nums">
            {displayValue}
          </span>
          {accent === "destructive" &&
          typeof value === "number" &&
          value > 0 ? (
            <Badge variant="destructive">attention</Badge>
          ) : null}
        </div>
        <p className="text-xs text-muted-foreground">{subtext}</p>
      </CardContent>
    </Card>
  );
}

// ---------------------------------------------------------------------------
// Charts
// ---------------------------------------------------------------------------

function LlmSpendChart({ data }: { data: DailyLlmSpend[] }) {
  const chartData = data.map((d) => ({
    day: formatDayShort(d.day),
    cost: Number(d.costUsd),
    calls: d.calls,
  }));

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">LLM spend (last 30 days)</CardTitle>
        <CardDescription>Daily cost in USD</CardDescription>
      </CardHeader>
      <CardContent className="px-2 pb-2">
        {chartData.length === 0 ? (
          <EmptyChart label="No spend recorded yet" />
        ) : (
          <ResponsiveContainer width="100%" height={200}>
            <BarChart
              data={chartData}
              margin={{ top: 4, right: 8, bottom: 0, left: -8 }}
            >
              <CartesianGrid
                strokeDasharray="3 3"
                stroke="var(--border)"
                vertical={false}
              />
              <XAxis
                dataKey="day"
                stroke="var(--muted-foreground)"
                fontSize={10}
                tickLine={false}
                axisLine={false}
                interval="preserveStartEnd"
              />
              <YAxis
                stroke="var(--muted-foreground)"
                fontSize={10}
                tickLine={false}
                axisLine={false}
                tickFormatter={(v) => `$${v}`}
              />
              <Tooltip
                contentStyle={{
                  background: "var(--popover)",
                  border: "1px solid var(--border)",
                  borderRadius: "var(--radius-md)",
                  fontSize: 12,
                }}
                formatter={(v) => [
                  `$${Number(v ?? 0).toFixed(2)}`,
                  "Cost",
                ]}
              />
              <Bar
                dataKey="cost"
                fill="var(--chart-2)"
                radius={[2, 2, 0, 0]}
              />
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
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">MCP calls (last 7 days)</CardTitle>
        <CardDescription>Calls and errors per day</CardDescription>
      </CardHeader>
      <CardContent className="px-2 pb-2">
        {chartData.length === 0 ? (
          <EmptyChart label="No MCP activity yet" />
        ) : (
          <ResponsiveContainer width="100%" height={200}>
            <AreaChart
              data={chartData}
              margin={{ top: 4, right: 8, bottom: 0, left: -8 }}
            >
              <CartesianGrid
                strokeDasharray="3 3"
                stroke="var(--border)"
                vertical={false}
              />
              <XAxis
                dataKey="day"
                stroke="var(--muted-foreground)"
                fontSize={10}
                tickLine={false}
                axisLine={false}
              />
              <YAxis
                stroke="var(--muted-foreground)"
                fontSize={10}
                tickLine={false}
                axisLine={false}
              />
              <Tooltip
                contentStyle={{
                  background: "var(--popover)",
                  border: "1px solid var(--border)",
                  borderRadius: "var(--radius-md)",
                  fontSize: 12,
                }}
              />
              <Area
                type="monotone"
                dataKey="calls"
                stroke="var(--chart-2)"
                fill="var(--chart-2)"
                fillOpacity={0.2}
                strokeWidth={2}
              />
              <Line
                type="monotone"
                dataKey="errors"
                stroke="var(--destructive)"
                strokeWidth={2}
                dot={false}
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
    <div className="flex h-[200px] items-center justify-center text-xs text-muted-foreground">
      {label}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Recent scans table + detail drawer
// ---------------------------------------------------------------------------

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
          // Normalize backend field names → frontend ScanDetail shape
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
                  new Date(finishedAt).getTime() -
                    new Date(startedAt).getTime()
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
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Recent scans</CardTitle>
        <CardDescription>Latest 10 scan runs</CardDescription>
      </CardHeader>
      <CardContent className="p-0">
        {scans.length === 0 ? (
          <div className="px-4 py-8 text-center text-sm text-muted-foreground">
            No scans yet. Add a repo and trigger a scan.
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-xs text-muted-foreground">
                  <th className="px-4 py-3 font-medium">Repo</th>
                  <th className="px-4 py-3 font-medium">Started</th>
                  <th className="px-4 py-3 font-medium">Duration</th>
                  <th className="px-4 py-3 font-medium">Files</th>
                  <th className="px-4 py-3 font-medium">Cost</th>
                  <th className="px-4 py-3 font-medium">Status</th>
                </tr>
              </thead>
              <tbody>
                {scans.map((scan) => (
                  <tr
                    key={scan.id}
                    onClick={() => setOpenScanId(scan.id)}
                    className="cursor-pointer border-b last:border-0 hover:bg-muted/40"
                  >
                    <td className="px-4 py-3 font-mono text-xs">
                      {repoName(scan.repoUrl)}
                    </td>
                    <td className="px-4 py-3 text-muted-foreground">
                      {formatRelative(scan.startedAt)}
                    </td>
                    <td className="px-4 py-3 tabular-nums">
                      {formatDuration(scan.durationMs)}
                    </td>
                    <td className="px-4 py-3 tabular-nums">
                      {scan.fileCount.toLocaleString()}
                    </td>
                    <td className="px-4 py-3 tabular-nums">
                      {formatUsd(scan.costUsd)}
                    </td>
                    <td className="px-4 py-3">
                      <Badge variant={statusBadgeVariant(scan.status)}>
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

      <Dialog
        open={openScanId !== null}
        onOpenChange={(o) => !o && setOpenScanId(null)}
      >
        <DialogContent className="max-w-2xl">
          <DialogHeader>
            <DialogTitle>Scan detail</DialogTitle>
            <DialogDescription>
              {detail
                ? `${repoName(detail.repoUrl)} · ${formatRelative(detail.startedAt)}`
                : "Loading…"}
            </DialogDescription>
          </DialogHeader>
          {loading ? (
            <p className="text-sm text-muted-foreground">Loading…</p>
          ) : error ? (
            <p className="text-sm text-destructive">{error}</p>
          ) : detail ? (
            <div className="flex flex-col gap-3 text-sm">
              <DetailRow label="Status">
                <Badge variant={statusBadgeVariant(detail.status)}>
                  {detail.status.toLowerCase()}
                </Badge>
              </DetailRow>
              <DetailRow label="Branch">
                <span className="font-mono text-xs">
                  {detail.defaultBranch ?? "—"}
                </span>
              </DetailRow>
              <DetailRow label="Commit">
                <span className="font-mono text-xs">
                  {detail.commitSha ? detail.commitSha.slice(0, 12) : "—"}
                </span>
              </DetailRow>
              <DetailRow label="Duration">
                {formatDuration(detail.durationMs)}
              </DetailRow>
              <DetailRow label="Files">
                {detail.fileCount.toLocaleString()} (
                {detail.cachedFiles.toLocaleString()} cached)
              </DetailRow>
              <DetailRow label="Graph delta">
                +{detail.nodesAdded.toLocaleString()} nodes · +
                {detail.edgesAdded.toLocaleString()} edges
              </DetailRow>
              <DetailRow label="Cost">{formatUsd(detail.costUsd)}</DetailRow>
              {detail.errorMessage ? (
                <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-xs text-destructive">
                  {detail.errorMessage}
                </div>
              ) : null}
              {detail.logs ? (
                <details className="rounded-md border border-border bg-muted/40">
                  <summary className="cursor-pointer px-3 py-2 text-xs font-medium">
                    Logs
                  </summary>
                  <pre className="max-h-64 overflow-auto px-3 pb-3 font-mono text-[11px] leading-tight whitespace-pre-wrap">
                    {detail.logs}
                  </pre>
                </details>
              ) : null}
            </div>
          ) : null}
          <DialogFooter>
            <Button
              variant="outline"
              onClick={() => setOpenScanId(null)}
            >
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
    <div className="flex items-center justify-between gap-4 border-b border-border/60 pb-2 last:border-0 last:pb-0">
      <span className="text-xs text-muted-foreground uppercase tracking-wide">
        {label}
      </span>
      <span>{children}</span>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Empty-state card (FE-019)
// ---------------------------------------------------------------------------

function EmptyDashboardCard({ slug }: { slug: string }) {
  return (
    <Card className="border-dashed">
      <CardHeader>
        <div className="flex items-center gap-2">
          <AlertCircleIcon className="size-4 text-muted-foreground" />
          <CardTitle className="text-base">Add your first repo</CardTitle>
        </div>
        <CardDescription>
          ArchMind starts mapping your codebase as soon as you add a GitHub
          repo. Once a scan completes, this dashboard fills in.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <Link
          href={`/workspaces/${slug}/repos`}
          className="inline-flex h-8 items-center justify-center gap-1.5 rounded-lg bg-primary px-2.5 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/80"
        >
          <PlusIcon className="size-3.5" />
          Add a repo
        </Link>
      </CardContent>
    </Card>
  );
}

