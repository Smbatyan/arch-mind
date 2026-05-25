import { LogoutButton } from "@/components/logout-button";

export function TopBar({ email }: { email: string }) {
  return (
    <header className="flex h-14 shrink-0 items-center border-b border-border bg-card/40 px-4">
      <span className="font-heading text-base font-medium">ArchMind</span>
      <div className="flex-1" />
      <div className="flex items-center gap-3">
        <span className="text-sm text-muted-foreground">{email}</span>
        <LogoutButton />
      </div>
    </header>
  );
}
