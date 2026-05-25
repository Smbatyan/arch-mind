import { cookies } from "next/headers";
import { redirect } from "next/navigation";

import type { SkillSummary } from "@/lib/types";

import { SkillsView } from "./skills-view";

const API_URL =
  process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

type WorkspaceDetail = {
  id: string;
  slug: string;
  name: string;
  role?: string;
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

async function fetchSkills(
  slug: string,
  headers: HeadersInit
): Promise<SkillSummary[]> {
  try {
    const res = await fetch(`${API_URL}/api/workspaces/${slug}/skills`, {
      headers,
      cache: "no-store",
    });
    if (!res.ok) return [];
    return (await res.json()) as SkillSummary[];
  } catch {
    return [];
  }
}

export default async function SkillsPage({
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

  const skills = await fetchSkills(slug, headers);

  return (
    <div className="mx-auto flex w-full max-w-5xl flex-col gap-6 py-8">
      <div className="flex flex-col gap-1">
        <h1 className="font-heading text-2xl font-medium">Skills</h1>
        <p className="text-sm text-muted-foreground">{workspace.name}</p>
      </div>
      <SkillsView slug={slug} initialSkills={skills} />
    </div>
  );
}
