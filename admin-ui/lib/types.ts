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

// =============================================================================
// Sprint 6 reporting / dashboard types
//
// CostUsd values come back from the API as strings to preserve precision —
// always parse with `Number(...)` only at the display boundary.
// =============================================================================

export interface RepoKpi {
  total: number;
  active: number;
  lastScanAt: string | null;
}

export interface GraphLabelCount {
  label: string;
  count: number;
}

export interface GraphKpi {
  totalNodes: number;
  totalEdges: number;
  topLabels: GraphLabelCount[];
}

export interface ExtractionKpi {
  totalFiles: number;
  cachedPct: number;
}

export interface ClarificationKpi {
  open: number;
  answered: number;
  dismissed: number;
}

export interface SkillsKpi {
  total: number;
  enabled: number;
}

export interface LlmSpendKpi {
  totalUsd: string;
  totalCalls: number;
  cacheHitPct: number;
}

export interface McpActivityKpi {
  totalCalls: number;
  errorRatePct: number;
  p95LatencyMs: number;
}

export interface ReportSummary {
  repos: RepoKpi;
  graph: GraphKpi;
  extractions: ExtractionKpi;
  clarifications: ClarificationKpi;
  skills: SkillsKpi;
  llmSpend: LlmSpendKpi;
  mcpActivity: McpActivityKpi;
}

export type ScanStatus =
  | "Pending"
  | "Running"
  | "Completed"
  | "Failed"
  | "Cancelled";

export interface ScanSummary {
  id: string;
  repoId: string;
  repoUrl: string;
  startedAt: string;
  finishedAt: string | null;
  durationMs: number | null;
  fileCount: number;
  costUsd: string;
  status: ScanStatus;
}

export interface ScanDetail extends ScanSummary {
  defaultBranch: string | null;
  commitSha: string | null;
  nodesAdded: number;
  edgesAdded: number;
  cachedFiles: number;
  errorMessage: string | null;
  logs: string | null;
}

export interface DailyLlmSpend {
  day: string; // ISO date (YYYY-MM-DD)
  costUsd: string;
  calls: number;
  cachedCalls: number;
}

export interface DailyMcpActivity {
  day: string; // ISO date (YYYY-MM-DD)
  calls: number;
  errors: number;
  p95LatencyMs: number;
}
