"use client";

import { ArrowLeftIcon, ExternalLinkIcon } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import * as React from "react";

import { api } from "@/lib/api";
import { RepoStatusBadge, type RepoStatus } from "@/components/repo-status-badge";
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

export type RepoDetail = {
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

export type ScanRun = {
  id: string;
  kind: string;
  status: string;
  startedAt: string | null;
  completedAt: string | null;
  filesScanned: number | null;
  errorMessage: string | null;
};

const dateFormatter = new Intl.DateTimeFormat("en-US", {
  dateStyle: "medium",
  timeStyle: "short",
});

function formatDate(value: string | null | undefined): string {
  if (!value) return "—";
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return dateFormatter.format(d);
}

function shortSha(sha: string | null): string | null {
  if (!sha) return null;
  return sha.slice(0, 7);
}

function truncate(s: string | null, n: number): string {
  if (!s) return "";
  return s.length > n ? `${s.slice(0, n)}…` : s;
}

const POLL_MS = 10_000;

export function RepoDetailView({
  slug,
  repoId,
  initialRepo,
  initialScans,
}: {
  slug: string;
  repoId: string;
  initialRepo: RepoDetail;
  initialScans: ScanRun[] | null;
}) {
  const router = useRouter();
  const [repo, setRepo] = React.useState<RepoDetail>(initialRepo);
  const [scans, setScans] = React.useState<ScanRun[] | null>(initialScans);
  const [confirmOpen, setConfirmOpen] = React.useState(false);
  const [deleting, setDeleting] = React.useState(false);
  const [deleteError, setDeleteError] = React.useState<string | null>(null);
  const [rescanConfirmOpen, setRescanConfirmOpen] = React.useState(false);
  const [rescanning, setRescanning] = React.useState(false);
  const [rescanMessage, setRescanMessage] = React.useState<
    | { kind: "info" | "error"; text: string }
    | null
  >(null);

  const scansAvailable = initialScans !== null;
  const sha = shortSha(repo.lastProcessedSha);

  // Polling while scanning
  React.useEffect(() => {
    if (repo.status !== "scanning") return;
    let cancelled = false;

    const tick = async () => {
      try {
        const next = await api<RepoDetail>(
          `/api/workspaces/${slug}/repos/${repoId}`
        );
        if (cancelled) return;
        setRepo(next);
      } catch {
        // swallow; will retry on next tick
      }
      if (scansAvailable) {
        try {
          const nextScans = await api<ScanRun[]>(
            `/api/workspaces/${slug}/repos/${repoId}/scans?limit=20`
          );
          if (cancelled) return;
          setScans(nextScans);
        } catch {
          // swallow
        }
      }
    };

    const intervalId = setInterval(tick, POLL_MS);
    return () => {
      cancelled = true;
      clearInterval(intervalId);
    };
  }, [repo.status, slug, repoId, scansAvailable]);

  async function handleDisconnect() {
    setDeleting(true);
    setDeleteError(null);
    try {
      await api<void>(
        `/api/workspaces/${slug}/repos/${repoId}`,
        { method: "DELETE" }
      );
      setConfirmOpen(false);
      router.push(`/workspaces/${slug}/repos`);
    } catch (err) {
      setDeleteError(
        err instanceof Error ? err.message : "Failed to disconnect repo."
      );
      setDeleting(false);
    }
  }

  async function handleRescan() {
    setRescanning(true);
    setRescanMessage(null);
    try {
      await api<{ message: string }>(
        `/api/workspaces/${slug}/repos/${repoId}/rescan`,
        { method: "POST" }
      );
      // Optimistically flip to scanning so polling effect kicks in.
      setRepo((prev) => ({ ...prev, status: "scanning" }));
      setRescanConfirmOpen(false);
      // Refresh server-rendered scan history.
      router.refresh();
    } catch (err) {
      const text =
        err instanceof Error ? err.message : "Failed to start re-scan.";
      if (text === "already scanning") {
        setRescanMessage({
          kind: "info",
          text: "Already scanning — please wait.",
        });
        // Reflect server state locally so polling resumes.
        setRepo((prev) => ({ ...prev, status: "scanning" }));
        setRescanConfirmOpen(false);
      } else {
        setRescanMessage({ kind: "error", text });
      }
    } finally {
      setRescanning(false);
    }
  }

  return (
    <>
      <div className="flex items-center gap-2">
        <Link
          href={`/workspaces/${slug}/repos`}
          className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ArrowLeftIcon className="size-3.5" />
          Repositories
        </Link>
      </div>

      <Card>
        <CardHeader>
          <div className="flex items-start justify-between gap-3">
            <div className="flex flex-col gap-1 min-w-0">
              <CardTitle className="text-lg font-semibold truncate">
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
                <span>
                  branch:{" "}
                  <span className="font-mono text-foreground">
                    {repo.defaultBranch}
                  </span>
                </span>
                {sha ? (
                  <span>
                    sha:{" "}
                    <span className="font-mono text-foreground">{sha}</span>
                  </span>
                ) : null}
                <span>files extracted: —</span>
                <span>last scan: {formatDate(repo.updatedAt)}</span>
              </CardDescription>
            </div>
            <div className="shrink-0">
              <RepoStatusBadge status={repo.status} />
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
      </Card>

      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="outline"
          size="sm"
          onClick={() => {
            setRescanMessage(null);
            setRescanConfirmOpen(true);
          }}
          disabled={repo.status === "scanning" || rescanning}
        >
          Re-scan
        </Button>
        <Button
          variant="destructive"
          size="sm"
          onClick={() => setConfirmOpen(true)}
        >
          Disconnect
        </Button>
      </div>

      {rescanMessage ? (
        <Alert
          variant={rescanMessage.kind === "error" ? "destructive" : "default"}
        >
          <AlertDescription>{rescanMessage.text}</AlertDescription>
        </Alert>
      ) : null}

      {scansAvailable && scans && scans.length > 0 ? (
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Scan history</CardTitle>
            <CardDescription>Recent scan runs</CardDescription>
          </CardHeader>
          <CardContent>
            <div className="overflow-x-auto">
              <table className="w-full text-xs">
                <thead>
                  <tr className="border-b text-left text-muted-foreground">
                    <th className="py-2 pr-3 font-medium">Kind</th>
                    <th className="py-2 pr-3 font-medium">Status</th>
                    <th className="py-2 pr-3 font-medium">Started</th>
                    <th className="py-2 pr-3 font-medium">Completed</th>
                    <th className="py-2 pr-3 font-medium">Files</th>
                    <th className="py-2 pr-3 font-medium">Error</th>
                  </tr>
                </thead>
                <tbody>
                  {scans.map((s) => (
                    <tr key={s.id} className="border-b last:border-0">
                      <td className="py-2 pr-3 font-mono">{s.kind}</td>
                      <td className="py-2 pr-3 font-mono">{s.status}</td>
                      <td className="py-2 pr-3">{formatDate(s.startedAt)}</td>
                      <td className="py-2 pr-3">{formatDate(s.completedAt)}</td>
                      <td className="py-2 pr-3 font-mono">
                        {s.filesScanned ?? "—"}
                      </td>
                      <td
                        className="py-2 pr-3 font-mono text-destructive"
                        title={s.errorMessage ?? undefined}
                      >
                        {truncate(s.errorMessage, 60)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </CardContent>
        </Card>
      ) : null}

      <AlertDialog
        open={rescanConfirmOpen}
        onOpenChange={(open) => {
          if (!rescanning) setRescanConfirmOpen(open);
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Re-scan repository?</AlertDialogTitle>
            <AlertDialogDescription>
              This will re-extract the entire repo. The graph stays available
              during the scan.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={rescanning}>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={(e) => {
                e.preventDefault();
                void handleRescan();
              }}
              disabled={rescanning}
            >
              {rescanning ? "Starting..." : "Re-scan"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

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
                void handleDisconnect();
              }}
              disabled={deleting}
            >
              {deleting ? "Disconnecting..." : "Disconnect"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}
