"use client";

import * as React from "react";
import ReactMarkdown from "react-markdown";

import { api } from "@/lib/api";
import type {
  Clarification,
  ClarificationStatus,
} from "@/lib/types";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import {
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";

const dateFormatter = new Intl.DateTimeFormat("en-US", {
  dateStyle: "medium",
  timeStyle: "short",
});

const ANSWER_MAX = 4000;

type TabValue = "open" | "answered" | "dismissed" | "all";

const TABS: { value: TabValue; label: string; status?: ClarificationStatus }[] = [
  { value: "open", label: "Open", status: "Open" },
  { value: "answered", label: "Answered", status: "Answered" },
  { value: "dismissed", label: "Dismissed", status: "Dismissed" },
  { value: "all", label: "All" },
];

function formatDate(value: string | null): string {
  if (!value) return "—";
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return dateFormatter.format(d);
}

function relativeTime(value: string): string {
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  const diffMs = Date.now() - d.getTime();
  const sec = Math.floor(diffMs / 1000);
  if (sec < 60) return `${sec}s ago`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min}m ago`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr}h ago`;
  const day = Math.floor(hr / 24);
  if (day < 30) return `${day}d ago`;
  return formatDate(value);
}

function priorityVariant(
  priority: number
): { className: string; label: string } {
  if (priority >= 70) {
    return {
      className:
        "bg-destructive/10 text-destructive border border-destructive/20",
      label: `P${priority}`,
    };
  }
  if (priority >= 40) {
    return {
      className:
        "bg-amber-500/10 text-amber-700 border border-amber-500/20 dark:text-amber-400",
      label: `P${priority}`,
    };
  }
  return {
    className:
      "bg-muted text-muted-foreground border border-border",
    label: `P${priority}`,
  };
}

function statusVariant(
  status: ClarificationStatus
): "default" | "secondary" | "outline" | "destructive" {
  if (status === "Open") return "default";
  if (status === "Answered") return "secondary";
  return "outline";
}

export function ClarificationsView({
  slug,
  initialClarifications,
}: {
  slug: string;
  initialClarifications: Clarification[];
}) {
  const [tab, setTab] = React.useState<TabValue>("open");
  const [items, setItems] = React.useState<Clarification[]>(
    initialClarifications
  );
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [expandedId, setExpandedId] = React.useState<string | null>(null);

  const reload = React.useCallback(
    async (current: TabValue) => {
      const found = TABS.find((t) => t.value === current);
      const qs = found?.status
        ? `?status=${found.status.toLowerCase()}&limit=50`
        : `?limit=50`;
      setLoading(true);
      setError(null);
      try {
        const data = await api<Clarification[]>(
          `/api/workspaces/${slug}/clarifications${qs}`
        );
        setItems(data);
      } catch (err) {
        setError(err instanceof Error ? err.message : "Failed to load.");
        setItems([]);
      } finally {
        setLoading(false);
      }
    },
    [slug]
  );

  const handleTabChange = React.useCallback(
    (value: TabValue) => {
      setTab(value);
      setExpandedId(null);
      void reload(value);
    },
    [reload]
  );

  const handleAnswered = React.useCallback(() => {
    setExpandedId(null);
    void reload(tab);
  }, [reload, tab]);

  return (
    <Tabs
      value={tab}
      onValueChange={(value) => handleTabChange(value as TabValue)}
    >
      <TabsList>
        {TABS.map((t) => (
          <TabsTrigger key={t.value} value={t.value}>
            {t.label}
          </TabsTrigger>
        ))}
      </TabsList>

      {TABS.map((t) => (
        <TabsContent key={t.value} value={t.value} className="pt-4">
          {error ? (
            <Alert variant="destructive" className="mb-4">
              <AlertDescription>{error}</AlertDescription>
            </Alert>
          ) : null}

          {loading ? (
            <p className="py-12 text-center text-sm text-muted-foreground">
              Loading…
            </p>
          ) : items.length === 0 ? (
            <EmptyState tab={tab} />
          ) : (
            <ul className="flex flex-col gap-2">
              {items.map((item) => (
                <ClarificationRow
                  key={item.id}
                  slug={slug}
                  clarification={item}
                  expanded={expandedId === item.id}
                  onToggle={() =>
                    setExpandedId((prev) =>
                      prev === item.id ? null : item.id
                    )
                  }
                  onChanged={handleAnswered}
                />
              ))}
            </ul>
          )}
        </TabsContent>
      ))}
    </Tabs>
  );
}

function EmptyState({ tab }: { tab: TabValue }) {
  const labels: Record<TabValue, string> = {
    open: "No open clarifications. Inbox zero.",
    answered: "No answered clarifications yet.",
    dismissed: "No dismissed clarifications.",
    all: "No clarifications yet. Run a scan to surface ambiguous extractions.",
  };
  return (
    <div className="flex justify-center py-12">
      <Card className="w-full max-w-md">
        <CardContent className="py-6 text-center text-sm text-muted-foreground">
          {labels[tab]}
        </CardContent>
      </Card>
    </div>
  );
}

function ClarificationRow({
  slug,
  clarification,
  expanded,
  onToggle,
  onChanged,
}: {
  slug: string;
  clarification: Clarification;
  expanded: boolean;
  onToggle: () => void;
  onChanged: () => void;
}) {
  const priority = priorityVariant(clarification.priority);
  return (
    <li>
      <Card className="overflow-hidden">
        <button
          type="button"
          onClick={onToggle}
          className="flex w-full flex-col gap-2 p-4 text-left transition-colors hover:bg-muted/40"
        >
          <div className="flex flex-wrap items-center gap-2">
            <span
              className={`inline-flex h-5 items-center rounded-4xl px-2 text-xs font-medium ${priority.className}`}
            >
              {priority.label}
            </span>
            <Badge variant="outline" className="text-[0.7rem]">
              {clarification.source}
            </Badge>
            <Badge
              variant={statusVariant(clarification.status)}
              className="text-[0.7rem]"
            >
              {clarification.status}
            </Badge>
            <span className="ml-auto text-xs text-muted-foreground">
              {relativeTime(clarification.createdAt)}
            </span>
          </div>

          <div className="flex flex-col gap-1">
            <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
              {clarification.topic}
            </p>
            <p className="text-sm font-medium">{clarification.question}</p>
          </div>

          {clarification.relatedNodeNames.length > 0 ? (
            <div className="flex flex-wrap gap-1">
              {clarification.relatedNodeNames.map((n) => (
                <Badge key={n} variant="secondary" className="text-[0.7rem]">
                  {n}
                </Badge>
              ))}
            </div>
          ) : null}
        </button>

        {expanded ? (
          <div className="border-t border-border bg-card/40 p-4">
            <ClarificationDetail
              slug={slug}
              clarification={clarification}
              onChanged={onChanged}
            />
          </div>
        ) : null}
      </Card>
    </li>
  );
}

function ClarificationDetail({
  slug,
  clarification,
  onChanged,
}: {
  slug: string;
  clarification: Clarification;
  onChanged: () => void;
}) {
  return (
    <div className="flex flex-col gap-4">
      {clarification.context ? (
        <div className="flex flex-col gap-1">
          <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
            Context
          </h3>
          <div className="prose prose-sm dark:prose-invert max-w-none rounded-md border border-border bg-background/50 p-3 text-sm">
            <ReactMarkdown>{clarification.context}</ReactMarkdown>
          </div>
        </div>
      ) : null}

      {clarification.relatedFilePaths.length > 0 ? (
        <div className="flex flex-col gap-1">
          <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
            Related files
          </h3>
          <ul className="flex flex-col gap-0.5">
            {clarification.relatedFilePaths.map((p) => (
              <li
                key={p}
                className="font-mono text-xs text-muted-foreground"
              >
                {p}
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      {clarification.status === "Answered" && clarification.answer ? (
        <div className="flex flex-col gap-1">
          <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
            Answer
          </h3>
          <p className="rounded-md border border-border bg-background/50 p-3 text-sm whitespace-pre-wrap">
            {clarification.answer}
          </p>
          {clarification.answeredAt ? (
            <p className="text-xs text-muted-foreground">
              Answered {formatDate(clarification.answeredAt)}
            </p>
          ) : null}
        </div>
      ) : null}

      {clarification.status === "Open" ? (
        <AnswerForm
          slug={slug}
          clarification={clarification}
          onChanged={onChanged}
        />
      ) : null}
    </div>
  );
}

function AnswerForm({
  slug,
  clarification,
  onChanged,
}: {
  slug: string;
  clarification: Clarification;
  onChanged: () => void;
}) {
  const hasChoices = clarification.choices.length > 0;
  const [answer, setAnswer] = React.useState("");
  const [submitting, setSubmitting] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [alreadyAnswered, setAlreadyAnswered] = React.useState(false);

  const [dismissOpen, setDismissOpen] = React.useState(false);
  const [dismissReason, setDismissReason] = React.useState("");
  const [dismissing, setDismissing] = React.useState(false);

  const trimmed = answer.trim();
  const canSubmit = trimmed.length > 0 && trimmed.length <= ANSWER_MAX;

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!canSubmit || submitting) return;
    setSubmitting(true);
    setError(null);
    try {
      await api(
        `/api/workspaces/${slug}/clarifications/${clarification.id}/answer`,
        {
          method: "POST",
          body: JSON.stringify({ answer: trimmed }),
        }
      );
      onChanged();
    } catch (err) {
      const msg = err instanceof Error ? err.message : "Failed to submit.";
      if (
        msg.toLowerCase().includes("already") ||
        msg.toLowerCase().includes("conflict") ||
        msg.includes("409")
      ) {
        setAlreadyAnswered(true);
        setTimeout(() => onChanged(), 1500);
      } else {
        setError(msg);
      }
    } finally {
      setSubmitting(false);
    }
  };

  const handleDismiss = async () => {
    setDismissing(true);
    try {
      const body: { reason?: string } = {};
      const r = dismissReason.trim();
      if (r) body.reason = r;
      await api(
        `/api/workspaces/${slug}/clarifications/${clarification.id}/dismiss`,
        {
          method: "POST",
          body: JSON.stringify(body),
        }
      );
      setDismissOpen(false);
      onChanged();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to dismiss.");
    } finally {
      setDismissing(false);
    }
  };

  if (alreadyAnswered) {
    return (
      <Alert>
        <AlertDescription>
          This question has already been resolved. Refreshing…
        </AlertDescription>
      </Alert>
    );
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-3">
      {error ? (
        <Alert variant="destructive">
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : null}

      <div className="flex flex-col gap-2">
        <Label className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          Your answer
        </Label>

        {hasChoices ? (
          <RadioGroup
            value={answer}
            onValueChange={(v) => setAnswer(typeof v === "string" ? v : "")}
            className="gap-2"
          >
            {clarification.choices.map((choice, idx) => {
              const id = `${clarification.id}-choice-${idx}`;
              return (
                <div key={id} className="flex items-start gap-2">
                  <RadioGroupItem value={choice} id={id} />
                  <Label
                    htmlFor={id}
                    className="cursor-pointer text-sm font-normal leading-5"
                  >
                    {choice}
                  </Label>
                </div>
              );
            })}
          </RadioGroup>
        ) : (
          <>
            <Textarea
              value={answer}
              onChange={(e) => setAnswer(e.target.value.slice(0, ANSWER_MAX))}
              placeholder="Provide a clear answer to the question above."
              className="min-h-[100px]"
              maxLength={ANSWER_MAX}
            />
            <div className="flex justify-end text-xs text-muted-foreground">
              {answer.length} / {ANSWER_MAX}
            </div>
          </>
        )}
      </div>

      <div className="flex items-center gap-2">
        <Button type="submit" disabled={!canSubmit || submitting}>
          {submitting ? "Submitting…" : "Submit answer"}
        </Button>
        <Button
          type="button"
          variant="outline"
          onClick={() => setDismissOpen(true)}
          disabled={submitting}
        >
          Dismiss
        </Button>
      </div>

      <AlertDialog open={dismissOpen} onOpenChange={setDismissOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Dismiss this clarification?</AlertDialogTitle>
            <AlertDialogDescription>
              The question will be moved to the Dismissed list and removed from
              the inbox. You can optionally record why.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <div className="flex flex-col gap-2">
            <Label
              htmlFor={`${clarification.id}-dismiss-reason`}
              className="text-xs font-medium uppercase tracking-wide text-muted-foreground"
            >
              Reason (optional)
            </Label>
            <Textarea
              id={`${clarification.id}-dismiss-reason`}
              value={dismissReason}
              onChange={(e) => setDismissReason(e.target.value)}
              placeholder="e.g. duplicate of another clarification"
              className="min-h-[60px]"
            />
          </div>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={dismissing}>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={(e) => {
                e.preventDefault();
                void handleDismiss();
              }}
              disabled={dismissing}
            >
              {dismissing ? "Dismissing…" : "Dismiss"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </form>
  );
}
