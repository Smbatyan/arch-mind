"use client";

import Link from "next/link";
import * as React from "react";

import { api } from "@/lib/api";
import type { Clarification, ClarificationStatus } from "@/lib/types";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";

const dateFormatter = new Intl.DateTimeFormat("en-US", {
  dateStyle: "medium",
  timeStyle: "short",
});

function formatDate(value: string | null): string {
  if (!value) return "—";
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return dateFormatter.format(d);
}

function truncate(s: string, n: number): string {
  if (!s) return "";
  return s.length > n ? `${s.slice(0, n)}…` : s;
}

function statusVariant(
  status: ClarificationStatus
): "default" | "secondary" | "outline" {
  if (status === "Open") return "default";
  if (status === "Answered") return "secondary";
  return "outline";
}

export function NodeClarificationsPanel({
  slug,
  nodeName,
}: {
  slug: string;
  nodeName: string;
}) {
  const [items, setItems] = React.useState<Clarification[] | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [loading, setLoading] = React.useState(false);

  React.useEffect(() => {
    if (!nodeName) {
      setItems(null);
      setError(null);
      return;
    }

    let cancelled = false;
    setLoading(true);
    setError(null);

    api<Clarification[]>(
      `/api/workspaces/${slug}/clarifications/by-node/${encodeURIComponent(nodeName)}`
    )
      .then((res) => {
        if (cancelled) return;
        setItems(res);
        setLoading(false);
      })
      .catch((err) => {
        if (cancelled) return;
        setError(
          err instanceof Error ? err.message : "Failed to load clarifications."
        );
        setItems(null);
        setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [slug, nodeName]);

  return (
    <section className="flex flex-col gap-3 rounded-md border border-border bg-card/40 p-3">
      <div className="flex items-baseline justify-between gap-2">
        <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          Clarifications for this node
        </h3>
        <Link
          href={`/workspaces/${slug}/clarifications`}
          className="text-xs text-muted-foreground hover:text-foreground hover:underline"
        >
          View all
        </Link>
      </div>

      {loading ? (
        <p className="px-1 py-4 text-xs text-muted-foreground">Loading…</p>
      ) : error ? (
        <Alert variant="destructive">
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : !items || items.length === 0 ? (
        <p className="px-1 py-4 text-xs text-muted-foreground">
          No clarifications for this node.
        </p>
      ) : (
        <ul className="flex flex-col gap-2">
          {items.map((c) => (
            <li
              key={c.id}
              className="flex flex-col gap-1 rounded-md border border-border/50 bg-background/40 p-2.5"
            >
              <div className="flex flex-wrap items-center gap-1.5">
                <Badge
                  variant={statusVariant(c.status)}
                  className="text-[0.7rem]"
                >
                  {c.status}
                </Badge>
                <Badge variant="outline" className="text-[0.7rem]">
                  {c.source}
                </Badge>
                <span className="ml-auto text-[0.7rem] text-muted-foreground">
                  {formatDate(c.createdAt)}
                </span>
              </div>
              <p
                className="text-sm font-medium leading-snug"
                title={c.question}
              >
                {truncate(c.question, 140)}
              </p>
              {c.status === "Answered" && c.answer ? (
                <div className="mt-1 rounded border border-border/40 bg-card/40 p-2">
                  <p className="mb-0.5 text-[0.65rem] font-medium uppercase tracking-wide text-muted-foreground">
                    Answer
                  </p>
                  <p className="text-xs whitespace-pre-wrap">
                    {truncate(c.answer, 240)}
                  </p>
                  {c.answeredAt ? (
                    <p className="mt-1 text-[0.7rem] text-muted-foreground">
                      {formatDate(c.answeredAt)}
                    </p>
                  ) : null}
                </div>
              ) : null}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
