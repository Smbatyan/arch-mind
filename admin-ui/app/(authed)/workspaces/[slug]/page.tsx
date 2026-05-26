import { redirect } from "next/navigation";

export default async function WorkspaceOverviewPage({
  params,
}: {
  params: Promise<{ slug: string }>;
}) {
  const { slug } = await params;
  redirect(`/workspaces/${slug}/dashboard`);
}
