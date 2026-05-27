import { ArrowLeftIcon } from "lucide-react";
import Link from "next/link";
import { cookies } from "next/headers";
import { redirect } from "next/navigation";

import type { Skill, SkillRevisionSummary } from "@/lib/types";

import { SkillEditorView } from "./skill-editor-view";

const API_URL =
  (typeof window === "undefined" ? process.env.INTERNAL_API_URL : undefined) ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5000";

async function buildCookieHeaders(): Promise<HeadersInit> {
  const cookieStore = await cookies();
  const sid = cookieStore.get("archmind.sid");
  return sid ? { cookie: `${sid.name}=${sid.value}` } : {};
}

async function fetchSkill(
  slug: string,
  id: string,
  headers: HeadersInit
): Promise<Skill | null> {
  try {
    const res = await fetch(
      `${API_URL}/api/workspaces/${slug}/skills/${id}`,
      { headers, cache: "no-store" }
    );
    if (!res.ok) return null;
    return (await res.json()) as Skill;
  } catch {
    return null;
  }
}

async function fetchRevisions(
  slug: string,
  id: string,
  headers: HeadersInit
): Promise<SkillRevisionSummary[]> {
  try {
    const res = await fetch(
      `${API_URL}/api/workspaces/${slug}/skills/${id}/revisions`,
      { headers, cache: "no-store" }
    );
    if (!res.ok) return [];
    return (await res.json()) as SkillRevisionSummary[];
  } catch {
    return [];
  }
}

export default async function SkillDetailPage({
  params,
}: {
  params: Promise<{ slug: string; id: string }>;
}) {
  const { slug, id } = await params;
  const headers = await buildCookieHeaders();

  const skill = await fetchSkill(slug, id, headers);
  if (!skill) {
    redirect(`/workspaces/${slug}/skills`);
  }

  const revisions = await fetchRevisions(slug, id, headers);

  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6 py-8">
      <div className="flex items-center gap-2">
        <Link
          href={`/workspaces/${slug}/skills`}
          className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ArrowLeftIcon className="size-3.5" />
          Skills
        </Link>
      </div>
      <div className="flex flex-col gap-1">
        <h1 className="font-heading text-2xl font-medium">{skill.title}</h1>
        <p className="font-mono text-xs text-muted-foreground">{skill.name}</p>
      </div>
      <SkillEditorView
        slug={slug}
        skillId={id}
        initialSkill={skill}
        initialRevisions={revisions}
      />
    </div>
  );
}
