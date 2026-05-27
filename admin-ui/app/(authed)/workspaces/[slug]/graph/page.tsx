import { cookies } from "next/headers";
import { redirect } from "next/navigation";

import { GraphBrowser, type GraphLabelsResponse } from "./graph-browser";

const API_URL =
  (typeof window === "undefined" ? process.env.INTERNAL_API_URL : undefined) ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5000";

type WorkspaceDetail = {
  id: string;
  slug: string;
  name: string;
  createdAt: string;
};

async function buildCookieHeaders(): Promise<HeadersInit> {
  const cookieStore = await cookies();
  const sid = cookieStore.get("archmind.sid");
  return sid ? { cookie: `${sid.name}=${sid.value}` } : {};
}

async function fetchWorkspace(
  slug: string,
  headers: HeadersInit
): Promise<WorkspaceDetail | null> {
  try {
    const res = await fetch(`${API_URL}/api/workspaces/${slug}`, {
      headers,
      cache: "no-store",
    });
    if (!res.ok) return null;
    return (await res.json()) as WorkspaceDetail;
  } catch {
    return null;
  }
}

async function fetchLabels(
  slug: string,
  headers: HeadersInit
): Promise<GraphLabelsResponse> {
  try {
    const res = await fetch(
      `${API_URL}/api/workspaces/${slug}/graph/labels`,
      { headers, cache: "no-store" }
    );
    if (!res.ok) return { vertices: [], edges: [] };
    return (await res.json()) as GraphLabelsResponse;
  } catch {
    return { vertices: [], edges: [] };
  }
}

export default async function GraphPage({
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

  const initialLabels = await fetchLabels(slug, headers);

  return (
    <div className="flex h-full w-full flex-col gap-4 py-6">
      <div className="px-4 md:px-6">
        <h1 className="font-heading text-2xl font-medium">Graph</h1>
        <p className="text-sm text-muted-foreground">{workspace.name}</p>
      </div>
      <GraphBrowser slug={slug} initialLabels={initialLabels} />
    </div>
  );
}
