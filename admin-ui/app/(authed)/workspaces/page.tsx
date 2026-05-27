import { cookies } from "next/headers";

import { WorkspacesView, type Workspace } from "./workspaces-view";

const API_URL =
  (typeof window === "undefined" ? process.env.INTERNAL_API_URL : undefined) ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5000";

async function fetchWorkspaces(): Promise<Workspace[]> {
  const cookieStore = await cookies();
  const sid = cookieStore.get("archmind.sid");
  const headers: HeadersInit = sid
    ? { cookie: `${sid.name}=${sid.value}` }
    : {};

  try {
    const res = await fetch(`${API_URL}/api/workspaces`, {
      headers,
      cache: "no-store",
    });
    if (!res.ok) return [];
    return (await res.json()) as Workspace[];
  } catch {
    return [];
  }
}

export default async function WorkspacesPage() {
  const workspaces = await fetchWorkspaces();

  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6 py-8">
      <h1 className="font-heading text-2xl font-medium">Your Workspaces</h1>
      <WorkspacesView initialWorkspaces={workspaces} />
    </div>
  );
}
