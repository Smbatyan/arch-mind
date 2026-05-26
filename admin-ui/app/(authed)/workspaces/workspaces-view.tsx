"use client";

// TODO: introduce ActiveWorkspaceContext when child routes need the slug without re-fetching.

import { useRouter } from "next/navigation";
import * as React from "react";

import { api } from "@/lib/api";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
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

const dateFormatter = new Intl.DateTimeFormat("en-US", {
  dateStyle: "medium",
});

function formatDate(value: string): string {
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return dateFormatter.format(d);
}

export function WorkspacesView({
  initialWorkspaces,
}: {
  initialWorkspaces: Workspace[];
}) {
  const router = useRouter();
  const [workspaces, setWorkspaces] =
    React.useState<Workspace[]>(initialWorkspaces);
  const [dialogOpen, setDialogOpen] = React.useState(false);

  const openDialog = React.useCallback(() => setDialogOpen(true), []);

  return (
    <>
      {workspaces.length === 0 ? (
        <EmptyState onCreate={openDialog} />
      ) : (
        <>
          <div className="flex items-center justify-end">
            <Button onClick={openDialog}>Create Workspace</Button>
          </div>
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {workspaces.map((ws) => (
              <WorkspaceCard
                key={ws.id}
                workspace={ws}
                onSelect={() => router.push(`/workspaces/${ws.slug}`)}
              />
            ))}
          </div>
        </>
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
  onSelect,
}: {
  workspace: Workspace;
  onSelect: () => void;
}) {
  return (
    <Card
      role="button"
      tabIndex={0}
      onClick={onSelect}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          onSelect();
        }
      }}
      className="cursor-pointer transition-shadow hover:ring-foreground/20"
    >
      <CardHeader>
        <CardTitle className="font-semibold">{workspace.name}</CardTitle>
        <CardDescription className="font-mono text-xs">
          {workspace.slug}
        </CardDescription>
      </CardHeader>
      <CardContent className="flex items-center justify-between gap-2">
        <Badge variant="secondary">{workspace.role}</Badge>
        <span className="text-xs text-muted-foreground">
          {formatDate(workspace.createdAt)}
        </span>
      </CardContent>
    </Card>
  );
}

function EmptyState({ onCreate }: { onCreate: () => void }) {
  return (
    <div className="flex justify-center py-12">
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

  // Reset form when dialog closes.
  React.useEffect(() => {
    if (!open) {
      setName("");
      setSlug("");
      setSlugTouched(false);
      setSubmitting(false);
      setServerError(null);
    }
  }, [open]);

  const slugValid = SLUG_REGEX.test(slug);
  const slugError =
    slugTouched && slug.length > 0 && !slugValid
      ? "Slug must be 2-50 chars, lowercase letters, digits, and hyphens; must start/end with a letter or digit."
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
          <DialogTitle>Create Workspace</DialogTitle>
          <DialogDescription>
            Workspaces isolate your repos, skills, and questions.
          </DialogDescription>
        </DialogHeader>
        <form onSubmit={handleSubmit} className="flex flex-col gap-4">
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
              onChange={(e) =>
                setSlug(
                  e.target.value.toLowerCase().replace(/[^a-z0-9-]/g, "")
                )
              }
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
                Lowercase letters, digits, and hyphens. Used in URLs.
              </p>
            )}
          </div>

          {serverError ? (
            <Alert variant="destructive">
              <AlertDescription>{serverError}</AlertDescription>
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
            <Button
              type="submit"
              disabled={submitting || !name.trim() || !slugValid}
            >
              {submitting ? "Creating..." : "Create"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
