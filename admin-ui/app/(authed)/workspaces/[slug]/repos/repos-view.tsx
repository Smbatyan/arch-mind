"use client";

import { ExternalLinkIcon } from "lucide-react";
import Link from "next/link";
import * as React from "react";

import { api } from "@/lib/api";
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
import { RepoStatusBadge, type RepoStatus } from "@/components/repo-status-badge";
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
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";

export type { RepoStatus };

export type RepoSummary = {
  id: string;
  githubUrl: string;
  defaultBranch: string;
  lastProcessedSha: string | null;
  status: RepoStatus;
  errorMessage: string | null;
  createdAt: string;
  updatedAt: string;
};

type CreateRepoResponse = {
  id: string;
  githubUrl: string;
  defaultBranch: string;
  status: RepoStatus;
  createdAt: string;
};

const GITHUB_URL_REGEX =
  /^https:\/\/github\.com\/[^/]+\/[^/]+?(?:\.git)?$/;

const dateFormatter = new Intl.DateTimeFormat("en-US", {
  dateStyle: "medium",
  timeStyle: "short",
});

function formatDate(value: string): string {
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return dateFormatter.format(d);
}

function shortSha(sha: string | null): string | null {
  if (!sha) return null;
  return sha.slice(0, 7);
}

export function ReposView({
  slug,
  initialRepos,
}: {
  slug: string;
  initialRepos: RepoSummary[];
}) {
  const [repos, setRepos] = React.useState<RepoSummary[]>(initialRepos);
  const [addOpen, setAddOpen] = React.useState(false);

  const openAdd = React.useCallback(() => setAddOpen(true), []);

  const handleAdded = React.useCallback((created: RepoSummary[]) => {
    setRepos((prev) => [...created, ...prev]);
  }, []);

  const handleDeleted = React.useCallback((id: string) => {
    setRepos((prev) => prev.filter((r) => r.id !== id));
  }, []);

  return (
    <>
      {repos.length === 0 ? (
        <EmptyState onAdd={openAdd} />
      ) : (
        <>
          <div className="flex items-center justify-end">
            <Button onClick={openAdd}>Add Repo</Button>
          </div>
          <div className="flex flex-col gap-3">
            {repos.map((repo) => (
              <RepoCard
                key={repo.id}
                slug={slug}
                repo={repo}
                onDeleted={handleDeleted}
              />
            ))}
          </div>
        </>
      )}

      <AddRepoDialog
        slug={slug}
        open={addOpen}
        onOpenChange={setAddOpen}
        onAdded={handleAdded}
      />
    </>
  );
}

function EmptyState({ onAdd }: { onAdd: () => void }) {
  return (
    <div className="flex justify-center py-12">
      <Card className="w-full max-w-md">
        <CardHeader>
          <CardTitle>No repos connected</CardTitle>
          <CardDescription>
            Add one to start ingestion.
          </CardDescription>
        </CardHeader>
        <CardContent className="flex justify-center pb-4">
          <Button onClick={onAdd}>Add Repo</Button>
        </CardContent>
      </Card>
    </div>
  );
}

function RepoCard({
  slug,
  repo,
  onDeleted,
}: {
  slug: string;
  repo: RepoSummary;
  onDeleted: (id: string) => void;
}) {
  const [confirmOpen, setConfirmOpen] = React.useState(false);
  const [deleting, setDeleting] = React.useState(false);
  const [deleteError, setDeleteError] = React.useState<string | null>(null);
  const sha = shortSha(repo.lastProcessedSha);

  async function handleDelete() {
    setDeleting(true);
    setDeleteError(null);
    try {
      await api<void>(
        `/api/workspaces/${slug}/repos/${repo.id}`,
        { method: "DELETE" }
      );
      setConfirmOpen(false);
      onDeleted(repo.id);
    } catch (err) {
      setDeleteError(
        err instanceof Error ? err.message : "Failed to disconnect repo."
      );
      setDeleting(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <div className="flex items-start justify-between gap-3">
          <div className="flex flex-col gap-1 min-w-0">
            <CardTitle className="flex items-center gap-1.5">
              <a
                href={repo.githubUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="font-mono text-sm break-all hover:underline"
              >
                {repo.githubUrl}
              </a>
              <ExternalLinkIcon className="size-3.5 shrink-0 text-muted-foreground" />
            </CardTitle>
            <CardDescription className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
              <span className="font-mono">{repo.defaultBranch}</span>
              {sha ? (
                <span className="font-mono text-muted-foreground">
                  {sha}
                </span>
              ) : null}
              <span className="text-muted-foreground">
                {formatDate(repo.createdAt)}
              </span>
            </CardDescription>
          </div>
          <div className="flex shrink-0 items-center gap-2">
            <RepoStatusBadge status={repo.status} />
            <Link
              href={`/workspaces/${slug}/repos/${repo.id}`}
              className="inline-flex h-7 items-center justify-center rounded-[min(var(--radius-md),12px)] border border-border bg-background px-2.5 text-[0.8rem] font-medium transition-colors hover:bg-muted hover:text-foreground"
            >
              Details
            </Link>
            <Button
              variant="destructive"
              size="sm"
              onClick={() => setConfirmOpen(true)}
            >
              Disconnect
            </Button>
          </div>
        </div>
      </CardHeader>
      {repo.status === "failed" && repo.errorMessage ? (
        <CardContent>
          <Alert variant="destructive">
            <AlertDescription className="font-mono text-xs">
              {repo.errorMessage}
            </AlertDescription>
          </Alert>
        </CardContent>
      ) : null}

      <AlertDialog open={confirmOpen} onOpenChange={setConfirmOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Disconnect repository?</AlertDialogTitle>
            <AlertDialogDescription>
              This removes <span className="font-mono">{repo.githubUrl}</span>{" "}
              from this workspace. Ingested data remains until explicitly
              cleaned up.
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
              {deleting ? "Disconnecting..." : "Disconnect"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </Card>
  );
}

type SubmitFailure = { url: string; error: string };

function AddRepoDialog({
  slug,
  open,
  onOpenChange,
  onAdded,
}: {
  slug: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onAdded: (created: RepoSummary[]) => void;
}) {
  const [patToken, setPatToken] = React.useState("");
  const [urlsText, setUrlsText] = React.useState("");
  const [defaultBranch, setDefaultBranch] = React.useState("");
  const [submitting, setSubmitting] = React.useState(false);
  const [validationError, setValidationError] = React.useState<string | null>(
    null
  );
  const [failures, setFailures] = React.useState<SubmitFailure[]>([]);

  React.useEffect(() => {
    if (!open) {
      setPatToken("");
      setUrlsText("");
      setDefaultBranch("");
      setSubmitting(false);
      setValidationError(null);
      setFailures([]);
    }
  }, [open]);

  function parseUrls(): { valid: string[]; invalid: string[] } {
    const lines = urlsText
      .split(/\r?\n/)
      .map((l) => l.trim())
      .filter((l) => l.length > 0);
    const valid: string[] = [];
    const invalid: string[] = [];
    for (const line of lines) {
      if (GITHUB_URL_REGEX.test(line)) valid.push(line);
      else invalid.push(line);
    }
    return { valid, invalid };
  }

  async function handleSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setValidationError(null);
    setFailures([]);

    if (!patToken.trim()) {
      setValidationError("PAT is required.");
      return;
    }

    const { valid, invalid } = parseUrls();
    if (invalid.length > 0) {
      setValidationError(
        `Invalid GitHub URL(s): ${invalid.join(", ")}. Expected: https://github.com/owner/repo`
      );
      return;
    }
    if (valid.length === 0) {
      setValidationError("Provide at least one repository URL.");
      return;
    }

    setSubmitting(true);

    const branch = defaultBranch.trim() || undefined;
    const created: RepoSummary[] = [];
    const newFailures: SubmitFailure[] = [];

    for (const url of valid) {
      try {
        const res = await api<CreateRepoResponse>(
          `/api/workspaces/${slug}/repos`,
          {
            method: "POST",
            body: JSON.stringify({
              githubUrl: url,
              defaultBranch: branch,
              patToken: patToken.trim(),
            }),
          }
        );
        created.push({
          id: res.id,
          githubUrl: res.githubUrl,
          defaultBranch: res.defaultBranch,
          status: res.status,
          lastProcessedSha: null,
          errorMessage: null,
          createdAt: res.createdAt,
          updatedAt: res.createdAt,
        });
      } catch (err) {
        newFailures.push({
          url,
          error:
            err instanceof Error ? err.message : "Failed to add repo.",
        });
      }
    }

    if (created.length > 0) {
      onAdded(created);
    }

    if (newFailures.length === 0) {
      onOpenChange(false);
    } else {
      setFailures(newFailures);
      setSubmitting(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Add Repo</DialogTitle>
          <DialogDescription>
            Connect one or more GitHub repositories to this workspace.
          </DialogDescription>
        </DialogHeader>
        <form onSubmit={handleSubmit} className="flex flex-col gap-4">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="repo-pat">GitHub Personal Access Token</Label>
            <Input
              id="repo-pat"
              type="password"
              value={patToken}
              onChange={(e) => setPatToken(e.target.value)}
              placeholder="ghp_..."
              autoComplete="off"
              required
            />
            <p className="text-xs text-muted-foreground">
              Scope: <span className="font-mono">repo</span> (read).
            </p>
          </div>

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="repo-urls">Repository URLs</Label>
            <Textarea
              id="repo-urls"
              value={urlsText}
              onChange={(e) => setUrlsText(e.target.value)}
              placeholder={"https://github.com/owner/repo\nhttps://github.com/owner/another"}
              className="font-mono text-xs min-h-24"
              required
            />
            <p className="text-xs text-muted-foreground">
              One URL per line. Format: https://github.com/owner/repo
            </p>
          </div>

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="repo-branch">Default branch (optional)</Label>
            <Input
              id="repo-branch"
              value={defaultBranch}
              onChange={(e) => setDefaultBranch(e.target.value)}
              placeholder="main"
              className="font-mono"
            />
          </div>

          {validationError ? (
            <Alert variant="destructive">
              <AlertDescription>{validationError}</AlertDescription>
            </Alert>
          ) : null}

          {failures.length > 0 ? (
            <Alert variant="destructive">
              <AlertDescription>
                <div className="flex flex-col gap-1">
                  <span>
                    {failures.length} repo
                    {failures.length === 1 ? "" : "s"} failed to add:
                  </span>
                  <ul className="list-disc pl-5">
                    {failures.map((f) => (
                      <li key={f.url} className="text-xs">
                        <span className="font-mono">{f.url}</span>:{" "}
                        {f.error}
                      </li>
                    ))}
                  </ul>
                </div>
              </AlertDescription>
            </Alert>
          ) : null}

          <DialogFooter>
            <Button
              type="button"
              variant="outline"
              onClick={() => onOpenChange(false)}
              disabled={submitting}
            >
              Cancel
            </Button>
            <Button type="submit" disabled={submitting}>
              {submitting ? "Adding..." : "Add"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
