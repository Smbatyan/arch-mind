"use client";

import { CheckCircle2Icon, CircleIcon, SparklesIcon } from "lucide-react";
import * as React from "react";

import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";

const DISMISSED_KEY = "archmind_onboarding_dismissed";

export type OnboardingStep = {
  label: string;
  done?: boolean;
  hint?: string;
};

const DEFAULT_STEPS: OnboardingStep[] = [
  { label: "Create a workspace" },
  { label: "Add a GitHub repo" },
  { label: "Connect Claude Code via MCP" },
];

export function isOnboardingDismissed(): boolean {
  if (typeof window === "undefined") return false;
  return window.localStorage.getItem(DISMISSED_KEY) === "1";
}

export function dismissOnboarding(): void {
  if (typeof window === "undefined") return;
  window.localStorage.setItem(DISMISSED_KEY, "1");
}

export function OnboardingChecklist({
  steps = DEFAULT_STEPS,
  primaryActionLabel,
  onPrimaryAction,
  onDismiss,
  showDismiss = true,
}: {
  steps?: OnboardingStep[];
  primaryActionLabel?: string;
  onPrimaryAction?: () => void;
  onDismiss?: () => void;
  showDismiss?: boolean;
}) {
  function handleDismiss() {
    dismissOnboarding();
    onDismiss?.();
  }

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-2">
          <SparklesIcon className="size-4 text-primary" />
          <CardTitle>Welcome to ArchMind</CardTitle>
        </div>
        <CardDescription>
          A guided tour to map your first codebase. Three quick steps.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <ul className="flex flex-col gap-2">
          {steps.map((step, idx) => (
            <li
              key={step.label}
              className="flex items-start gap-2 text-sm"
            >
              {step.done ? (
                <CheckCircle2Icon className="mt-0.5 size-4 shrink-0 text-emerald-500" />
              ) : (
                <CircleIcon className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
              )}
              <div className="flex flex-col">
                <span
                  className={
                    step.done ? "text-muted-foreground line-through" : ""
                  }
                >
                  <span className="font-mono text-xs text-muted-foreground">
                    {idx + 1}.
                  </span>{" "}
                  {step.label}
                </span>
                {step.hint ? (
                  <span className="text-xs text-muted-foreground">
                    {step.hint}
                  </span>
                ) : null}
              </div>
            </li>
          ))}
        </ul>

        <div className="flex items-center justify-between gap-2 pt-1">
          {primaryActionLabel && onPrimaryAction ? (
            <Button onClick={onPrimaryAction}>{primaryActionLabel}</Button>
          ) : (
            <span />
          )}
          {showDismiss ? (
            <button
              type="button"
              onClick={handleDismiss}
              className="text-xs text-muted-foreground underline-offset-4 hover:underline"
            >
              Dismiss
            </button>
          ) : null}
        </div>
      </CardContent>
    </Card>
  );
}
