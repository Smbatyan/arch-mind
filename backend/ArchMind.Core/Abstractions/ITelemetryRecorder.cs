using ArchMind.Core.Entities;

namespace ArchMind.Core.Abstractions;

/// <summary>
/// Persists per-request MCP telemetry and per-call LLM telemetry rows. Wired
/// into MCP middleware (Wave 2) and the LLM router. Implementations should
/// treat both methods as best-effort — telemetry failures must not surface to
/// the caller as request failures.
/// </summary>
public interface ITelemetryRecorder
{
    /// <summary>Records one MCP request outcome.</summary>
    Task RecordMcpAsync(McpTelemetryEntry entry, CancellationToken ct);

    /// <summary>Records one LLM API call outcome.</summary>
    Task RecordLlmCallAsync(LlmCallLog entry, CancellationToken ct);
}
