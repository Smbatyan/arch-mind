import { cookies } from "next/headers";
import { redirect } from "next/navigation";

import type { WorkspaceApiKey } from "@/lib/types";

import { ConnectView } from "./connect-view";

const API_URL =
  (typeof window === "undefined" ? process.env.INTERNAL_API_URL : undefined) ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5000";

async function buildCookieHeaders(): Promise<HeadersInit> {
  const cookieStore = await cookies();
  const sid = cookieStore.get("archmind.sid");
  return sid ? { cookie: `${sid.name}=${sid.value}` } : {};
}

async function fetchWorkspace(
  slug: string,
  headers: HeadersInit
): Promise<{ id: string; slug: string; name: string } | null> {
  try {
    const res = await fetch(`${API_URL}/api/workspaces/${slug}`, {
      headers,
      cache: "no-store",
    });
    if (!res.ok) return null;
    return (await res.json()) as { id: string; slug: string; name: string };
  } catch {
    return null;
  }
}

async function fetchApiKeys(
  slug: string,
  headers: HeadersInit
): Promise<WorkspaceApiKey[]> {
  try {
    const res = await fetch(`${API_URL}/api/workspaces/${slug}/api-keys`, {
      headers,
      cache: "no-store",
    });
    if (!res.ok) return [];
    return (await res.json()) as WorkspaceApiKey[];
  } catch {
    return [];
  }
}

export default async function ConnectPage({
  params,
}: {
  params: Promise<{ slug: string }>;
}) {
  const { slug } = await params;
  const headers = await buildCookieHeaders();

  const workspace = await fetchWorkspace(slug, headers);
  if (!workspace) {
    redirect("/workspaces");
  }

  const allKeys = await fetchApiKeys(slug, headers);
  const activeKeys = allKeys.filter((k) => k.revokedAt === null);

  // Public-facing backend URL — used in the generated MCP command.
  const publicApiUrl =
    process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";
  const mcpUrl = `${publicApiUrl}/mcp/${slug}`;
  const commandsUrl = `${publicApiUrl}/api/workspaces/${slug}/claude-commands`;

  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6 py-8">
      <div className="flex flex-col gap-1">
        <h1 className="font-heading text-2xl font-medium">Connect</h1>
        <p className="text-sm text-muted-foreground">
          Get your personalised MCP connection command for Claude Code or
          Cursor. The workspace URL is pre-filled — just supply your API key.
        </p>
      </div>
      <ConnectView slug={slug} mcpUrl={mcpUrl} commandsUrl={commandsUrl} activeKeys={activeKeys} />
    </div>
  );
}
