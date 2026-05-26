import { cookies } from "next/headers";
import { redirect } from "next/navigation";

import type {
  DailyLlmSpend,
  DailyMcpActivity,
  ReportSummary,
  ScanSummary,
} from "@/lib/types";

// ---------------------------------------------------------------------------
// API response normalizers — the backend shape differs from the frontend types
// ---------------------------------------------------------------------------

function normalizeSummary(raw: unknown): ReportSummary {
  const r = (raw ?? {}) as Record<string, unknown>;

  // graph: API returns { nodeCounts: {Label: n}, edgeCounts: {Type: n} }
  const graphRaw = (r.graph ?? {}) as Record<string, Record<string, number>>;
  const nodeCounts = graphRaw.nodeCounts ?? {};
  const edgeCounts = graphRaw.edgeCounts ?? {};
  const totalNodes = Object.values(nodeCounts).reduce((a, b) => a + b, 0);
  const totalEdges = Object.values(edgeCounts).reduce((a, b) => a + b, 0);
  const topLabels = Object.entries(nodeCounts)
    .sort((a, b) => b[1] - a[1])
    .slice(0, 5)
    .map(([label, count]) => ({ label, count }));

  // skills: API has { count } not { total, enabled }
  const skillsRaw = (r.skills ?? {}) as Record<string, number>;
  const skillsTotal = skillsRaw.total ?? skillsRaw.count ?? 0;

  // llmSpend: API uses key "llm" with field "totalCostUsd"
  const llmRaw = ((r.llmSpend ?? r.llm) ?? {}) as Record<string, unknown>;
  const llmTotalUsd = String(
    (llmRaw.totalUsd ?? llmRaw.totalCostUsd) ?? "0"
  );

  // mcpActivity: API uses key "mcp"
  const mcpRaw = ((r.mcpActivity ?? r.mcp) ?? {}) as Record<string, number>;

  return {
    repos: (r.repos as ReportSummary["repos"]) ?? {
      total: 0,
      active: 0,
      lastScanAt: null,
    },
    graph: { totalNodes, totalEdges, topLabels },
    extractions: (r.extractions as ReportSummary["extractions"]) ?? {
      totalFiles: 0,
      cachedPct: 0,
    },
    clarifications: (r.clarifications as ReportSummary["clarifications"]) ?? {
      open: 0,
      answered: 0,
      dismissed: 0,
    },
    skills: { total: skillsTotal, enabled: skillsRaw.enabled ?? skillsTotal },
    llmSpend: {
      totalUsd: llmTotalUsd,
      totalCalls: Number(llmRaw.totalCalls ?? 0),
      cacheHitPct: Number(llmRaw.cacheHitPct ?? 0),
    },
    mcpActivity: {
      totalCalls: mcpRaw.totalCalls ?? 0,
      errorRatePct: mcpRaw.errorRatePct ?? 0,
      p95LatencyMs: mcpRaw.p95LatencyMs ?? 0,
    },
  };
}

function normalizeDays<T>(raw: unknown, fallback: T[]): T[] {
  if (Array.isArray(raw)) return raw as T[];
  const r = (raw ?? {}) as { days?: T[] };
  return r.days ?? fallback;
}

function normalizeScans(raw: unknown): ScanSummary[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((item: Record<string, unknown>) => {
    const startedAt = String(item.startedAt ?? "");
    const finishedAt =
      (item.finishedAt as string | null) ??
      (item.completedAt as string | null) ??
      null;
    const durationMs =
      startedAt && finishedAt
        ? Math.max(0, new Date(finishedAt).getTime() - new Date(startedAt).getTime())
        : null;
    return {
      id: String(item.id ?? ""),
      repoId: String(item.repoId ?? ""),
      repoUrl: (item.repoUrl as string | null) ?? null,
      startedAt,
      finishedAt,
      durationMs,
      fileCount: Number(item.fileCount ?? item.filesScanned ?? 0),
      costUsd: String(item.costUsd ?? item.totalCostUsd ?? "0"),
      status: (item.status as ScanSummary["status"]) ?? "Pending",
    } as ScanSummary;
  });
}

import { DashboardView } from "./dashboard-view";

const API_URL =
  (typeof window === "undefined" ? process.env.INTERNAL_API_URL : undefined) ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5000";

type WorkspaceDetail = {
  id: string;
  slug: string;
  name: string;
  role?: string;
  createdAt: string;
};

async function buildCookieHeaders(): Promise<HeadersInit> {
  const cookieStore = await cookies();
  const sid = cookieStore.get("archmind.sid");
  return sid ? { cookie: `${sid.name}=${sid.value}` } : {};
}

async function fetchJson<T>(
  url: string,
  headers: HeadersInit,
  fallback: T
): Promise<T> {
  try {
    const res = await fetch(url, { headers, cache: "no-store" });
    if (!res.ok) return fallback;
    return (await res.json()) as T;
  } catch {
    return fallback;
  }
}

const EMPTY_SUMMARY: ReportSummary = {
  repos: { total: 0, active: 0, lastScanAt: null },
  graph: { totalNodes: 0, totalEdges: 0, topLabels: [] },
  extractions: { totalFiles: 0, cachedPct: 0 },
  clarifications: { open: 0, answered: 0, dismissed: 0 },
  skills: { total: 0, enabled: 0 },
  llmSpend: { totalUsd: "0", totalCalls: 0, cacheHitPct: 0 },
  mcpActivity: { totalCalls: 0, errorRatePct: 0, p95LatencyMs: 0 },
};

export default async function DashboardPage({
  params,
}: {
  params: Promise<{ slug: string }>;
}) {
  const { slug } = await params;
  const headers = await buildCookieHeaders();

  const workspace = await fetchJson<WorkspaceDetail | null>(
    `${API_URL}/api/workspaces/${slug}`,
    headers,
    null
  );
  if (!workspace) {
    redirect("/workspaces");
  }

  const [rawSummary, rawLlmSpend, rawMcpActivity, recentScansRaw] =
    await Promise.all([
      fetchJson<unknown>(
        `${API_URL}/api/workspaces/${slug}/report/summary`,
        headers,
        null
      ),
      fetchJson<unknown>(
        `${API_URL}/api/workspaces/${slug}/report/llm-spend?days=30`,
        headers,
        null
      ),
      fetchJson<unknown>(
        `${API_URL}/api/workspaces/${slug}/report/mcp-activity?days=7`,
        headers,
        null
      ),
      fetchJson<unknown>(
        `${API_URL}/api/workspaces/${slug}/report/scans?limit=10`,
        headers,
        []
      ),
    ]);

  const summary = rawSummary ? normalizeSummary(rawSummary) : EMPTY_SUMMARY;
  const llmSpend = normalizeDays<DailyLlmSpend>(rawLlmSpend, []);
  const mcpActivity = normalizeDays<DailyMcpActivity>(rawMcpActivity, []);
  const recentScans = normalizeScans(recentScansRaw);

  return (
    <div className="mx-auto flex w-full max-w-7xl flex-col gap-6 py-8">
      <div className="flex flex-col gap-1">
        <h1 className="font-heading text-2xl font-medium">Dashboard</h1>
        <p className="text-sm text-muted-foreground">{workspace.name}</p>
      </div>
      <DashboardView
        slug={slug}
        summary={summary}
        llmSpend={llmSpend}
        mcpActivity={mcpActivity}
        recentScans={recentScans}
      />
    </div>
  );
}
