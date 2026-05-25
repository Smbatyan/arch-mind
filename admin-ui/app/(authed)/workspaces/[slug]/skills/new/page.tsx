import { ArrowLeftIcon } from "lucide-react";
import Link from "next/link";

import { NewSkillView } from "./new-skill-view";

export default async function NewSkillPage({
  params,
}: {
  params: Promise<{ slug: string }>;
}) {
  const { slug } = await params;

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
        <h1 className="font-heading text-2xl font-medium">New skill</h1>
        <p className="text-sm text-muted-foreground">
          Define a markdown-based skill the agent can invoke.
        </p>
      </div>
      <NewSkillView slug={slug} />
    </div>
  );
}
