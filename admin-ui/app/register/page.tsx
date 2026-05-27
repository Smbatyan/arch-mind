"use client";

import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { BrainCircuitIcon, EyeIcon, EyeOffIcon, CheckIcon } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { API_URL } from "@/lib/api";

export default function RegisterPage() {
  const router = useRouter();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const passwordTooShort = password.length > 0 && password.length < 8;
  const passwordsMismatch = confirmPassword.length > 0 && password !== confirmPassword;
  const passwordOk = password.length >= 8;
  const confirmOk = confirmPassword.length > 0 && password === confirmPassword;

  const canSubmit =
    email.length > 0 &&
    passwordOk &&
    confirmOk &&
    !submitting;

  async function onSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setServerError(null);
    if (!passwordOk || !confirmOk) return;

    setSubmitting(true);
    try {
      const res = await fetch(`${API_URL}/api/auth/register`, {
        method: "POST",
        credentials: "include",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email, password }),
      });
      if (!res.ok) {
        const data = await res.json().catch(() => ({ error: res.statusText }));
        throw new Error(data.error ?? "Registration failed");
      }
      router.push("/workspaces");
    } catch (err) {
      setServerError(err instanceof Error ? err.message : "Registration failed");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main className="relative flex min-h-screen items-center justify-center overflow-hidden bg-background p-4">
      {/* Ambient glows */}
      <div
        className="pointer-events-none absolute -top-40 right-1/4 h-96 w-96 translate-x-1/2 rounded-full opacity-20 blur-3xl"
        style={{
          background:
            "radial-gradient(circle, oklch(0.63 0.245 295), transparent 70%)",
        }}
        aria-hidden
      />
      <div
        className="pointer-events-none absolute -bottom-40 left-1/4 h-96 w-96 -translate-x-1/2 rounded-full opacity-15 blur-3xl"
        style={{
          background:
            "radial-gradient(circle, oklch(0.59 0.200 265), transparent 70%)",
        }}
        aria-hidden
      />

      {/* Card */}
      <div className="animate-fade-up relative z-10 w-full max-w-sm">
        {/* Logo */}
        <div className="mb-8 flex flex-col items-center gap-3 text-center">
          <div
            className="flex h-12 w-12 items-center justify-center rounded-2xl shadow-lg animate-glow-pulse"
            style={{
              background:
                "linear-gradient(135deg, oklch(0.63 0.245 295), oklch(0.59 0.200 265))",
            }}
          >
            <BrainCircuitIcon className="size-6 text-white" />
          </div>
          <div>
            <h1 className="text-xl font-bold tracking-tight">ArchMind</h1>
            <p className="mt-1 text-sm text-muted-foreground">
              Create your account
            </p>
          </div>
        </div>

        {/* Form card */}
        <div className="rounded-2xl border border-border/60 bg-card/80 p-6 shadow-2xl backdrop-blur-sm">
          <form onSubmit={onSubmit} className="flex flex-col gap-4" noValidate>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="email" className="text-sm font-medium">
                Email
              </Label>
              <Input
                id="email"
                type="email"
                autoComplete="email"
                required
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                placeholder="you@example.com"
                className="h-10 bg-background/60"
              />
            </div>

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="password" className="text-sm font-medium">
                Password
              </Label>
              <div className="relative">
                <Input
                  id="password"
                  type={showPassword ? "text" : "password"}
                  autoComplete="new-password"
                  required
                  minLength={8}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  aria-invalid={passwordTooShort || undefined}
                  className="h-10 bg-background/60 pr-10"
                />
                <button
                  type="button"
                  onClick={() => setShowPassword((v) => !v)}
                  className="absolute right-3 top-1/2 -translate-y-1/2 text-muted-foreground transition-colors hover:text-foreground"
                  aria-label={showPassword ? "Hide password" : "Show password"}
                  tabIndex={-1}
                >
                  {showPassword ? (
                    <EyeOffIcon className="size-4" />
                  ) : (
                    <EyeIcon className="size-4" />
                  )}
                </button>
              </div>

              {/* Password strength hint */}
              <div className="flex items-center gap-1.5 text-xs">
                <span
                  className={[
                    "flex items-center gap-1 transition-colors",
                    passwordOk ? "text-emerald-400" : "text-muted-foreground/50",
                  ].join(" ")}
                >
                  <CheckIcon className="size-3" />
                  8+ characters
                </span>
                {passwordTooShort && (
                  <span className="text-destructive ml-1">
                    Too short
                  </span>
                )}
              </div>
            </div>

            <div className="flex flex-col gap-1.5">
              <div className="flex items-center justify-between">
                <Label htmlFor="confirm-password" className="text-sm font-medium">
                  Confirm password
                </Label>
                {confirmOk && (
                  <span className="flex items-center gap-1 text-xs text-emerald-400">
                    <CheckIcon className="size-3" />
                    Matches
                  </span>
                )}
              </div>
              <Input
                id="confirm-password"
                type="password"
                autoComplete="new-password"
                required
                value={confirmPassword}
                onChange={(e) => setConfirmPassword(e.target.value)}
                aria-invalid={passwordsMismatch || undefined}
                className="h-10 bg-background/60"
              />
              {passwordsMismatch && (
                <p className="text-xs text-destructive">Passwords do not match</p>
              )}
            </div>

            {serverError && (
              <Alert variant="destructive" className="py-2">
                <AlertDescription className="text-xs">{serverError}</AlertDescription>
              </Alert>
            )}

            <Button
              type="submit"
              disabled={!canSubmit}
              className="mt-1 h-10 w-full font-medium"
              style={
                canSubmit
                  ? {
                      background:
                        "linear-gradient(135deg, oklch(0.63 0.245 295), oklch(0.59 0.200 265))",
                    }
                  : undefined
              }
            >
              {submitting ? (
                <span className="flex items-center gap-2">
                  <span className="h-3.5 w-3.5 rounded-full border-2 border-white/40 border-t-white animate-spin" />
                  Creating account…
                </span>
              ) : (
                "Create account"
              )}
            </Button>
          </form>
        </div>

        {/* Footer */}
        <p className="mt-4 text-center text-sm text-muted-foreground">
          Already have an account?{" "}
          <Link
            href="/login"
            className="font-medium text-primary underline-offset-4 hover:underline"
          >
            Sign in
          </Link>
        </p>
      </div>
    </main>
  );
}
