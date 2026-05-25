import { cookies } from "next/headers";
import { redirect } from "next/navigation";

import { RepoDetailView, type RepoDetail, type ScanRun } from "./repo-detail-view";

const API_URL =
  process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

async function buildCookieHeaders(): Promise<HeadersInit> {
  const cookieStore = await cookies();
  const sid = cookieStore.get("archmind.sid");
  return sid ? { cookie: `${sid.name}=${sid.value}` } : {};
}

async function fetchRepo(
  slug: string,
  id: string,
  headers: HeadersInit
): Promise<RepoDetail | null> {
  try {
    const res = await fetch(
      `${API_URL}/api/workspaces/${slug}/repos/${id}`,
      { headers, cache: "no-store" }
    );
    if (!res.ok) return null;
    return (await res.json()) as RepoDetail;
  } catch {
    return null;
  }
}

async function fetchScans(
  slug: string,
  id: string,
  headers: HeadersInit
): Promise<ScanRun[] | null> {
  try {
    const res = await fetch(
      `${API_URL}/api/workspaces/${slug}/repos/${id}/scans?limit=20`,
      { headers, cache: "no-store" }
    );
    if (res.status === 404) return null;
    if (!res.ok) return null;
    return (await res.json()) as ScanRun[];
  } catch {
    return null;
  }
}

export default async function RepoDetailPage({
  params,
}: {
  params: Promise<{ slug: string; id: string }>;
}) {
  const { slug, id } = await params;
  const headers = await buildCookieHeaders();

  const repo = await fetchRepo(slug, id, headers);
  if (!repo) {
    redirect(`/workspaces/${slug}/repos`);
  }

  const scans = await fetchScans(slug, id, headers);

  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6 py-8">
      <RepoDetailView
        slug={slug}
        repoId={id}
        initialRepo={repo}
        initialScans={scans}
      />
    </div>
  );
}
