using System.Collections.Concurrent;

namespace ArchMind.Api.Mcp;

/// <summary>
/// Tracks live MCP sessions in-process. Backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// for the MVP — a distributed store (Redis / Postgres) will replace this when we scale beyond
/// a single API instance.
/// </summary>
public interface IMcpSessionStore
{
    McpSession Create(Guid workspaceId, Guid? apiKeyId = null);
    McpSession? Get(Guid sessionId);
    bool Remove(Guid sessionId);
    void Touch(Guid sessionId);
}

public sealed class InMemoryMcpSessionStore : IMcpSessionStore
{
    private readonly ConcurrentDictionary<Guid, McpSession> _sessions = new();
    private readonly TimeProvider _clock;

    public InMemoryMcpSessionStore(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    public McpSession Create(Guid workspaceId, Guid? apiKeyId = null)
    {
        var session = new McpSession(Guid.NewGuid(), workspaceId, _clock.GetUtcNow().UtcDateTime, apiKeyId);
        _sessions[session.Id] = session;
        return session;
    }

    public McpSession? Get(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public bool Remove(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.Channel.Writer.TryComplete();
            return true;
        }
        return false;
    }

    public void Touch(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.LastActivityAt = _clock.GetUtcNow().UtcDateTime;
        }
    }
}
