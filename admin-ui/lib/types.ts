// Shared API types used across admin-ui.

/**
 * Metadata for a workspace API key as returned by GET list and stored long-term.
 * Never includes the raw token — `plaintext` is only on the create response.
 */
export type WorkspaceApiKey = {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
};

/**
 * Response from POST /api/workspaces/{slug}/api-keys.
 * `plaintext` is the raw bearer token and is returned exactly once at creation.
 */
export type WorkspaceApiKeyCreated = {
  id: string;
  name: string;
  plaintext: string;
  prefix: string;
  createdAt: string;
};

/** Slug-style identifier rules for skill `name`. */
export const SKILL_NAME_REGEX = /^[a-z0-9][a-z0-9-]{0,63}$/;

/** Row returned by GET /api/workspaces/{slug}/skills (no body). */
export type SkillSummary = {
  id: string;
  name: string;
  title: string;
  description: string;
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
};

/** Full skill document including body and triggers. */
export type Skill = SkillSummary & {
  body: string;
  triggers: string[];
};

/** Revision metadata row (no body). */
export type SkillRevisionSummary = {
  version: number;
  changeNote: string | null;
  createdAt: string;
};

/** Full revision content. */
export type SkillRevision = SkillRevisionSummary & {
  name: string;
  title: string;
  description: string;
  body: string;
  triggers: string[];
  enabled: boolean;
};

/** Status of a clarification request as exposed by the API. */
export type ClarificationStatus = "Open" | "Answered" | "Dismissed";

/** Source pipeline that generated a clarification. */
export type ClarificationSource =
  | "FileExtraction"
  | "CrossFileCorrelation"
  | "ManualLlmGen";

/** Clarification record returned by the Sprint 5 clarifications API. */
export interface Clarification {
  id: string;
  source: ClarificationSource;
  topic: string;
  question: string;
  context: string | null;
  choices: string[];
  priority: number;
  status: ClarificationStatus;
  answer: string | null;
  answeredAt: string | null;
  relatedFilePaths: string[];
  relatedNodeNames: string[];
  createdAt: string;
  updatedAt: string;
}
