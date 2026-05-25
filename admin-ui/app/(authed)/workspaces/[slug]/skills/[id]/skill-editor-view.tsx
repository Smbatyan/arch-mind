"use client";

import dynamic from "next/dynamic";
import { useRouter } from "next/navigation";
import * as React from "react";

import { api } from "@/lib/api";
import type {
  Skill,
  SkillRevision,
  SkillRevisionSummary,
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
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from "@/components/ui/tabs";

import { SkillEditorForm } from "../skill-editor-form";

// Monaco viewer for revision body — same wrapper as the editor, read-only.
const SkillMarkdownEditor = dynamic(
  () => import("@/components/skill-markdown-editor"),
  {
    ssr: false,
    loading: () => (
      <div className="flex h-[500px] items-center justify-center rounded-md border border-border text-sm text-muted-foreground">
        Loading viewer…
      </div>
    ),
  }
);

const dateFormatter = new Intl.DateTimeFormat("en-US", {
  dateStyle: "medium",
  timeStyle: "short",
});

function formatDate(value: string): string {
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return dateFormatter.format(d);
}

export function SkillEditorView({
  slug,
  skillId,
  initialSkill,
  initialRevisions,
}: {
  slug: string;
  skillId: string;
  initialSkill: Skill;
  initialRevisions: SkillRevisionSummary[];
}) {
  const router = useRouter();
  const [skill, setSkill] = React.useState<Skill>(initialSkill);
  const [revisions, setRevisions] =
    React.useState<SkillRevisionSummary[]>(initialRevisions);

  const [confirmOpen, setConfirmOpen] = React.useState(false);
  const [deleting, setDeleting] = React.useState(false);
  const [deleteError, setDeleteError] = React.useState<string | null>(null);

  const handleSaved = React.useCallback((next: Skill) => {
    setSkill(next);
    // Refresh revisions after save.
    void (async () => {
      try {
        const fresh = await api<SkillRevisionSummary[]>(
          `/api/workspaces/${slug}/skills/${skillId}/revisions`
        );
        setRevisions(fresh);
      } catch {
        // ignore — table just shows what it last had
      }
    })();
  }, [slug, skillId]);

  async function handleDelete() {
    setDeleting(true);
    setDeleteError(null);
    try {
      await api<void>(
        `/api/workspaces/${slug}/skills/${skillId}`,
        { method: "DELETE" }
      );
      setConfirmOpen(false);
      router.push(`/workspaces/${slug}/skills`);
    } catch (err) {
      setDeleteError(
        err instanceof Error ? err.message : "Failed to delete skill."
      );
      setDeleting(false);
    }
  }

  return (
    <>
      <Tabs defaultValue="edit">
        <TabsList>
          <TabsTrigger value="edit">Edit</TabsTrigger>
          <TabsTrigger value="history">
            History
            {revisions.length > 0 ? ` (${revisions.length})` : ""}
          </TabsTrigger>
        </TabsList>

        <TabsContent value="edit" className="pt-4">
          <SkillEditorForm
            slug={slug}
            mode="edit"
            skillId={skillId}
            initial={{
              name: skill.name,
              title: skill.title,
              description: skill.description,
              triggers: skill.triggers,
              enabled: skill.enabled,
              body: skill.body,
            }}
            requireChangeNote
            onSaved={handleSaved}
          />
        </TabsContent>

        <TabsContent value="history" className="pt-4">
          <HistoryPanel
            slug={slug}
            skillId={skillId}
            revisions={revisions}
          />
        </TabsContent>
      </Tabs>

      <div className="flex items-center justify-end pt-6">
        <Button
          variant="destructive"
          size="sm"
          onClick={() => setConfirmOpen(true)}
        >
          Delete skill
        </Button>
      </div>

      <AlertDialog open={confirmOpen} onOpenChange={setConfirmOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete skill?</AlertDialogTitle>
            <AlertDialogDescription>
              This permanently removes{" "}
              <span className="font-mono">{skill.name}</span> and all of its
              revisions.
            </AlertDialogDescription>
          </AlertDialogHeader>
          {deleteError ? (
            <Alert variant="destructive">
              <AlertDescription>{deleteError}</AlertDescription>
            </Alert>
          ) : null}
          <AlertDialogFooter>
            <AlertDialogCancel disabled={deleting}>Cancel</AlertDialogCancel>
            <AlertDialogAction
              variant="destructive"
              onClick={(e) => {
                e.preventDefault();
                void handleDelete();
              }}
              disabled={deleting}
            >
              {deleting ? "Deleting…" : "Delete"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}

function HistoryPanel({
  slug,
  skillId,
  revisions,
}: {
  slug: string;
  skillId: string;
  revisions: SkillRevisionSummary[];
}) {
  const [selected, setSelected] = React.useState<SkillRevision | null>(null);
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  async function open(version: number) {
    setError(null);
    setLoading(true);
    try {
      const rev = await api<SkillRevision>(
        `/api/workspaces/${slug}/skills/${skillId}/revisions/${version}`
      );
      setSelected(rev);
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Failed to load revision."
      );
    } finally {
      setLoading(false);
    }
  }

  if (revisions.length === 0) {
    return (
      <Card>
        <CardHeader>
          <CardTitle className="text-base">No history yet</CardTitle>
          <CardDescription>
            Edits to this skill will appear here as numbered revisions.
          </CardDescription>
        </CardHeader>
      </Card>
    );
  }

  return (
    <div className="grid gap-4 md:grid-cols-[260px_1fr]">
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Revisions</CardTitle>
          <CardDescription>Most recent first.</CardDescription>
        </CardHeader>
        <CardContent className="p-0">
          <ul className="flex flex-col">
            {revisions.map((rev) => {
              const active = selected?.version === rev.version;
              return (
                <li key={rev.version}>
                  <button
                    type="button"
                    onClick={() => open(rev.version)}
                    className={`flex w-full flex-col items-start gap-0.5 border-b px-4 py-3 text-left last:border-0 transition-colors hover:bg-muted/60 ${
                      active ? "bg-muted" : ""
                    }`}
                  >
                    <span className="font-mono text-xs">v{rev.version}</span>
                    <span className="text-xs text-muted-foreground">
                      {formatDate(rev.createdAt)}
                    </span>
                    {rev.changeNote ? (
                      <span
                        className="text-xs text-foreground/80"
                        title={rev.changeNote}
                      >
                        {rev.changeNote.length > 60
                          ? `${rev.changeNote.slice(0, 60)}…`
                          : rev.changeNote}
                      </span>
                    ) : (
                      <span className="text-xs text-muted-foreground italic">
                        (no change note)
                      </span>
                    )}
                  </button>
                </li>
              );
            })}
          </ul>
        </CardContent>
      </Card>

      <div className="flex flex-col gap-2">
        {error ? (
          <Alert variant="destructive">
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        ) : null}

        {loading ? (
          <div className="flex h-[500px] items-center justify-center rounded-md border border-border text-sm text-muted-foreground">
            Loading revision…
          </div>
        ) : selected ? (
          <>
            <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1 text-xs text-muted-foreground">
              <span className="font-mono text-foreground">
                v{selected.version}
              </span>
              <span>{formatDate(selected.createdAt)}</span>
              <span className="font-mono">{selected.name}</span>
              <span>{selected.enabled ? "enabled" : "disabled"}</span>
            </div>
            <div className="text-sm">
              <span className="text-muted-foreground">title: </span>
              {selected.title}
            </div>
            <div className="text-sm">
              <span className="text-muted-foreground">description: </span>
              {selected.description}
            </div>
            {selected.triggers.length > 0 ? (
              <div className="flex flex-wrap items-center gap-1 text-xs">
                <span className="text-muted-foreground">triggers:</span>
                {selected.triggers.map((t) => (
                  <span
                    key={t}
                    className="rounded border border-border bg-muted px-1.5 py-0.5 font-mono"
                  >
                    {t}
                  </span>
                ))}
              </div>
            ) : null}
            <SkillMarkdownEditor
              value={selected.body}
              readOnly
              height={500}
            />
          </>
        ) : (
          <div className="flex h-[500px] items-center justify-center rounded-md border border-dashed border-border text-sm text-muted-foreground">
            Select a revision to view its body.
          </div>
        )}
      </div>
    </div>
  );
}
