"use client";

import Editor, { type EditorProps } from "@monaco-editor/react";

type Props = {
  value: string;
  onChange?: (value: string) => void;
  readOnly?: boolean;
  height?: number | string;
};

/**
 * Thin wrapper around @monaco-editor/react locked to markdown + dark theme.
 * Imported via next/dynamic with `ssr: false` from its consumers so Monaco's
 * `window`-dependent loader never runs on the server.
 */
export default function SkillMarkdownEditor({
  value,
  onChange,
  readOnly = false,
  height = 500,
}: Props) {
  const options: EditorProps["options"] = {
    readOnly,
    wordWrap: "on",
    minimap: { enabled: false },
    fontSize: 13,
    lineNumbers: "on",
    scrollBeyondLastLine: false,
    automaticLayout: true,
    renderWhitespace: "none",
  };

  return (
    <div className="overflow-hidden rounded-md border border-border">
      <Editor
        height={height}
        defaultLanguage="markdown"
        language="markdown"
        theme="vs-dark"
        value={value}
        onChange={(v) => onChange?.(v ?? "")}
        options={options}
      />
    </div>
  );
}
