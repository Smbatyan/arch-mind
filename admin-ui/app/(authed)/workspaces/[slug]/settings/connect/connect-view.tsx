"use client";

import * as React from "react";
import Link from "next/link";
import {
  CheckIcon,
  CopyIcon,
  KeyRoundIcon,
  TerminalIcon,
  MousePointerIcon,
  ArrowRightIcon,
  SparklesIcon,
} from "lucide-react";

import type { WorkspaceApiKey } from "@/lib/types";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type Platform = "claude-code" | "cursor";

// ---------------------------------------------------------------------------
// Copy hook
// ---------------------------------------------------------------------------

function useCopy(text: string) {
  const [copied, setCopied] = React.useState(false);
  const copy = React.useCallback(async () => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // Ignore — user can copy manually.
    }
  }, [text]);
  return { copied, copy };
}

// ---------------------------------------------------------------------------
// Terminal code block
// Renders syntax-highlighted children but copies `copyText` (plain string).
// ---------------------------------------------------------------------------

function TerminalBlock({
  copyText,
  filename,
  children,
}: {
  copyText: string;
  filename: string;
  children: React.ReactNode;
}) {
  const { copied, copy } = useCopy(copyText);

  return (
    <div className="overflow-hidden rounded-xl border border-border/60 shadow-sm">
      {/* macOS-style title bar */}
      <div className="flex items-center gap-3 border-b border-border/50 bg-muted/60 px-4 py-2.5">
        <div className="flex items-center gap-1.5" aria-hidden>
          <div className="size-2.5 rounded-full bg-red-400/70" />
          <div className="size-2.5 rounded-full bg-amber-400/70" />
          <div className="size-2.5 rounded-full bg-emerald-400/70" />
        </div>
        <span className="flex-1 text-center font-mono text-[10px] tracking-wide text-muted-foreground">
          {filename}
        </span>
        <Button
          variant="ghost"
          size="sm"
          onClick={() => void copy()}
          className="ml-auto h-6 gap-1.5 px-2 text-[11px] text-muted-foreground hover:text-foreground"
        >
          {copied ? (
            <>
              <CheckIcon className="size-3 text-emerald-400" />
              <span>Copied!</span>
            </>
          ) : (
            <>
              <CopyIcon className="size-3" />
              <span>Copy</span>
            </>
          )}
        </Button>
      </div>

      {/* Code body — intentionally dark regardless of light/dark mode */}
      <pre
        className="overflow-x-auto p-4 text-[13px] leading-relaxed"
        style={{
          background: "oklch(0.13 0.012 260)",
          color: "oklch(0.87 0.012 250)",
          fontFamily: "var(--font-mono, ui-monospace, monospace)",
          tabSize: 2,
        }}
      >
        <code>{children}</code>
      </pre>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Syntax helpers — keep the JSX readable
// ---------------------------------------------------------------------------

const T = {
  kw: (s: string) => (
    <span style={{ color: "oklch(0.78 0.18 295)" }}>{s}</span>
  ),
  cmd: (s: string) => (
    <span style={{ color: "oklch(0.76 0.14 230)" }}>{s}</span>
  ),
  flag: (s: string) => (
    <span style={{ color: "oklch(0.80 0.15 65)" }}>{s}</span>
  ),
  str: (s: string) => (
    <span style={{ color: "oklch(0.75 0.15 155)" }}>{s}</span>
  ),
  url: (s: string) => (
    <span style={{ color: "oklch(0.72 0.16 230)" }}>{s}</span>
  ),
  placeholder: (s: string) => (
    <span
      style={{
        color: "oklch(0.72 0.18 25)",
        textDecoration: "underline",
        textDecorationStyle: "dotted",
        textDecorationColor: "oklch(0.72 0.18 25 / 0.5)",
      }}
    >
      {s}
    </span>
  ),
  dim: (s: string) => (
    <span style={{ color: "oklch(0.55 0.008 250)" }}>{s}</span>
  ),
  key: (s: string) => (
    <span style={{ color: "oklch(0.76 0.14 230)" }}>{s}</span>
  ),
};

// ---------------------------------------------------------------------------
// Platform-specific command renderers
// ---------------------------------------------------------------------------

function ClaudeCodeCommand({ mcpUrl }: { mcpUrl: string }) {
  const plaintext = [
    "claude mcp add archmind \\",
    "  -t http \\",
    `  -H "X-Api-Key: <YOUR_API_KEY>" \\`,
    "  -s user \\",
    `  ${mcpUrl}`,
  ].join("\n");

  return (
    <TerminalBlock copyText={plaintext} filename="Terminal">
      {T.kw("claude")}{" "}
      {T.cmd("mcp add archmind")}&nbsp;{"\\"}
      {"\n  "}
      {T.flag("-t")}&nbsp;http&nbsp;{"\\"}
      {"\n  "}
      {T.flag("-H")}&nbsp;
      {T.str('"X-Api-Key: ')}
      {T.placeholder("<YOUR_API_KEY>")}
      {T.str('"')}&nbsp;{"\\"}
      {"\n  "}
      {T.flag("-s")}&nbsp;user&nbsp;{"\\"}
      {"\n  "}
      {T.url(mcpUrl)}
    </TerminalBlock>
  );
}

function CursorConfig({ mcpUrl }: { mcpUrl: string }) {
  const json = JSON.stringify(
    {
      mcpServers: {
        archmind: {
          url: mcpUrl,
          headers: { "X-Api-Key": "<YOUR_API_KEY>" },
        },
      },
    },
    null,
    2
  );

  return (
    <TerminalBlock copyText={json} filename="~/.cursor/mcp.json">
      {T.dim("{")}
      {"\n  "}
      {T.key('"mcpServers"')}
      {T.dim(": {")}
      {"\n    "}
      {T.key('"archmind"')}
      {T.dim(": {")}
      {"\n      "}
      {T.key('"url"')}
      {T.dim(": ")}
      {T.str(`"${mcpUrl}"`)}
      {T.dim(",")}
      {"\n      "}
      {T.key('"headers"')}
      {T.dim(": {")}
      {"\n        "}
      {T.key('"X-Api-Key"')}
      {T.dim(": ")}
      {T.str('"')}
      {T.placeholder("<YOUR_API_KEY>")}
      {T.str('"')}
      {"\n      "}
      {T.dim("}")}
      {"\n    "}
      {T.dim("}")}
      {"\n  "}
      {T.dim("}")}
      {"\n"}
      {T.dim("}")}
    </TerminalBlock>
  );
}

// ---------------------------------------------------------------------------
// Platform card / switcher
// ---------------------------------------------------------------------------

type PlatformOption = {
  id: Platform;
  label: string;
  description: string;
  icon: React.ReactNode;
  badge?: string;
};

const PLATFORMS: PlatformOption[] = [
  {
    id: "claude-code",
    label: "Claude Code",
    description: "Run one command in your terminal",
    icon: <TerminalIcon className="size-5" />,
    badge: "Recommended",
  },
  {
    id: "cursor",
    label: "Cursor",
    description: "Add JSON to ~/.cursor/mcp.json",
    icon: <MousePointerIcon className="size-5" />,
  },
];

function PlatformCard({
  option,
  selected,
  onSelect,
}: {
  option: PlatformOption;
  selected: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onSelect}
      className={[
        "relative flex flex-1 flex-col gap-2 rounded-xl border p-4 text-left transition-all duration-150",
        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        selected
          ? "border-primary/60 bg-primary/5 shadow-[0_0_0_1px_oklch(0.68_0.235_295_/_0.3)] shadow-primary/10"
          : "border-border bg-card hover:border-border/80 hover:bg-muted/30",
      ].join(" ")}
    >
      {/* Selected indicator */}
      {selected && (
        <span
          className="absolute right-3 top-3 flex size-5 items-center justify-center rounded-full"
          style={{
            background:
              "linear-gradient(135deg, oklch(0.68 0.235 295), oklch(0.62 0.200 265))",
          }}
        >
          <CheckIcon className="size-3 text-white" />
        </span>
      )}

      <span
        className={
          selected ? "text-primary" : "text-muted-foreground"
        }
      >
        {option.icon}
      </span>

      <div className="flex items-center gap-2">
        <span className="text-sm font-medium leading-tight">{option.label}</span>
        {option.badge && (
          <Badge variant="secondary" className="text-[10px] px-1.5 py-0">
            {option.badge}
          </Badge>
        )}
      </div>

      <span className="text-xs text-muted-foreground leading-snug">
        {option.description}
      </span>
    </button>
  );
}

// ---------------------------------------------------------------------------
// Steps list
// ---------------------------------------------------------------------------

function Steps({
  platform,
  slug,
  hasActiveKeys,
}: {
  platform: Platform;
  slug: string;
  hasActiveKeys: boolean;
}) {
  const steps =
    platform === "claude-code"
      ? [
          {
            n: 1,
            text: hasActiveKeys ? (
              <>
                Copy your API key from the{" "}
                <Link
                  href={`/workspaces/${slug}/settings/api-keys`}
                  className="text-primary underline underline-offset-2 hover:opacity-80"
                >
                  API Keys
                </Link>{" "}
                page (shown once at creation).
              </>
            ) : (
              <>
                <Link
                  href={`/workspaces/${slug}/settings/api-keys`}
                  className="text-primary underline underline-offset-2 hover:opacity-80"
                >
                  Create an API key
                </Link>{" "}
                for this workspace first.
              </>
            ),
          },
          {
            n: 2,
            text: (
              <>
                Copy the command above, replace{" "}
                <code className="rounded bg-muted px-1 py-0.5 font-mono text-[11px]">
                  &lt;YOUR_API_KEY&gt;
                </code>{" "}
                with your actual key, and run it in a terminal.
              </>
            ),
          },
          {
            n: 3,
            text: (
              <>
                Ask Claude Code to use the{" "}
                <code className="rounded bg-muted px-1 py-0.5 font-mono text-[11px]">
                  archmind
                </code>{" "}
                MCP tool — it will connect automatically.
              </>
            ),
          },
        ]
      : [
          {
            n: 1,
            text: hasActiveKeys ? (
              <>
                Copy your API key from the{" "}
                <Link
                  href={`/workspaces/${slug}/settings/api-keys`}
                  className="text-primary underline underline-offset-2 hover:opacity-80"
                >
                  API Keys
                </Link>{" "}
                page (shown once at creation).
              </>
            ) : (
              <>
                <Link
                  href={`/workspaces/${slug}/settings/api-keys`}
                  className="text-primary underline underline-offset-2 hover:opacity-80"
                >
                  Create an API key
                </Link>{" "}
                for this workspace first.
              </>
            ),
          },
          {
            n: 2,
            text: (
              <>
                Open or create{" "}
                <code className="rounded bg-muted px-1 py-0.5 font-mono text-[11px]">
                  ~/.cursor/mcp.json
                </code>
                . Paste the config above, replacing{" "}
                <code className="rounded bg-muted px-1 py-0.5 font-mono text-[11px]">
                  &lt;YOUR_API_KEY&gt;
                </code>{" "}
                with your actual key.
              </>
            ),
          },
          {
            n: 3,
            text: (
              <>
                Restart Cursor. The{" "}
                <code className="rounded bg-muted px-1 py-0.5 font-mono text-[11px]">
                  archmind
                </code>{" "}
                server will appear in Cursor&apos;s MCP panel.
              </>
            ),
          },
        ];

  return (
    <ol className="flex flex-col gap-3">
      {steps.map((step) => (
        <li key={step.n} className="flex items-start gap-3">
          <span
            className="mt-0.5 flex size-5 shrink-0 items-center justify-center rounded-full text-[10px] font-bold text-white"
            style={{
              background:
                "linear-gradient(135deg, oklch(0.68 0.235 295), oklch(0.62 0.200 265))",
            }}
          >
            {step.n}
          </span>
          <span className="text-sm leading-relaxed text-muted-foreground">
            {step.text}
          </span>
        </li>
      ))}
    </ol>
  );
}

// ---------------------------------------------------------------------------
// Quick Commands section (Claude Code only)
// ---------------------------------------------------------------------------

function QuickCommandsSection({ commandsUrl }: { commandsUrl: string }) {
  const curlCmd = [
    "mkdir -p ~/.claude/commands && \\",
    `  curl -o ~/.claude/commands/archmind.md \\`,
    `  ${commandsUrl}`,
  ].join("\n");

  const { copied, copy } = useCopy(curlCmd);

  return (
    <div className="flex flex-col gap-4 rounded-xl border border-border/50 bg-muted/20 p-5">
      <div className="flex items-start gap-3">
        <span
          className="mt-0.5 flex size-8 shrink-0 items-center justify-center rounded-lg"
          style={{
            background:
              "linear-gradient(135deg, oklch(0.68 0.235 295 / 0.15), oklch(0.62 0.200 265 / 0.15))",
          }}
        >
          <SparklesIcon className="size-4 text-primary" />
        </span>
        <div>
          <p className="text-sm font-medium">Add <code className="rounded bg-muted px-1 py-0.5 font-mono text-[11px]">/archmind</code> shortcut</p>
          <p className="mt-0.5 text-xs text-muted-foreground">
            Installs a Claude Code slash command so you never need to type MCP boilerplate.
          </p>
        </div>
      </div>

      <TerminalBlock copyText={curlCmd} filename="Terminal">
        {T.kw("mkdir")}&nbsp;
        {T.flag("-p")}&nbsp;
        {T.str("~/.claude/commands")}&nbsp;{"&&"}&nbsp;{"\\"}
        {"\n  "}
        {T.cmd("curl")}&nbsp;
        {T.flag("-o")}&nbsp;
        {T.str("~/.claude/commands/archmind.md")}&nbsp;{"\\"}
        {"\n  "}
        {T.url(commandsUrl)}
      </TerminalBlock>

      <div className="flex items-start gap-2 rounded-lg border border-border/40 bg-background/60 px-4 py-3">
        <span className="mt-0.5 text-base">💡</span>
        <p className="text-xs text-muted-foreground leading-relaxed">
          Then type&nbsp;
          <code className="rounded bg-muted px-1 py-0.5 font-mono text-[11px] text-foreground">
            /archmind find all unused API endpoints
          </code>
          &nbsp;in Claude Code — it will route straight to ArchMind.
        </p>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main export
// ---------------------------------------------------------------------------

export function ConnectView({
  slug,
  mcpUrl,
  commandsUrl,
  activeKeys,
}: {
  slug: string;
  mcpUrl: string;
  commandsUrl: string;
  activeKeys: WorkspaceApiKey[];
}) {
  const [platform, setPlatform] = React.useState<Platform>("claude-code");
  const hasActiveKeys = activeKeys.length > 0;

  return (
    <div className="flex flex-col gap-6">
      {/* No active keys warning */}
      {!hasActiveKeys && (
        <Alert>
          <KeyRoundIcon className="size-4" />
          <AlertDescription className="flex items-center justify-between gap-2">
            <span>
              You need an active API key before you can connect a client.
            </span>
            <Link
              href={`/workspaces/${slug}/settings/api-keys`}
              className="inline-flex shrink-0 items-center gap-1 rounded-lg border border-border bg-background px-2.5 py-1 text-xs font-medium hover:bg-muted transition-colors"
            >
              Create key <ArrowRightIcon className="size-3" />
            </Link>
          </AlertDescription>
        </Alert>
      )}

      {/* Platform selector */}
      <div>
        <p className="mb-3 text-xs font-medium uppercase tracking-wider text-muted-foreground">
          Platform
        </p>
        <div className="flex gap-3">
          {PLATFORMS.map((opt) => (
            <PlatformCard
              key={opt.id}
              option={opt}
              selected={platform === opt.id}
              onSelect={() => setPlatform(opt.id)}
            />
          ))}
        </div>
      </div>

      {/* MCP URL info row */}
      <div className="flex items-center gap-2 rounded-lg border border-border/50 bg-muted/30 px-4 py-2.5">
        <span className="text-xs text-muted-foreground shrink-0">MCP URL</span>
        <code className="flex-1 font-mono text-xs text-foreground/80 truncate">
          {mcpUrl}
        </code>
        <Badge variant="secondary" className="shrink-0 text-[10px]">
          pre-filled
        </Badge>
      </div>

      {/* Command / config block */}
      <div>
        <p className="mb-3 text-xs font-medium uppercase tracking-wider text-muted-foreground">
          {platform === "claude-code" ? "Command" : "Configuration"}
        </p>
        {platform === "claude-code" ? (
          <ClaudeCodeCommand mcpUrl={mcpUrl} />
        ) : (
          <CursorConfig mcpUrl={mcpUrl} />
        )}
      </div>

      {/* Steps */}
      <div>
        <p className="mb-3 text-xs font-medium uppercase tracking-wider text-muted-foreground">
          Steps
        </p>
        <Steps
          platform={platform}
          slug={slug}
          hasActiveKeys={hasActiveKeys}
        />
      </div>

      {/* Quick Commands — Claude Code only */}
      {platform === "claude-code" && (
        <QuickCommandsSection commandsUrl={commandsUrl} />
      )}
    </div>
  );
}
