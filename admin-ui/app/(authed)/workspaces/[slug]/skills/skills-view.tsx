"use client";

import Link from "next/link";
import * as React from "react";

import type { SkillSummary } from "@/lib/types";
import { Badge } from "@/components/ui/badge";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";

const dateFormatter = new Intl.DateTimeFormat("en-US", {
  dateStyle: "medium",
  timeStyle: "short",
});

function formatDate(value: string): string {
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return dateFormatter.format(d);
}

function truncate(s: string, n: number): string {
  if (!s) return "";
  return s.length > n ? `${s.slice(0, n)}…` : s;
}

export function SkillsView({
  slug,
  initialSkills,
}: {
  slug: string;
  initialSkills: SkillSummary[];
}) {
  const [skills] = React.useState<SkillSummary[]>(initialSkills);

  if (skills.length === 0) {
    return <EmptyState slug={slug} />;
  }

  return (
    <>
      <div className="flex items-center justify-end">
        <Link
          href={`/workspaces/${slug}/skills/new`}
          className="inline-flex h-8 items-center justify-center rounded-lg bg-primary px-2.5 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/80"
        >
          New Skill
        </Link>
      </div>

      <Card>
        <CardContent className="p-0">
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-xs text-muted-foreground">
                  <th className="px-4 py-3 font-medium">Name</th>
                  <th className="px-4 py-3 font-medium">Title</th>
                  <th className="px-4 py-3 font-medium">Description</th>
                  <th className="px-4 py-3 font-medium">Enabled</th>
                  <th className="px-4 py-3 font-medium">Updated</th>
                </tr>
              </thead>
              <tbody>
                {skills.map((skill) => (
                  <tr
                    key={skill.id}
                    className="border-b last:border-0 hover:bg-muted/40"
                  >
                    <td className="px-4 py-3 font-mono text-xs">
                      <Link
                        href={`/workspaces/${slug}/skills/${skill.id}`}
                        className="hover:underline"
                      >
                        {skill.name}
                      </Link>
                    </td>
                    <td className="px-4 py-3">
                      <Link
                        href={`/workspaces/${slug}/skills/${skill.id}`}
                        className="hover:underline"
                      >
                        {skill.title}
                      </Link>
                    </td>
                    <td
                      className="px-4 py-3 text-muted-foreground"
                      title={skill.description}
                    >
                      {truncate(skill.description, 80)}
                    </td>
                    <td className="px-4 py-3">
                      <Badge variant={skill.enabled ? "default" : "outline"}>
                        {skill.enabled ? "enabled" : "disabled"}
                      </Badge>
                    </td>
                    <td className="px-4 py-3 text-xs text-muted-foreground">
                      {formatDate(skill.updatedAt)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </CardContent>
      </Card>
    </>
  );
}

function EmptyState({ slug }: { slug: string }) {
  return (
    <div className="flex justify-center py-12">
      <Card className="w-full max-w-md">
        <CardHeader>
          <CardTitle>No skills yet</CardTitle>
          <CardDescription>
            Create a skill to extend the assistant with custom instructions.
          </CardDescription>
        </CardHeader>
        <CardContent className="flex justify-center pb-4">
          <Link
            href={`/workspaces/${slug}/skills/new`}
            className="inline-flex h-8 items-center justify-center rounded-lg bg-primary px-2.5 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/80"
          >
            New Skill
          </Link>
        </CardContent>
      </Card>
    </div>
  );
}
