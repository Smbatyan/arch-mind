namespace ArchMind.Core.Extraction;

/// <summary>
/// A single LLM extraction prompt: tool metadata, system + user templates, and the
/// JSON Schema the tool input must conform to. Versioned via <see cref="Version"/>;
/// bumping the version invalidates cached results cleanly because the version is
/// part of the cache key.
/// </summary>
public sealed record ExtractionPrompt(
    ExtractionPromptId Id,
    string Version,
    string ToolName,
    string ToolDescription,
    string SystemPrompt,
    string UserPromptTemplate,
    string OutputJsonSchema);
