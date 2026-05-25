import Link from "next/link";

type NavItem = { label: string; href: string };

const items: NavItem[] = [
  // TODO(FE-006+): replace `/workspaces` placeholders with `/workspaces/[slug]/...`
  { label: "Repos", href: "/workspaces" },
  { label: "Skills", href: "/workspaces" },
  { label: "Questions", href: "/workspaces" },
  { label: "Graph", href: "/workspaces" },
  { label: "Reports", href: "/workspaces" },
  { label: "Settings", href: "/workspaces" },
];

export function SideNav() {
  return (
    <aside className="hidden w-56 shrink-0 border-r border-border bg-card/40 md:block">
      <nav className="flex flex-col gap-1 p-3">
        {items.map((item, idx) => (
          <Link
            key={`${item.label}-${idx}`}
            href={item.href}
            className="rounded-md px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
          >
            {item.label}
          </Link>
        ))}
      </nav>
    </aside>
  );
}
