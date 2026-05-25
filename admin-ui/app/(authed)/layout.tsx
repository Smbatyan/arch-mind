import { cookies } from "next/headers";
import { redirect } from "next/navigation";

import { SideNav } from "@/components/side-nav";
import { TopBar } from "@/components/top-bar";

const API_URL =
  process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

type MeResponse = { user: { id: string; email: string } };

async function fetchCurrentUser(): Promise<MeResponse["user"] | null> {
  const cookieStore = await cookies();
  const sid = cookieStore.get("archmind.sid");
  const headers: HeadersInit = sid
    ? { cookie: `${sid.name}=${sid.value}` }
    : {};

  try {
    const res = await fetch(`${API_URL}/api/auth/me`, {
      headers,
      cache: "no-store",
    });
    if (!res.ok) return null;
    const data = (await res.json()) as MeResponse;
    return data.user;
  } catch {
    return null;
  }
}

export default async function AuthedLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  const user = await fetchCurrentUser();
  if (!user) {
    redirect("/login");
  }

  return (
    <div className="flex min-h-screen flex-col">
      <TopBar email={user.email} />
      <div className="flex flex-1 overflow-hidden">
        <SideNav />
        <main className="flex-1 overflow-auto p-6">{children}</main>
      </div>
    </div>
  );
}
