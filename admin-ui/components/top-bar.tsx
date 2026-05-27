"use client";

import Link from "next/link";
import { useParams, usePathname } from "next/navigation";
import { BrainCircuitIcon, ChevronRightIcon } from "lucide-react";
import { LogoutButton } from "@/components/logout-button";

// Derive a human-readable label from the last path segment
function pageLabel(pathname: string, slug: string | null): string {
  const segments = pathname.split("/").filter(Boolean);
  const last = segments[segments.length - 1];

  if (!last || last === "workspaces") return "Workspaces";
  if (slug && last === slug) return "Overview";

  const labels: Record<string, string> = {
    dashboard: "Dashboard",
    repos: "Repositories",
    skills: "Skills",
    graph: "Knowledge Graph",
    clarifications: "Clarifications",
    "api-keys": "API Keys",
    settings: "Settings",
    new: "New Skill",
  };

  return labels[last] ?? last.replace(/-/g, " ").replace(/\b\w/g, (c) => c.toUpperCase());
}

function UserBadge({ email }: { email: string }) {
  const initials = email.slice(0, 2).toUpperCase();
  return (
    <div className="flex items-center gap-2.5">
      <div
        className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-[11px] font-semibold"
        style={{
          background: "linear-gradient(135deg, oklch(0.63 0.245 295), oklch(0.59 0.200 265))",
          color: "white",
        }}
        title={email}
        aria-label={`Signed in as ${email}`}
      >
        {initials}
      </div>
      <span className="hidden text-xs text-muted-foreground sm:block max-w-[160px] truncate">
        {email}
      </span>
    </div>
  );
}

export function TopBar({ email }: { email: string }) {
  const params = useParams<{ slug?: string | string[] }>();
  const pathname = usePathname();
  const rawSlug = params?.slug;
  const slug = Array.isArray(rawSlug) ? rawSlug[0] : rawSlug ?? null;

  const currentLabel = pageLabel(pathname ?? "", slug);
  const inWorkspace = Boolean(slug);

  return (
    <header className="sticky top-0 z-50 flex h-12 shrink-0 items-center gap-3 border-b border-border/40 bg-background/80 px-4 backdrop-blur-xl">
      {/* Logo */}
      <Link
        href="/workspaces"
        className="flex shrink-0 items-center gap-2 transition-opacity hover:opacity-80"
        aria-label="ArchMind — go to workspaces"
      >
        <div
          className="flex h-6 w-6 items-center justify-center rounded-md"
          style={{
            background: "linear-gradient(135deg, oklch(0.63 0.245 295), oklch(0.59 0.200 265))",
          }}
        >
          <BrainCircuitIcon className="size-3.5 text-white" />
        </div>
        <span className="text-sm font-semibold tracking-tight">ArchMind</span>
      </Link>

      {/* Breadcrumb */}
      {inWorkspace && (
        <>
          <ChevronRightIcon className="size-3.5 shrink-0 text-muted-foreground/40" aria-hidden />
          <Link
            href={`/workspaces/${slug}`}
            className="text-xs text-muted-foreground transition-colors hover:text-foreground font-mono"
          >
            {slug}
          </Link>
          <ChevronRightIcon className="size-3.5 shrink-0 text-muted-foreground/40" aria-hidden />
          <span className="text-xs font-medium">{currentLabel}</span>
        </>
      )}

      <div className="flex-1" />

      {/* User */}
      <div className="flex items-center gap-2">
        <UserBadge email={email} />
        <LogoutButton />
      </div>
    </header>
  );
}
