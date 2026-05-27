"use client";

import { CheckIcon, CopyIcon, PlusIcon } from "lucide-react";
import * as React from "react";

import { api } from "@/lib/api";
import type { WorkspaceApiKey, WorkspaceApiKeyCreated } from "@/lib/types";
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

const dateFormatter = new Intl.DateTimeFormat("en-US", {
  dateStyle: "medium",
  timeStyle: "short",
});

function formatDate(value: string | null): string {
  if (!value) return "Never";
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return dateFormatter.format(d);
}

export function ApiKeysView({
  slug,
  initialKeys,
}: {
  slug: string;
  initialKeys: WorkspaceApiKey[];
}) {
  const [keys, setKeys] = React.useState<WorkspaceApiKey[]>(initialKeys);
  const [createOpen, setCreateOpen] = React.useState(false);
  const [createdKey, setCreatedKey] =
    React.useState<WorkspaceApiKeyCreated | null>(null);

  const openCreate = React.useCallback(() => setCreateOpen(true), []);

  const handleCreated = React.useCallback(
    (created: WorkspaceApiKeyCreated) => {
      setKeys((prev) => [
        {
          id: created.id,
          name: created.name,
          prefix: created.prefix,
          createdAt: created.createdAt,
          lastUsedAt: null,
          revokedAt: null,
        },
        ...prev,
      ]);
      setCreatedKey(created);
    },
    []
  );

  const handleRevoked = React.useCallback((id: string) => {
    const revokedAt = new Date().toISOString();
    setKeys((prev) =>
      prev.map((k) => (k.id === id ? { ...k, revokedAt } : k))
    );
  }, []);

  return (
    <>
      {keys.length === 0 ? (
        <EmptyState onCreate={openCreate} />
      ) : (
        <>
          <div className="flex items-center justify-between animate-fade-up">
            <p className="text-sm text-muted-foreground">
              {keys.length} key{keys.length !== 1 ? "s" : ""}
            </p>
            <Button onClick={openCreate} className="gap-1.5">
              <PlusIcon className="size-3.5" />
              Create key
            </Button>
          </div>
          <Card
            className="animate-fade-up overflow-hidden"
            style={{ "--delay": "60ms" } as React.CSSProperties}
          >
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead className="border-b border-border bg-muted/30">
                  <tr className="text-left text-xs text-muted-foreground">
                    <th className="px-4 py-2 font-medium">Name</th>
                    <th className="px-4 py-2 font-medium">Prefix</th>
                    <th className="px-4 py-2 font-medium">Created</th>
                    <th className="px-4 py-2 font-medium">Last used</th>
                    <th className="px-4 py-2 font-medium">Status</th>
                    <th className="px-4 py-2" />
                  </tr>
                </thead>
                <tbody>
                  {keys.map((k) => (
                    <ApiKeyRow
                      key={k.id}
                      slug={slug}
                      apiKey={k}
                      onRevoked={handleRevoked}
                    />
                  ))}
                </tbody>
              </table>
            </div>
          </Card>
        </>
      )}

      <CreateKeyDialog
        slug={slug}
        open={createOpen}
        onOpenChange={setCreateOpen}
        onCreated={handleCreated}
      />

      <PlaintextDialog
        created={createdKey}
        onDismiss={() => setCreatedKey(null)}
      />
    </>
  );
}

function EmptyState({ onCreate }: { onCreate: () => void }) {
  return (
    <div className="flex justify-center py-12">
      <Card className="w-full max-w-md">
        <CardHeader>
          <CardTitle>No API keys yet</CardTitle>
          <CardDescription>
            Create a bearer token to connect an MCP client.
          </CardDescription>
        </CardHeader>
        <CardContent className="flex justify-center pb-4">
          <Button onClick={onCreate}>Create Key</Button>
        </CardContent>
      </Card>
    </div>
  );
}

function ApiKeyRow({
  slug,
  apiKey,
  onRevoked,
}: {
  slug: string;
  apiKey: WorkspaceApiKey;
  onRevoked: (id: string) => void;
}) {
  const [confirmOpen, setConfirmOpen] = React.useState(false);
  const [revoking, setRevoking] = React.useState(false);
  const [revokeError, setRevokeError] = React.useState<string | null>(null);
  const revoked = apiKey.revokedAt !== null;

  async function handleRevoke() {
    setRevoking(true);
    setRevokeError(null);
    try {
      await api<void>(
        `/api/workspaces/${slug}/api-keys/${apiKey.id}`,
        { method: "DELETE" }
      );
      setConfirmOpen(false);
      onRevoked(apiKey.id);
    } catch (err) {
      setRevokeError(
        err instanceof Error ? err.message : "Failed to revoke key."
      );
      setRevoking(false);
    }
  }

  return (
    <tr
      className={
        "border-b border-border last:border-b-0 " +
        (revoked ? "text-muted-foreground" : "")
      }
    >
      <td className="px-4 py-3 font-medium">{apiKey.name}</td>
      <td className="px-4 py-3 font-mono text-xs">
        {apiKey.prefix}
        <span className="text-muted-foreground">…</span>
      </td>
      <td className="px-4 py-3 text-xs">{formatDate(apiKey.createdAt)}</td>
      <td className="px-4 py-3 text-xs">{formatDate(apiKey.lastUsedAt)}</td>
      <td className="px-4 py-3">
        {revoked ? (
          <Badge variant="outline">Revoked</Badge>
        ) : (
          <Badge variant="secondary">Active</Badge>
        )}
      </td>
      <td className="px-4 py-3 text-right">
        {revoked ? null : (
          <Button
            variant="destructive"
            size="sm"
            onClick={() => setConfirmOpen(true)}
          >
            Revoke
          </Button>
        )}
        <AlertDialog open={confirmOpen} onOpenChange={setConfirmOpen}>
          <AlertDialogContent>
            <AlertDialogHeader>
              <AlertDialogTitle>Revoke API key?</AlertDialogTitle>
              <AlertDialogDescription>
                <span className="font-medium text-foreground">
                  {apiKey.name}
                </span>{" "}
                will stop working immediately. MCP clients using it must be
                reconfigured with a new key. This cannot be undone.
              </AlertDialogDescription>
            </AlertDialogHeader>
            {revokeError ? (
              <Alert variant="destructive">
                <AlertDescription>{revokeError}</AlertDescription>
              </Alert>
            ) : null}
            <AlertDialogFooter>
              <AlertDialogCancel disabled={revoking}>Cancel</AlertDialogCancel>
              <AlertDialogAction
                variant="destructive"
                onClick={(e) => {
                  e.preventDefault();
                  void handleRevoke();
                }}
                disabled={revoking}
              >
                {revoking ? "Revoking..." : "Revoke"}
              </AlertDialogAction>
            </AlertDialogFooter>
          </AlertDialogContent>
        </AlertDialog>
      </td>
    </tr>
  );
}

function CreateKeyDialog({
  slug,
  open,
  onOpenChange,
  onCreated,
}: {
  slug: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onCreated: (created: WorkspaceApiKeyCreated) => void;
}) {
  const [name, setName] = React.useState("");
  const [submitting, setSubmitting] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    if (!open) {
      setName("");
      setSubmitting(false);
      setError(null);
    }
  }, [open]);

  async function handleSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setError(null);
    const trimmed = name.trim();
    if (!trimmed) {
      setError("Name is required.");
      return;
    }
    setSubmitting(true);
    try {
      const res = await api<WorkspaceApiKeyCreated>(
        `/api/workspaces/${slug}/api-keys`,
        {
          method: "POST",
          body: JSON.stringify({ name: trimmed }),
        }
      );
      onCreated(res);
      onOpenChange(false);
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Failed to create API key."
      );
      setSubmitting(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Create API Key</DialogTitle>
          <DialogDescription>
            Give this token a name so you can recognise it later. The raw token
            is shown exactly once after creation.
          </DialogDescription>
        </DialogHeader>
        <form onSubmit={handleSubmit} className="flex flex-col gap-4">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="api-key-name">Name</Label>
            <Input
              id="api-key-name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="Claude Code (laptop)"
              autoComplete="off"
              required
            />
          </div>

          {error ? (
            <Alert variant="destructive">
              <AlertDescription>{error}</AlertDescription>
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
              {submitting ? "Creating..." : "Create"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/**
 * Plaintext dialog enforces the one-time-reveal contract:
 *  - The dialog cannot be dismissed via overlay/escape: any close attempt
 *    while uncopied prompts an AlertDialog confirmation.
 *  - Only the explicit "I've copied it" button (or confirming the warning)
 *    actually drops the plaintext from state.
 */
function PlaintextDialog({
  created,
  onDismiss,
}: {
  created: WorkspaceApiKeyCreated | null;
  onDismiss: () => void;
}) {
  const [copied, setCopied] = React.useState(false);
  const [warnOpen, setWarnOpen] = React.useState(false);

  React.useEffect(() => {
    if (created) {
      setCopied(false);
      setWarnOpen(false);
    }
  }, [created]);

  const open = created !== null;

  async function copyPlaintext() {
    if (!created) return;
    try {
      await navigator.clipboard.writeText(created.plaintext);
      setCopied(true);
    } catch {
      // Fallback: select the text manually — copy state remains false.
    }
  }

  function handleOpenChange(next: boolean) {
    if (next) return;
    if (copied) {
      onDismiss();
      return;
    }
    setWarnOpen(true);
  }

  function confirmDismissWithoutCopy() {
    setWarnOpen(false);
    onDismiss();
  }

  return (
    <>
      <Dialog open={open} onOpenChange={handleOpenChange}>
        <DialogContent className="sm:max-w-lg">
          <DialogHeader>
            <DialogTitle>Copy your new API key</DialogTitle>
            <DialogDescription>
              This is the only time the full token will be shown. Store it in
              your password manager or paste it into your MCP client now.
            </DialogDescription>
          </DialogHeader>

          {created ? (
            <div className="flex flex-col gap-3">
              <Alert variant="destructive">
                <AlertDescription>
                  We will not be able to show this token again. If you lose it
                  you must create a new key.
                </AlertDescription>
              </Alert>

              <div className="flex flex-col gap-1.5">
                <Label>Token</Label>
                <div className="flex items-stretch gap-2">
                  <code className="flex-1 break-all rounded-md border border-border bg-muted/40 px-3 py-2 font-mono text-xs">
                    {created.plaintext}
                  </code>
                  <Button
                    type="button"
                    variant="outline"
                    onClick={() => void copyPlaintext()}
                    aria-label="Copy token"
                  >
                    {copied ? (
                      <CheckIcon className="size-4" />
                    ) : (
                      <CopyIcon className="size-4" />
                    )}
                  </Button>
                </div>
                {copied ? (
                  <p className="text-xs text-muted-foreground">
                    Copied to clipboard.
                  </p>
                ) : null}
              </div>
            </div>
          ) : null}

          <DialogFooter>
            <Button
              type="button"
              onClick={() => {
                if (copied) {
                  onDismiss();
                } else {
                  setWarnOpen(true);
                }
              }}
            >
              I&apos;ve copied it
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <AlertDialog open={warnOpen} onOpenChange={setWarnOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Close without copying?</AlertDialogTitle>
            <AlertDialogDescription>
              You haven&apos;t copied the token yet. If you close this dialog
              the token will be lost and you will need to create a new key.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Go back</AlertDialogCancel>
            <AlertDialogAction
              variant="destructive"
              onClick={(e) => {
                e.preventDefault();
                confirmDismissWithoutCopy();
              }}
            >
              Close anyway
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}
