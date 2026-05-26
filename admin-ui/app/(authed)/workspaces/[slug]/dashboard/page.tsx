import { cookies } from "next/headers";
import { redirect } from "next/navigation";

import type {
  DailyLlmSpend,
  DailyMcpActivity,
  ReportSummary,
  ScanSummary,
} from "@/lib/types";

import { DashboardView } from "./dashboard-view";

const API_URL =
  process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

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

  const [summary, llmSpend, mcpActivity, recentScans] = await Promise.all([
    fetchJson<ReportSummary>(
      `${API_URL}/api/workspaces/${slug}/report/summary`,
      headers,
      EMPTY_SUMMARY
    ),
    fetchJson<DailyLlmSpend[]>(
      `${API_URL}/api/workspaces/${slug}/report/llm-spend?days=30`,
      headers,
      []
    ),
    fetchJson<DailyMcpActivity[]>(
      `${API_URL}/api/workspaces/${slug}/report/mcp-activity?days=7`,
      headers,
      []
    ),
    fetchJson<ScanSummary[]>(
      `${API_URL}/api/workspaces/${slug}/report/scans?limit=10`,
      headers,
      []
    ),
  ]);

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
