using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace ArchMind.Infrastructure.Telemetry;

/// <summary>
/// EF Core-backed <see cref="ITelemetryRecorder"/>. Inserts one row per call.
/// Telemetry persistence is best-effort: exceptions are logged but swallowed
/// so an MCP request / LLM call never fails because telemetry could not be
/// written.
/// </summary>
public sealed class TelemetryRecorder : ITelemetryRecorder
{
    private readonly ArchMindDbContext _db;
    private readonly ILogger<TelemetryRecorder> _logger;

    public TelemetryRecorder(ArchMindDbContext db, ILogger<TelemetryRecorder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RecordMcpAsync(McpTelemetryEntry entry, CancellationToken ct)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }
        if (entry.CreatedAt == default)
        {
            entry.CreatedAt = DateTimeOffset.UtcNow;
        }

        try
        {
            _db.McpTelemetry.Add(entry);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record MCP telemetry for workspace {WorkspaceId}", entry.WorkspaceId);
        }
    }

    public async Task RecordLlmCallAsync(LlmCallLog entry, CancellationToken ct)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }
        if (entry.CreatedAt == default)
        {
            entry.CreatedAt = DateTimeOffset.UtcNow;
        }

        try
        {
            _db.LlmCallLogs.Add(entry);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record LLM call log for workspace {WorkspaceId}", entry.WorkspaceId);
        }
    }
}
