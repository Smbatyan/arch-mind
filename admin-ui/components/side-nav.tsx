"use client";

import * as React from "react";
import Link from "next/link";
import { useParams, usePathname } from "next/navigation";
import {
  FolderGitIcon,
  KeyIcon,
  LayoutDashboardIcon,
  Loader2Icon,
  MessageCircleQuestion,
  PlugZapIcon,
  Share2Icon,
  SparklesIcon,
  CheckCircle2Icon,
  AlertCircleIcon,
} from "lucide-react";

import { api } from "@/lib/api";

type NavItem = { label: string; href: string; icon?: React.ReactNode };
type NavGroup = { label?: string; items: NavItem[] };

function buildGroups(slug: string | null): NavGroup[] {
  if (!slug) {
    return [
      {
        items: [
          {
            label: "Workspaces",
            href: "/workspaces",
            icon: <LayoutDashboardIcon className="size-4" />,
          },
        ],
      },
    ];
  }

  const base = `/workspaces/${slug}`;
  return [
    {
      items: [
        {
          label: "Dashboard",
          href: `${base}/dashboard`,
          icon: <LayoutDashboardIcon className="size-4" />,
        },
        {
          label: "Repos",
          href: `${base}/repos`,
          icon: <FolderGitIcon className="size-4" />,
        },
        {
          label: "Skills",
          href: `${base}/skills`,
          icon: <SparklesIcon className="size-4" />,
        },
        {
          label: "Knowledge Graph",
          href: `${base}/graph`,
          icon: <Share2Icon className="size-4" />,
        },
        {
          label: "Clarifications",
          href: `${base}/clarifications`,
          icon: <MessageCircleQuestion className="size-4" />,
        },
      ],
    },
    {
      label: "Settings",
      items: [
        {
          label: "API Keys",
          href: `${base}/settings/api-keys`,
          icon: <KeyIcon className="size-4" />,
        },
        {
          label: "Connect",
          href: `${base}/settings/connect`,
          icon: <PlugZapIcon className="size-4" />,
        },
      ],
    },
  ];
}

export function SideNav() {
  const params = useParams<{ slug?: string | string[] }>();
  const pathname = usePathname();
  const rawSlug = params?.slug;
  const slug = Array.isArray(rawSlug) ? rawSlug[0] : rawSlug ?? null;

  const groups = buildGroups(slug);

  // Precompute global indices for stagger animation
  let counter = 0;
  const groupsWithIdx = groups.map((group) => ({
    ...group,
    items: group.items.map((item) => ({ ...item, globalIdx: counter++ })),
  }));

  return (
    <aside className="hidden w-56 shrink-0 border-r border-sidebar-border bg-sidebar md:flex md:flex-col">
      {/* Workspace switcher */}
      {slug && (
        <div className="border-b border-sidebar-border p-2">
          <Link
            href="/workspaces"
            className="group flex items-center gap-2 rounded-lg px-2 py-1.5 transition-colors hover:bg-sidebar-accent/60"
            title="Switch workspace"
          >
            <div
              className="flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-[11px] font-bold text-white"
              style={{
                background:
                  "linear-gradient(135deg, oklch(0.68 0.235 295), oklch(0.62 0.200 265))",
              }}
              aria-hidden
            >
              {slug.slice(0, 1).toUpperCase()}
            </div>
            <div className="min-w-0 flex-1">
              <p className="truncate text-xs font-medium leading-tight">{slug}</p>
              <p className="text-[10px] text-muted-foreground leading-tight">
                switch workspace
              </p>
            </div>
            <svg
              className="size-3 shrink-0 text-muted-foreground/50 transition-transform group-hover:translate-x-0.5"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden
            >
              <path d="M7 5l5 5-5 5V5z" />
            </svg>
          </Link>
        </div>
      )}

      {/* Nav groups */}
      <nav className="flex flex-1 flex-col p-2" aria-label="Sidebar navigation">
        {groupsWithIdx.map((group, groupIdx) => (
          <React.Fragment key={groupIdx}>
            {/* Section divider + label for non-first groups */}
            {groupIdx > 0 && (
              <>
                <div className="mx-1 my-2 border-t border-sidebar-border/60" aria-hidden />
                {group.label && (
                  <p className="px-2.5 pb-1 pt-0.5 text-[10px] font-semibold uppercase tracking-widest text-muted-foreground/40">
                    {group.label}
                  </p>
                )}
              </>
            )}

            <div className="flex flex-col gap-0.5">
              {group.items.map((item) => {
                const isActive =
                  pathname === item.href ||
                  pathname?.startsWith(`${item.href}/`);

                return (
                  <Link
                    key={item.label}
                    href={item.href}
                    className={[
                      "group relative flex items-center gap-2.5 rounded-lg px-2.5 py-2 text-sm transition-all duration-150",
                      "animate-slide-in-left",
                      isActive
                        ? "bg-sidebar-accent text-foreground font-medium shadow-sm"
                        : "text-muted-foreground hover:bg-sidebar-accent/50 hover:text-foreground",
                    ].join(" ")}
                    style={{ "--delay": `${item.globalIdx * 40}ms` } as React.CSSProperties}
                    aria-current={isActive ? "page" : undefined}
                  >
                    {/* Active left bar */}
                    {isActive && (
                      <span
                        className="absolute -left-2 top-1/2 -translate-y-1/2 h-5 w-[3px] rounded-r-full"
                        style={{
                          background:
                            "linear-gradient(to bottom, oklch(0.68 0.235 295), oklch(0.62 0.200 265))",
                          boxShadow: "0 0 12px oklch(0.68 0.235 295 / 0.5)",
                        }}
                        aria-hidden
                      />
                    )}

                    {/* Icon */}
                    <span
                      className={[
                        "shrink-0 transition-colors duration-150",
                        isActive
                          ? "text-primary"
                          : "text-muted-foreground/70 group-hover:text-foreground/80",
                      ].join(" ")}
                    >
                      {item.icon}
                    </span>

                    <span className="truncate">{item.label}</span>
                  </Link>
                );
              })}
            </div>
          </React.Fragment>
        ))}
      </nav>

      {/* Job status */}
      <JobStatusPill />
    </aside>
  );
}

/* ── Job Status ──────────────────────────────────────────────────────── */

type JobStatus = {
  enqueued: number;
  processing: number;
  scheduled: number;
  failedLastHour: number;
  succeededLastHour: number;
  asOf: string;
};

function JobStatusPill() {
  const [status, setStatus] = React.useState<JobStatus | null>(null);
  const [error, setError] = React.useState(false);

  React.useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;

    const tick = async () => {
      try {
        const data = await api<JobStatus>("/api/jobs/status");
        if (cancelled) return;
        setStatus(data);
        setError(false);
      } catch {
        if (cancelled) return;
        setError(true);
      } finally {
        if (!cancelled) {
          const busy = (status?.enqueued ?? 0) + (status?.processing ?? 0) > 0;
          timer = setTimeout(tick, busy ? 5_000 : 15_000);
        }
      }
    };

    tick();
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (error || !status) return <div className="h-1 w-full" />;

  const active = status.enqueued + status.processing;
  const busy = active > 0;

  return (
    <div className="border-t border-sidebar-border p-2">
      <div
        className={[
          "flex items-center gap-2 rounded-lg px-2.5 py-2 text-xs transition-colors",
          busy
            ? "bg-amber-500/8 text-amber-300"
            : "bg-emerald-500/8 text-emerald-400",
        ].join(" ")}
        title={[
          `processing: ${status.processing}`,
          `enqueued: ${status.enqueued}`,
          `scheduled: ${status.scheduled}`,
          `failed (1h): ${status.failedLastHour}`,
          `succeeded (1h): ${status.succeededLastHour}`,
        ].join("\n")}
      >
        {busy ? (
          <Loader2Icon className="size-3 shrink-0 animate-spin text-amber-400" />
        ) : (
          <span className="relative flex size-2 shrink-0">
            <span className="absolute inline-flex h-full w-full rounded-full bg-emerald-400 opacity-75 animate-pulse-dot" />
            <span className="relative inline-flex size-2 rounded-full bg-emerald-500" />
          </span>
        )}

        <div className="flex min-w-0 flex-1 flex-col">
          <span className="font-medium leading-tight">
            {busy ? `${active} job${active !== 1 ? "s" : ""} running` : "System idle"}
          </span>
          {busy ? (
            <span className="text-[0.6rem] leading-tight opacity-70">
              {status.processing} active · {status.enqueued} queued
            </span>
          ) : (
            <span className="text-[0.6rem] leading-tight opacity-70">
              {status.succeededLastHour} succeeded · {status.failedLastHour} failed (1h)
            </span>
          )}
        </div>

        {busy ? null : (
          <CheckCircle2Icon className="size-3 shrink-0 text-emerald-500/70" />
        )}
      </div>
    </div>
  );
}
