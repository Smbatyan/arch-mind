import Link from "next/link";

import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";

export default function Page() {
  return (
    <main className="flex min-h-screen items-center justify-center bg-background p-8">
      <Card className="w-full max-w-md">
        <CardHeader>
          <CardTitle className="text-3xl">ArchMind Admin</CardTitle>
          <CardDescription>Sprint 1 skeleton</CardDescription>
        </CardHeader>
        <CardContent className="flex gap-2">
          <Button render={<Link href="/login" />}>Login</Button>
          <Button variant="outline" render={<Link href="/register" />}>
            Register
          </Button>
        </CardContent>
      </Card>
    </main>
  );
}
