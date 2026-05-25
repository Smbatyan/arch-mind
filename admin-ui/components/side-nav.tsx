"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { KeyIcon, SparklesIcon } from "lucide-react";

type NavItem = { label: string; href: string; icon?: React.ReactNode };

function buildItems(slug: string | null): NavItem[] {
  // TODO(FE-006+): replace `/workspaces` placeholders with `/workspaces/[slug]/...`
  return [
    { label: "Repos", href: "/workspaces" },
    {
      label: "Skills",
      href: "/workspaces",
      icon: <SparklesIcon className="size-3.5" />,
    },
    { label: "Questions", href: "/workspaces" },
    { label: "Graph", href: "/workspaces" },
    { label: "Reports", href: "/workspaces" },
    { label: "Settings", href: "/workspaces" },
    {
      label: "API Keys",
      href: slug ? `/workspaces/${slug}/settings/api-keys` : "/workspaces",
      icon: <KeyIcon className="size-3.5" />,
    },
  ];
}

export function SideNav() {
  const params = useParams<{ slug?: string | string[] }>();
  const rawSlug = params?.slug;
  const slug = Array.isArray(rawSlug) ? rawSlug[0] : rawSlug ?? null;

  const items = buildItems(slug);

  return (
    <aside className="hidden w-56 shrink-0 border-r border-border bg-card/40 md:block">
      <nav className="flex flex-col gap-1 p-3">
        {items.map((item, idx) => (
          <Link
            key={`${item.label}-${idx}`}
            href={item.href}
            className="flex items-center gap-2 rounded-md px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
          >
            {item.icon}
            <span>{item.label}</span>
          </Link>
        ))}
      </nav>
    </aside>
  );
}
