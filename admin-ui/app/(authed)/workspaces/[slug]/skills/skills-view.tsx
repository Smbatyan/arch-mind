"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import * as React from "react";
import { PlusIcon } from "lucide-react";

import type { SkillSummary } from "@/lib/types";
import { Badge } from "@/components/ui/badge";
import { buttonVariants } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { cn } from "@/lib/utils";

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
  const router = useRouter();
  const [skills] = React.useState<SkillSummary[]>(initialSkills);

  if (skills.length === 0) {
    return <EmptyState slug={slug} />;
  }

  return (
    <>
      <div className="flex items-center justify-between animate-fade-up">
        <p className="text-sm text-muted-foreground">
          {skills.length} skill{skills.length !== 1 ? "s" : ""}
        </p>
        <Link
          href={`/workspaces/${slug}/skills/new`}
          className={cn(buttonVariants({ variant: "default" }), "gap-1.5")}
        >
          <PlusIcon className="size-3.5" />
          New skill
        </Link>
      </div>

      <Card
        className="animate-fade-up overflow-hidden"
        style={{ "--delay": "60ms" } as React.CSSProperties}
      >
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
                    className="cursor-pointer border-b last:border-0 transition-colors hover:bg-muted/40"
                    onClick={() =>
                      router.push(`/workspaces/${slug}/skills/${skill.id}`)
                    }
                  >
                    <td className="px-4 py-3 font-mono text-xs">{skill.name}</td>
                    <td className="px-4 py-3 font-medium">{skill.title}</td>
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
            className={cn(buttonVariants({ variant: "default" }), "gap-1.5")}
          >
            <PlusIcon className="size-3.5" />
            New skill
          </Link>
        </CardContent>
      </Card>
    </div>
  );
}
