using System.Text.Json;
using System.Text.Json.Serialization;
using ArchMind.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArchMind.Api.Mcp;

/// <summary>
/// MCP <c>prompts/list</c> and <c>prompts/get</c> handler.
///
/// Exposes workspace-scoped <see cref="ArchMind.Core.Entities.Skill"/> rows as
/// MCP prompts so AI clients can discover and load user-authored context on
/// demand. The skill <c>Name</c> slug is used as the prompt name; the skill
/// <c>Body</c> markdown is returned as a single text message on retrieval.
/// </summary>
public sealed class McpPromptsHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<McpPromptsHandler> _logger;

    public McpPromptsHandler(ILogger<McpPromptsHandler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns the catalog of enabled skills for the workspace as MCP prompts.
    /// </summary>
    public async Task<McpResponse> HandleListAsync(
        McpRequest request,
        Guid workspaceId,
        ArchMindDbContext db,
        CancellationToken ct)
    {
        var skills = await db.Set<ArchMind.Core.Entities.Skill>()
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.Enabled)
            .OrderBy(s => s.Name)
            .Select(s => new { s.Name, s.Title, s.Description })
            .ToListAsync(ct);

        var prompts = skills.Select(s => new
        {
            name = s.Name,
            description = string.IsNullOrWhiteSpace(s.Description) ? s.Title : s.Description,
            arguments = Array.Empty<object>(),
        }).ToArray();

        return BuildResponse(request.Id, new { prompts });
    }

    /// <summary>
    /// Returns a single skill rendered as MCP prompt messages.
    /// </summary>
    public async Task<McpResponse> HandleGetAsync(
        McpRequest request,
        Guid workspaceId,
        ArchMindDbContext db,
        CancellationToken ct)
    {
        var name = TryGetStringParam(request.Params, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Error(request.Id, McpErrorCodes.InvalidParams, "missing 'name' parameter");
        }

        var skill = await db.Set<ArchMind.Core.Entities.Skill>()
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.Name == name && s.Enabled)
            .Select(s => new { s.Title, s.Description, s.Body })
            .FirstOrDefaultAsync(ct);

        if (skill is null)
        {
            return Error(request.Id, McpErrorCodes.InvalidParams, $"skill not found: {name}");
        }

        return BuildResponse(request.Id, new
        {
            description = string.IsNullOrWhiteSpace(skill.Description) ? skill.Title : skill.Description,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new { type = "text", text = skill.Body },
                },
            },
        });
    }

    private static string? TryGetStringParam(JsonElement? @params, string name)
    {
        if (@params is not JsonElement p || p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        return v.GetString();
    }

    private static McpResponse BuildResponse(JsonElement? id, object result) =>
        new(JsonRpc: "2.0", Id: id, Result: result, Error: null);

    private static McpResponse Error(JsonElement? id, int code, string message) =>
        new(JsonRpc: "2.0", Id: id, Result: null, Error: new McpError(code, message, null));
}
