"use client";

import dynamic from "next/dynamic";
import { useRouter } from "next/navigation";
import * as React from "react";

import { api } from "@/lib/api";
import { SKILL_NAME_REGEX, type Skill } from "@/lib/types";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";

// Monaco depends on `window`; load it only on the client. The wrapper component
// itself is "use client", but ssr:false here also stops Next from running the
// module during prerender.
const SkillMarkdownEditor = dynamic(
  () => import("@/components/skill-markdown-editor"),
  {
    ssr: false,
    loading: () => (
      <div className="flex h-[500px] items-center justify-center rounded-md border border-border text-sm text-muted-foreground">
        Loading editor…
      </div>
    ),
  }
);

const DEFAULT_BODY = `# Skill body

Describe the model's behavior here in markdown. The first line is typically
a one-sentence summary of when this skill should trigger.
`;

export type SkillFormMode = "create" | "edit";

export type SkillEditorInitial = {
  name: string;
  title: string;
  description: string;
  triggers: string[];
  enabled: boolean;
  body: string;
};

export function SkillEditorForm({
  slug,
  mode,
  skillId,
  initial,
  requireChangeNote = false,
  onSaved,
}: {
  slug: string;
  mode: SkillFormMode;
  skillId?: string;
  initial: SkillEditorInitial;
  requireChangeNote?: boolean;
  onSaved?: (skill: Skill) => void;
}) {
  const router = useRouter();

  const [name, setName] = React.useState(initial.name);
  const [title, setTitle] = React.useState(initial.title);
  const [description, setDescription] = React.useState(initial.description);
  const [triggersText, setTriggersText] = React.useState(
    initial.triggers.join(", ")
  );
  const [enabled, setEnabled] = React.useState(initial.enabled);
  const [body, setBody] = React.useState(initial.body);
  const [changeNote, setChangeNote] = React.useState("");

  const [submitting, setSubmitting] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  const parsedTriggers = React.useMemo(
    () =>
      triggersText
        .split(",")
        .map((t) => t.trim())
        .filter((t) => t.length > 0),
    [triggersText]
  );

  function validate(): string | null {
    if (mode === "create" && !SKILL_NAME_REGEX.test(name)) {
      return "Name must be lowercase letters, digits, and dashes (1–64 chars, starting with a letter or digit).";
    }
    if (!title.trim()) return "Title is required.";
    if (!description.trim()) return "Description is required.";
    if (!body.trim()) return "Body cannot be empty.";
    if (requireChangeNote && !changeNote.trim()) {
      return "Change note is required when saving an edit.";
    }
    return null;
  }

  async function handleSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setError(null);

    const validation = validate();
    if (validation) {
      setError(validation);
      return;
    }

    setSubmitting(true);

    try {
      if (mode === "create") {
        const created = await api<Skill>(
          `/api/workspaces/${slug}/skills`,
          {
            method: "POST",
            body: JSON.stringify({
              name: name.trim(),
              title: title.trim(),
              description: description.trim(),
              body,
              triggers: parsedTriggers,
              enabled,
            }),
          }
        );
        onSaved?.(created);
        router.push(`/workspaces/${slug}/skills/${created.id}`);
        router.refresh();
      } else if (mode === "edit" && skillId) {
        const updated = await api<Skill>(
          `/api/workspaces/${slug}/skills/${skillId}`,
          {
            method: "PUT",
            body: JSON.stringify({
              name: name.trim(),
              title: title.trim(),
              description: description.trim(),
              body,
              triggers: parsedTriggers,
              enabled,
              changeNote: changeNote.trim(),
            }),
          }
        );
        setChangeNote("");
        onSaved?.(updated);
        router.refresh();
      }
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Failed to save skill."
      );
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-5">
      <div className="grid gap-4 md:grid-cols-2">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="skill-name">Name</Label>
          <Input
            id="skill-name"
            value={name}
            onChange={(e) => setName(e.target.value.toLowerCase())}
            placeholder="my-skill"
            className="font-mono"
            readOnly={mode === "edit"}
            required
          />
          <p className="text-xs text-muted-foreground">
            Lowercase, digits, dashes. 1–64 chars. Used as the slug.
          </p>
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="skill-title">Title</Label>
          <Input
            id="skill-title"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="Human-readable title"
            required
          />
        </div>
      </div>

      <div className="flex flex-col gap-1.5">
        <Label htmlFor="skill-description">Description</Label>
        <Textarea
          id="skill-description"
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          placeholder="When and why the model should use this skill."
          className="min-h-20"
          required
        />
      </div>

      <div className="flex flex-col gap-1.5">
        <Label htmlFor="skill-triggers">Triggers</Label>
        <Textarea
          id="skill-triggers"
          value={triggersText}
          onChange={(e) => setTriggersText(e.target.value)}
          placeholder="comma, separated, keywords"
          className="min-h-16 font-mono text-xs"
        />
        <p className="text-xs text-muted-foreground">
          Comma-separated keywords or phrases.{" "}
          {parsedTriggers.length > 0 ? (
            <span>
              {parsedTriggers.length} trigger
              {parsedTriggers.length === 1 ? "" : "s"} parsed.
            </span>
          ) : (
            <span>None yet.</span>
          )}
        </p>
      </div>

      <div className="flex items-center gap-2">
        <input
          id="skill-enabled"
          type="checkbox"
          checked={enabled}
          onChange={(e) => setEnabled(e.target.checked)}
          className="size-4 rounded border-border accent-primary"
        />
        <Label htmlFor="skill-enabled" className="cursor-pointer">
          Enabled
        </Label>
      </div>

      <div className="flex flex-col gap-1.5">
        <Label>Body (markdown)</Label>
        <SkillMarkdownEditor
          value={body}
          onChange={setBody}
          height={500}
        />
      </div>

      {requireChangeNote ? (
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="skill-change-note">Change note</Label>
          <Input
            id="skill-change-note"
            value={changeNote}
            onChange={(e) => setChangeNote(e.target.value)}
            placeholder="What changed and why"
            required
          />
          <p className="text-xs text-muted-foreground">
            Recorded with this revision in the history.
          </p>
        </div>
      ) : null}

      {error ? (
        <Alert variant="destructive">
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : null}

      <div className="flex items-center justify-end gap-2">
        <Button
          type="button"
          variant="outline"
          onClick={() => router.push(`/workspaces/${slug}/skills`)}
          disabled={submitting}
        >
          Cancel
        </Button>
        <Button type="submit" disabled={submitting}>
          {submitting
            ? "Saving…"
            : mode === "create"
              ? "Create skill"
              : "Save changes"}
        </Button>
      </div>
    </form>
  );
}

export const DEFAULT_SKILL_BODY = DEFAULT_BODY;
