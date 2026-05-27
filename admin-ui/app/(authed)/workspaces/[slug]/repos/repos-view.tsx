"use client";

import { ExternalLinkIcon, PlusIcon } from "lucide-react";
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
import { Button, buttonVariants } from "@/components/ui/button";
import { RepoStatusBadge, type RepoStatus } from "@/components/repo-status-badge";
import { cn } from "@/lib/utils";
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

export type { RepoStatus };

export type RepoSummary = {
  id: string;
  name: string;
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
  name: string;
  githubUrl: string;
  defaultBranch: string;
  status: RepoStatus;
  createdAt: string;
};

const GITHUB_REPO_URL_REGEX =
  /^https:\/\/github\.com\/[^/]+\/[^/]+?(?:\.git)?$/;

// Bare org / user URL: https://github.com/<owner> with no second segment
const GITHUB_ORG_URL_REGEX =
  /^https:\/\/github\.com\/[^/]+\/?$/;

type DiscoveredRepo = {
  name: string;
  githubUrl: string;
  defaultBranch: string;
  private: boolean;
  description: string | null;
};

type DiscoverResponse = {
  owner: string;
  repos: DiscoveredRepo[];
};

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
          <div className="flex items-center justify-between animate-fade-up">
            <p className="text-sm text-muted-foreground">
              {repos.length} repo{repos.length !== 1 ? "s" : ""}
            </p>
            <Button onClick={openAdd} className="gap-1.5">
              <PlusIcon className="size-3.5" />
              Add repo
            </Button>
          </div>
          <div className="flex flex-col gap-3">
            {repos.map((repo, idx) => (
              <RepoCard
                key={repo.id}
                slug={slug}
                repo={repo}
                delay={(idx + 1) * 60}
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
  delay = 0,
}: {
  slug: string;
  repo: RepoSummary;
  onDeleted: (id: string) => void;
  delay?: number;
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
    <Card
      className="animate-fade-up card-hover"
      style={{ "--delay": `${delay}ms` } as React.CSSProperties}
    >
      <CardHeader>
        <div className="flex items-start justify-between gap-3">
          <div className="flex flex-col gap-1 min-w-0">
            <CardTitle className="text-base font-semibold truncate">
              {repo.name || repo.githubUrl}
            </CardTitle>
            <CardDescription className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
              <a
                href={repo.githubUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1 font-mono hover:underline"
              >
                {repo.githubUrl}
                <ExternalLinkIcon className="size-3 shrink-0 text-muted-foreground" />
              </a>
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
              className={cn(buttonVariants({ variant: "outline", size: "sm" }))}
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
              This removes{" "}
              <span className="font-semibold">{repo.name || repo.githubUrl}</span>{" "}
              (<span className="font-mono text-xs">{repo.githubUrl}</span>) from
              this workspace. Ingested data remains until explicitly cleaned up.
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

type AddStep = "input" | "select";

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
  const [step, setStep] = React.useState<AddStep>("input");
  const [patToken, setPatToken] = React.useState("");
  const [url, setUrl] = React.useState("");
  const [defaultBranch, setDefaultBranch] = React.useState("");

  const [discovering, setDiscovering] = React.useState(false);
  const [submitting, setSubmitting] = React.useState(false);
  const [validationError, setValidationError] = React.useState<string | null>(null);
  const [failures, setFailures] = React.useState<SubmitFailure[]>([]);

  // Selection step state
  const [candidates, setCandidates] = React.useState<DiscoveredRepo[]>([]);
  const [selectedUrls, setSelectedUrls] = React.useState<Set<string>>(new Set());
  const [progress, setProgress] = React.useState<{ done: number; total: number } | null>(null);

  React.useEffect(() => {
    if (!open) {
      setStep("input");
      setPatToken("");
      setUrl("");
      setDefaultBranch("");
      setDiscovering(false);
      setSubmitting(false);
      setValidationError(null);
      setFailures([]);
      setCandidates([]);
      setSelectedUrls(new Set());
      setProgress(null);
    }
  }, [open]);

  function trimmedUrl(): string {
    return url.trim().replace(/\/$/, "");
  }

  async function handleContinue(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setValidationError(null);
    setFailures([]);

    if (!patToken.trim()) {
      setValidationError("PAT is required.");
      return;
    }
    const u = trimmedUrl();
    if (!u) {
      setValidationError("Enter a GitHub URL.");
      return;
    }

    const isRepo = GITHUB_REPO_URL_REGEX.test(u);
    const isOrg = !isRepo && GITHUB_ORG_URL_REGEX.test(u);

    if (isRepo) {
      // Skip selection: single repo, treat as one-item candidate list.
      setCandidates([{
        name: u.split("/").pop()?.replace(/\.git$/, "") ?? u,
        githubUrl: u,
        defaultBranch: defaultBranch.trim() || "main",
        private: false,
        description: null,
      }]);
      setSelectedUrls(new Set([u]));
      setStep("select");
      return;
    }

    if (!isOrg) {
      setValidationError(
        "URL must be either https://github.com/<org-or-user> or https://github.com/<owner>/<repo>."
      );
      return;
    }

    setDiscovering(true);
    try {
      const res = await api<DiscoverResponse>(
        `/api/workspaces/${slug}/repos/discover`,
        {
          method: "POST",
          body: JSON.stringify({ orgUrl: u, patToken: patToken.trim() }),
        }
      );
      if (res.repos.length === 0) {
        setValidationError(`No repositories visible for ${res.owner} with this PAT.`);
      } else {
        setCandidates(res.repos);
        setSelectedUrls(new Set(res.repos.map((r) => r.githubUrl)));
        setStep("select");
      }
    } catch (err) {
      setValidationError(
        err instanceof Error ? err.message : "Failed to fetch organization repos."
      );
    } finally {
      setDiscovering(false);
    }
  }

  function toggleOne(url: string) {
    setSelectedUrls((prev) => {
      const next = new Set(prev);
      if (next.has(url)) next.delete(url);
      else next.add(url);
      return next;
    });
  }

  function setAll(checked: boolean) {
    setSelectedUrls(new Set(checked ? candidates.map((c) => c.githubUrl) : []));
  }

  async function handleSubmitSelection() {
    setValidationError(null);
    setFailures([]);

    const picked = candidates.filter((c) => selectedUrls.has(c.githubUrl));
    if (picked.length === 0) {
      setValidationError("Select at least one repository.");
      return;
    }

    setSubmitting(true);
    setProgress({ done: 0, total: picked.length });

    const branchOverride = defaultBranch.trim();
    const created: RepoSummary[] = [];
    const newFailures: SubmitFailure[] = [];

    for (let i = 0; i < picked.length; i++) {
      const c = picked[i];
      try {
        const res = await api<CreateRepoResponse>(
          `/api/workspaces/${slug}/repos`,
          {
            method: "POST",
            body: JSON.stringify({
              githubUrl: c.githubUrl,
              defaultBranch: branchOverride || c.defaultBranch || "main",
              patToken: patToken.trim(),
              name: c.name,
            }),
          }
        );
        created.push({
          id: res.id,
          name: res.name,
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
          url: c.githubUrl,
          error: err instanceof Error ? err.message : "Failed to add repo.",
        });
      }
      setProgress({ done: i + 1, total: picked.length });
    }

    if (created.length > 0) onAdded(created);

    if (newFailures.length === 0) {
      onOpenChange(false);
    } else {
      setFailures(newFailures);
      setSubmitting(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className={step === "select" ? "sm:max-w-2xl" : "sm:max-w-md"}>
        <DialogHeader>
          <DialogTitle>
            {step === "input" ? "Add Repo" : "Select repositories"}
          </DialogTitle>
          <DialogDescription>
            {step === "input"
              ? "Paste a GitHub repo URL, or an org/user URL to pick from all visible repos."
              : `Backend processes 5 repos concurrently; the rest queue automatically.`}
          </DialogDescription>
        </DialogHeader>

        {step === "input" ? (
          <form onSubmit={handleContinue} className="flex flex-col gap-4">
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
              <Label htmlFor="repo-url">GitHub URL</Label>
              <Input
                id="repo-url"
                value={url}
                onChange={(e) => setUrl(e.target.value)}
                placeholder="https://github.com/owner   or   https://github.com/owner/repo"
                className="font-mono text-xs"
                required
              />
              <p className="text-xs text-muted-foreground">
                Org / user URL fans out to all visible repos; single-repo URL adds just that one.
              </p>
            </div>

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="repo-branch">Default branch override (optional)</Label>
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

            <DialogFooter>
              <Button
                type="button"
                variant="outline"
                onClick={() => onOpenChange(false)}
                disabled={discovering}
              >
                Cancel
              </Button>
              <Button type="submit" disabled={discovering}>
                {discovering ? "Discovering..." : "Continue"}
              </Button>
            </DialogFooter>
          </form>
        ) : (
          <div className="flex flex-col gap-3">
            <div className="flex items-center justify-between text-xs text-muted-foreground">
              <span>
                {selectedUrls.size} of {candidates.length} selected
              </span>
              <div className="flex gap-2">
                <button
                  type="button"
                  onClick={() => setAll(true)}
                  className="hover:text-foreground hover:underline"
                  disabled={submitting}
                >
                  Select all
                </button>
                <span>·</span>
                <button
                  type="button"
                  onClick={() => setAll(false)}
                  className="hover:text-foreground hover:underline"
                  disabled={submitting}
                >
                  Clear
                </button>
              </div>
            </div>

            <div className="max-h-96 overflow-y-auto rounded-md border border-border">
              <ul className="divide-y divide-border">
                {candidates.map((c) => {
                  const checked = selectedUrls.has(c.githubUrl);
                  return (
                    <li key={c.githubUrl} className="flex items-start gap-3 px-3 py-2">
                      <input
                        type="checkbox"
                        id={`pick-${c.githubUrl}`}
                        checked={checked}
                        onChange={() => toggleOne(c.githubUrl)}
                        disabled={submitting}
                        className="mt-1 size-4 cursor-pointer"
                      />
                      <label
                        htmlFor={`pick-${c.githubUrl}`}
                        className="flex min-w-0 flex-1 cursor-pointer flex-col"
                      >
                        <span className="flex items-center gap-2 truncate text-sm font-medium">
                          {c.name}
                          {c.private ? (
                            <span className="rounded bg-muted px-1.5 py-0.5 text-[0.65rem] uppercase text-muted-foreground">
                              private
                            </span>
                          ) : null}
                          <span className="text-xs font-normal text-muted-foreground">
                            {c.defaultBranch}
                          </span>
                        </span>
                        {c.description ? (
                          <span className="truncate text-xs text-muted-foreground">
                            {c.description}
                          </span>
                        ) : null}
                      </label>
                    </li>
                  );
                })}
              </ul>
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
                          <span className="font-mono">{f.url}</span>: {f.error}
                        </li>
                      ))}
                    </ul>
                  </div>
                </AlertDescription>
              </Alert>
            ) : null}

            {progress ? (
              <p className="text-xs text-muted-foreground">
                Adding {progress.done} of {progress.total}...
              </p>
            ) : null}

            <DialogFooter>
              <Button
                type="button"
                variant="outline"
                onClick={() => setStep("input")}
                disabled={submitting}
              >
                Back
              </Button>
              <Button
                type="button"
                onClick={() => void handleSubmitSelection()}
                disabled={submitting || selectedUrls.size === 0}
              >
                {submitting
                  ? `Adding ${progress?.done ?? 0}/${progress?.total ?? selectedUrls.size}...`
                  : `Connect ${selectedUrls.size}`}
              </Button>
            </DialogFooter>
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}
