"use client";

import {
  DEFAULT_SKILL_BODY,
  SkillEditorForm,
} from "../skill-editor-form";

export function NewSkillView({ slug }: { slug: string }) {
  return (
    <SkillEditorForm
      slug={slug}
      mode="create"
      initial={{
        name: "",
        title: "",
        description: "",
        triggers: [],
        enabled: true,
        body: DEFAULT_SKILL_BODY,
      }}
    />
  );
}
