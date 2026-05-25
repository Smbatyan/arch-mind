"use client";

import { Loader2Icon } from "lucide-react";

import { Badge } from "@/components/ui/badge";

export type RepoStatus = "pending" | "scanning" | "scanned" | "failed";

export function RepoStatusBadge({ status }: { status: RepoStatus }) {
  switch (status) {
    case "pending":
      return (
        <Badge className="bg-muted text-muted-foreground" variant="secondary">
          pending
        </Badge>
      );
    case "scanning":
      return (
        <Badge className="bg-blue-100 text-blue-700 dark:bg-blue-500/20 dark:text-blue-300">
          <Loader2Icon className="animate-spin" />
          scanning
        </Badge>
      );
    case "scanned":
      return (
        <Badge className="bg-emerald-100 text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-300">
          scanned
        </Badge>
      );
    case "failed":
      return <Badge variant="destructive">failed</Badge>;
    default:
      return <Badge variant="secondary">{status}</Badge>;
  }
}
