using ArchMind.Infrastructure.Data;
using ArchMind.Workers.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArchMind.Workers.Polling;

/// <summary>
/// Default <see cref="IPollingRegistrar"/> backed by Hangfire's
/// <see cref="IRecurringJobManager"/>. Singleton — keeps no per-call state.
/// When <see cref="PollingOptions.Enabled"/> is false, <see cref="RegisterRepo"/>
/// becomes a no-op so tests and dev environments stay quiet.
/// </summary>
public sealed class PollingRegistrar : IPollingRegistrar
{
    private readonly IRecurringJobManager _recurringJobs;
    private readonly IOptionsMonitor<PollingOptions> _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PollingRegistrar> _logger;

    public PollingRegistrar(
        IRecurringJobManager recurringJobs,
        IOptionsMonitor<PollingOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<PollingRegistrar> logger)
    {
        _recurringJobs = recurringJobs;
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void RegisterRepo(Guid workspaceId, Guid repoId)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            _logger.LogDebug(
                "Polling disabled; skipping RegisterRepo workspace={WorkspaceId} repo={RepoId}",
                workspaceId, repoId);
            return;
        }

        var jobId = BuildJobId(workspaceId, repoId);
        _recurringJobs.AddOrUpdate<PollRepoJob>(
            jobId,
            j => j.RunAsync(workspaceId, repoId, default),
            options.CronExpression);

        _logger.LogInformation(
            "Polling registered jobId={JobId} cron={Cron}",
            jobId, options.CronExpression);
    }

    public void UnregisterRepo(Guid workspaceId, Guid repoId)
    {
        var jobId = BuildJobId(workspaceId, repoId);
        _recurringJobs.RemoveIfExists(jobId);
        _logger.LogInformation("Polling unregistered jobId={JobId}", jobId);
    }

    public async Task SynchronizeAllAsync(CancellationToken ct = default)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            _logger.LogInformation("Polling disabled; skipping SynchronizeAllAsync.");
            return;
        }

        // Scoped DbContext access — registrar itself is singleton.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchMindDbContext>();

        var repos = await db.Repos
            .AsNoTracking()
            .Select(r => new { r.WorkspaceId, r.Id })
            .ToListAsync(ct);

        foreach (var repo in repos)
        {
            ct.ThrowIfCancellationRequested();
            RegisterRepo(repo.WorkspaceId, repo.Id);
        }

        _logger.LogInformation("Polling synchronized count={Count}", repos.Count);
    }

    internal static string BuildJobId(Guid workspaceId, Guid repoId) =>
        $"poll-{workspaceId}-{repoId}";
}
