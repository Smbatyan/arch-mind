"use client";

import { useRouter } from "next/navigation";
import * as React from "react";
import { PlusIcon, FolderGitIcon, CalendarIcon, ShieldIcon } from "lucide-react";

import { api } from "@/lib/api";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
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
import { OnboardingChecklist } from "@/components/onboarding-checklist";

export type Workspace = {
  id: string;
  slug: string;
  name: string;
  role: string;
  createdAt: string;
};

type CreateWorkspaceResponse = {
  id: string;
  slug: string;
  name: string;
  createdAt: string;
  role?: string;
};

const SLUG_REGEX = /^[a-z0-9](?:[a-z0-9-]{1,48}[a-z0-9])?$/;

const dateFormatter = new Intl.DateTimeFormat("en-US", { dateStyle: "medium" });

function formatDate(value: string): string {
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return dateFormatter.format(d);
}

// Pick a deterministic color from the workspace slug
const GRADIENT_PRESETS = [
  "linear-gradient(135deg, oklch(0.63 0.245 295), oklch(0.59 0.200 265))",  // violet→indigo
  "linear-gradient(135deg, oklch(0.59 0.200 265), oklch(0.66 0.180 235))",  // indigo→sky
  "linear-gradient(135deg, oklch(0.66 0.180 235), oklch(0.66 0.180 155))",  // sky→emerald
  "linear-gradient(135deg, oklch(0.66 0.180 155), oklch(0.75 0.180 75))",   // emerald→amber
  "linear-gradient(135deg, oklch(0.75 0.180 75),  oklch(0.66 0.220 015))",  // amber→rose
];

function slugGradient(slug: string): string {
  let hash = 0;
  for (let i = 0; i < slug.length; i++) hash = (hash * 31 + slug.charCodeAt(i)) | 0;
  return GRADIENT_PRESETS[Math.abs(hash) % GRADIENT_PRESETS.length];
}

export function WorkspacesView({
  initialWorkspaces,
}: {
  initialWorkspaces: Workspace[];
}) {
  const router = useRouter();
  const [workspaces, setWorkspaces] = React.useState<Workspace[]>(initialWorkspaces);
  const [dialogOpen, setDialogOpen] = React.useState(false);
  const openDialog = React.useCallback(() => setDialogOpen(true), []);

  return (
    <>
      {workspaces.length === 0 ? (
        <EmptyState onCreate={openDialog} />
      ) : (
        <div className="flex flex-col gap-6">
          {/* Header */}
          <div className="flex items-center justify-between animate-fade-up">
            <div>
              <h1 className="text-xl font-semibold">Workspaces</h1>
              <p className="mt-0.5 text-sm text-muted-foreground">
                {workspaces.length} workspace{workspaces.length !== 1 ? "s" : ""}
              </p>
            </div>
            <Button
              onClick={openDialog}
              className="gap-1.5"
            >
              <PlusIcon className="size-3.5" />
              New workspace
            </Button>
          </div>

          {/* Grid */}
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {workspaces.map((ws, idx) => (
              <WorkspaceCard
                key={ws.id}
                workspace={ws}
                delay={idx * 60}
                onSelect={() => router.push(`/workspaces/${ws.slug}`)}
              />
            ))}
          </div>
        </div>
      )}

      <CreateWorkspaceDialog
        open={dialogOpen}
        onOpenChange={setDialogOpen}
        onCreated={(ws) => {
          setWorkspaces((prev) => [ws, ...prev]);
          setDialogOpen(false);
          router.push(`/workspaces/${ws.slug}`);
        }}
      />
    </>
  );
}

function WorkspaceCard({
  workspace,
  delay,
  onSelect,
}: {
  workspace: Workspace;
  delay: number;
  onSelect: () => void;
}) {
  const gradient = slugGradient(workspace.slug);

  return (
    <button
      type="button"
      onClick={onSelect}
      className="animate-fade-up group relative overflow-hidden rounded-xl border border-border bg-card p-5 text-left transition-all duration-200 hover:border-primary/30 hover:shadow-lg hover:shadow-primary/5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      style={{ "--delay": `${delay}ms` } as React.CSSProperties}
    >
      {/* Top gradient strip */}
      <div
        className="pointer-events-none absolute inset-x-0 top-0 h-0.5 transition-opacity duration-200 group-hover:opacity-100 opacity-60"
        style={{ background: gradient }}
        aria-hidden
      />

      {/* Avatar */}
      <div className="mb-4 flex items-center gap-3">
        <div
          className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl text-sm font-bold text-white shadow-sm"
          style={{ background: gradient }}
          aria-hidden
        >
          {workspace.name.slice(0, 1).toUpperCase()}
        </div>
        <div className="min-w-0">
          <p className="truncate font-semibold leading-tight">{workspace.name}</p>
          <p className="truncate font-mono text-xs text-muted-foreground leading-tight mt-0.5">
            {workspace.slug}
          </p>
        </div>
      </div>

      {/* Meta */}
      <div className="flex items-center justify-between gap-2 text-xs text-muted-foreground">
        <div className="flex items-center gap-1">
          <ShieldIcon className="size-3" />
          <span className="capitalize">{workspace.role}</span>
        </div>
        <div className="flex items-center gap-1">
          <CalendarIcon className="size-3" />
          <span>{formatDate(workspace.createdAt)}</span>
        </div>
      </div>

      {/* Hover arrow hint */}
      <div className="absolute right-4 top-1/2 -translate-y-1/2 opacity-0 transition-opacity duration-150 group-hover:opacity-100">
        <div className="flex h-7 w-7 items-center justify-center rounded-full bg-primary/10">
          <FolderGitIcon className="size-3.5 text-primary" />
        </div>
      </div>
    </button>
  );
}

function EmptyState({ onCreate }: { onCreate: () => void }) {
  return (
    <div className="flex justify-center py-12 animate-fade-up">
      <div className="w-full max-w-md">
        <OnboardingChecklist
          primaryActionLabel="Create workspace"
          onPrimaryAction={onCreate}
          showDismiss={false}
        />
      </div>
    </div>
  );
}

function CreateWorkspaceDialog({
  open,
  onOpenChange,
  onCreated,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onCreated: (ws: Workspace) => void;
}) {
  const [name, setName] = React.useState("");
  const [slug, setSlug] = React.useState("");
  const [slugTouched, setSlugTouched] = React.useState(false);
  const [submitting, setSubmitting] = React.useState(false);
  const [serverError, setServerError] = React.useState<string | null>(null);

  React.useEffect(() => {
    if (!open) {
      setName("");
      setSlug("");
      setSlugTouched(false);
      setSubmitting(false);
      setServerError(null);
    }
  }, [open]);

  // Auto-derive slug from name
  React.useEffect(() => {
    if (!slugTouched && name) {
      setSlug(
        name
          .toLowerCase()
          .replace(/\s+/g, "-")
          .replace(/[^a-z0-9-]/g, "")
          .slice(0, 50)
      );
    }
  }, [name, slugTouched]);

  const slugValid = SLUG_REGEX.test(slug);
  const slugError =
    slugTouched && slug.length > 0 && !slugValid
      ? "2–50 chars, lowercase letters, digits, and hyphens; start/end with letter or digit."
      : null;

  async function handleSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setServerError(null);
    setSlugTouched(true);
    if (!name.trim() || !slugValid) return;

    setSubmitting(true);
    try {
      const created = await api<CreateWorkspaceResponse>("/api/workspaces", {
        method: "POST",
        body: JSON.stringify({ name: name.trim(), slug }),
      });
      const workspace: Workspace = {
        id: created.id,
        slug: created.slug,
        name: created.name,
        role: created.role ?? "owner",
        createdAt: created.createdAt,
      };
      onCreated(workspace);
    } catch (err) {
      setServerError(
        err instanceof Error ? err.message : "Failed to create workspace."
      );
      setSubmitting(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Create workspace</DialogTitle>
          <DialogDescription>
            Workspaces isolate your repos, skills, and questions.
          </DialogDescription>
        </DialogHeader>
        <form onSubmit={handleSubmit} className="flex flex-col gap-4 pt-1">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="ws-name">Name</Label>
            <Input
              id="ws-name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="My Workspace"
              autoFocus
              required
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="ws-slug">Slug</Label>
            <Input
              id="ws-slug"
              value={slug}
              onChange={(e) => {
                setSlugTouched(true);
                setSlug(e.target.value.toLowerCase().replace(/[^a-z0-9-]/g, ""));
              }}
              onBlur={() => setSlugTouched(true)}
              placeholder="my-workspace"
              className="font-mono"
              required
              aria-invalid={slugError ? true : undefined}
            />
            {slugError ? (
              <p className="text-xs text-destructive">{slugError}</p>
            ) : (
              <p className="text-xs text-muted-foreground">
                Used in URLs. Auto-derived from name.
              </p>
            )}
          </div>

          {serverError && (
            <Alert variant="destructive">
              <AlertDescription>{serverError}</AlertDescription>
            </Alert>
          )}

          <DialogFooter>
            <Button
              type="button"
              variant="outline"
              onClick={() => onOpenChange(false)}
              disabled={submitting}
            >
              Cancel
            </Button>
            <Button type="submit" disabled={submitting || !name.trim() || !slugValid}>
              {submitting ? "Creating…" : "Create workspace"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
