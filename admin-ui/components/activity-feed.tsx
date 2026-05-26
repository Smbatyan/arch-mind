"use client";

import {
  CheckCircle2Icon,
  CircleDotIcon,
  MessageCircleQuestionIcon,
  PlayCircleIcon,
  XCircleIcon,
} from "lucide-react";
import * as React from "react";

import { api } from "@/lib/api";
import type { Clarification, ScanSummary } from "@/lib/types";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";

// ---------------------------------------------------------------------------
// Helpers (duplicated minimally to keep the component drop-in)
// ---------------------------------------------------------------------------

function formatRelative(value: string): string {
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

function repoName(url: string | null | undefined): string {
  if (!url) return "—";
  return url.replace(/^https?:\/\/[^/]+\//, "").replace(/\.git$/, "") || url;
}

function formatUsd(value: string): string {
  const num = Number(value);
  if (Number.isNaN(num)) return "$0.00";
  return `$${num.toFixed(2)}`;
}

// ---------------------------------------------------------------------------
// Unified event model
// ---------------------------------------------------------------------------

type FeedEvent =
  | {
      kind: "scan-started";
      id: string;
      at: string;
      repoUrl: string | null;
    }
  | {
      kind: "scan-finished";
      id: string;
      at: string;
      repoUrl: string | null;
      fileCount: number;
      costUsd: string;
      success: boolean;
    }
  | {
      kind: "clarification";
      id: string;
      at: string;
      status: Clarification["status"];
      topic: string;
    };

// Normalize raw API scan shape → ScanSummary (API uses snake_case / different keys).
function normalizeScan(raw: Record<string, unknown>): ScanSummary {
  const finishedAt =
    (raw.finishedAt as string | null) ??
    (raw.completedAt as string | null) ??
    null;
  return {
    id: String(raw.id ?? ""),
    repoId: String(raw.repoId ?? ""),
    repoUrl: (raw.repoUrl as string | null) ?? null,
    startedAt: String(raw.startedAt ?? ""),
    finishedAt,
    durationMs: null,
    fileCount: Number(raw.fileCount ?? raw.filesScanned ?? 0),
    costUsd: String(raw.costUsd ?? raw.totalCostUsd ?? "0"),
    status: (raw.status as ScanSummary["status"]) ?? "Running",
  };
}

function buildFeed(
  scans: Record<string, unknown>[],
  clarifications: Clarification[]
): FeedEvent[] {
  const events: FeedEvent[] = [];

  for (const rawScan of scans) {
    const scan = normalizeScan(rawScan);
    const doneStatuses = ["succeeded", "Completed", "failed", "Failed", "cancelled", "Cancelled"];
    events.push({
      kind: "scan-started",
      id: `${scan.id}-start`,
      at: scan.startedAt,
      repoUrl: scan.repoUrl,
    });
    if (scan.finishedAt && doneStatuses.includes(scan.status)) {
      events.push({
        kind: "scan-finished",
        id: `${scan.id}-finish`,
        at: scan.finishedAt,
        repoUrl: scan.repoUrl,
        fileCount: scan.fileCount,
        costUsd: scan.costUsd,
        success: scan.status === "succeeded" || scan.status === "Completed",
      });
    }
  }

  for (const c of clarifications) {
    events.push({
      kind: "clarification",
      id: c.id,
      at: c.updatedAt ?? c.createdAt,
      status: c.status,
      topic: c.topic,
    });
  }

  events.sort((a, b) => +new Date(b.at) - +new Date(a.at));
  return events.slice(0, 15);
}

// ---------------------------------------------------------------------------
// Row rendering
// ---------------------------------------------------------------------------

function EventRow({ event }: { event: FeedEvent }) {
  const time = formatRelative(event.at);

  switch (event.kind) {
    case "scan-started":
      return (
        <Row
          icon={<PlayCircleIcon className="size-4 text-muted-foreground" />}
          verb="Scan started"
          subject={repoName(event.repoUrl)}
          time={time}
        />
      );
    case "scan-finished":
      return (
        <Row
          icon={
            event.success ? (
              <CheckCircle2Icon className="size-4 text-emerald-500" />
            ) : (
              <XCircleIcon className="size-4 text-destructive" />
            )
          }
          verb="Scan finished"
          subject={`${repoName(event.repoUrl)} · ${event.fileCount.toLocaleString()} files · ${formatUsd(event.costUsd)}`}
          time={time}
        />
      );
    case "clarification": {
      const verb =
        event.status === "Answered"
          ? "Answered clarification"
          : event.status === "Dismissed"
            ? "Dismissed clarification"
            : "Open clarification";
      return (
        <Row
          icon={
            event.status === "Open" ? (
              <MessageCircleQuestionIcon className="size-4 text-amber-500" />
            ) : (
              <CircleDotIcon className="size-4 text-muted-foreground" />
            )
          }
          verb={verb}
          subject={event.topic}
          time={time}
        />
      );
    }
  }
}

function Row({
  icon,
  verb,
  subject,
  time,
}: {
  icon: React.ReactNode;
  verb: string;
  subject: string;
  time: string;
}) {
  return (
    <li className="flex items-start gap-3 border-b border-border/60 px-4 py-2.5 last:border-0">
      <span className="mt-0.5 shrink-0">{icon}</span>
      <div className="min-w-0 flex-1">
        <p className="truncate text-sm">
          <span className="font-medium">{verb}</span>
          <span className="text-muted-foreground"> · {subject}</span>
        </p>
        <p className="text-xs text-muted-foreground">{time}</p>
      </div>
    </li>
  );
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export function ActivityFeed({ slug }: { slug: string }) {
  const [events, setEvents] = React.useState<FeedEvent[] | null>(null);
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const [scans, clarifications] = await Promise.all([
          api<Record<string, unknown>[]>(
            `/api/workspaces/${slug}/report/scans?limit=5`
          ).catch(() => [] as Record<string, unknown>[]),
          api<Clarification[]>(
            `/api/workspaces/${slug}/clarifications?status=all&limit=5`
          ).catch(() => [] as Clarification[]),
        ]);
        if (!cancelled) setEvents(buildFeed(scans, clarifications));
      } catch (e) {
        if (!cancelled) {
          setError(
            e instanceof Error ? e.message : "Failed to load activity"
          );
        }
      }
    }
    load();
    return () => {
      cancelled = true;
    };
  }, [slug]);

  return (
    <Card className="h-full">
      <CardHeader>
        <CardTitle className="text-sm">Activity</CardTitle>
        <CardDescription>Recent scans and clarifications</CardDescription>
      </CardHeader>
      <CardContent className="p-0">
        {error ? (
          <p className="px-4 py-6 text-sm text-destructive">{error}</p>
        ) : events === null ? (
          <p className="px-4 py-6 text-sm text-muted-foreground">Loading…</p>
        ) : events.length === 0 ? (
          <p className="px-4 py-6 text-sm text-muted-foreground">
            No recent activity. Run a scan or answer a clarification to get
            started.
          </p>
        ) : (
          <ul className="flex flex-col">
            {events.map((e) => (
              <EventRow key={`${e.kind}-${e.id}`} event={e} />
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}
