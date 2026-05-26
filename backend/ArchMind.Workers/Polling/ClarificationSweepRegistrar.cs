using ArchMind.Infrastructure.Data;
using ArchMind.Workers.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArchMind.Workers.Polling;

/// <summary>
/// BE-042 (Sprint 5): per-workspace recurring registration plumbing for
/// <see cref="ClarificationCandidateSweepJob"/>. One Hangfire recurring job per
/// workspace, fired every 6 hours.
/// </summary>
public interface IClarificationSweepRegistrar
{
    /// <summary>
    /// Add (or update) the recurring clarification-sweep job for each
    /// workspace. Idempotent — safe to call on every startup.
    /// </summary>
    void RegisterAll(IEnumerable<Guid> workspaceIds);

    /// <summary>
    /// Remove the recurring sweep job for the given workspace. Idempotent.
    /// </summary>
    void Unregister(Guid workspaceId);
}

/// <summary>
/// Default <see cref="IClarificationSweepRegistrar"/> backed by Hangfire's
/// <see cref="IRecurringJobManager"/>. Singleton — keeps no per-call state.
/// </summary>
public sealed class ClarificationSweepRegistrar : IClarificationSweepRegistrar
{
    /// <summary>Every 6 hours, on the hour. Matches BE-042 cadence.</summary>
    private const string CronExpression = "0 */6 * * *";

    private readonly IRecurringJobManager _recurringJobs;
    private readonly ILogger<ClarificationSweepRegistrar> _logger;

    public ClarificationSweepRegistrar(
        IRecurringJobManager recurringJobs,
        ILogger<ClarificationSweepRegistrar> logger)
    {
        _recurringJobs = recurringJobs;
        _logger = logger;
    }

    public void RegisterAll(IEnumerable<Guid> workspaceIds)
    {
        if (workspaceIds is null) return;
        var registered = 0;
        foreach (var workspaceId in workspaceIds)
        {
            if (workspaceId == Guid.Empty) continue;
            var jobId = BuildJobId(workspaceId);
            _recurringJobs.AddOrUpdate<ClarificationCandidateSweepJob>(
                jobId,
                j => j.RunAsync(workspaceId, default),
                CronExpression);
            registered++;
        }

        if (registered > 0)
        {
            _logger.LogInformation(
                "ClarificationSweepRegistrar registered count={Count} cron={Cron}",
                registered, CronExpression);
        }
    }

    public void Unregister(Guid workspaceId)
    {
        var jobId = BuildJobId(workspaceId);
        _recurringJobs.RemoveIfExists(jobId);
        _logger.LogInformation("ClarificationSweepRegistrar unregistered jobId={JobId}", jobId);
    }

    internal static string BuildJobId(Guid workspaceId) => $"clarification-sweep:{workspaceId}";
}

/// <summary>
/// Hosted service that runs once on application startup to register a
/// recurring <see cref="ClarificationCandidateSweepJob"/> for every existing
/// workspace. Mirrors <see cref="PollingStartupSync"/> but kept separate so
/// clarification scheduling failures don't entangle with repo polling.
/// </summary>
public sealed class ClarificationSweepStartupSync : IHostedService
{
    private readonly IClarificationSweepRegistrar _registrar;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClarificationSweepStartupSync> _logger;

    public ClarificationSweepStartupSync(
        IClarificationSweepRegistrar registrar,
        IServiceScopeFactory scopeFactory,
        ILogger<ClarificationSweepStartupSync> logger)
    {
        _registrar = registrar;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ArchMindDbContext>();

            var workspaceIds = await db.Workspaces
                .AsNoTracking()
                .Select(w => w.Id)
                .ToListAsync(cancellationToken);

            if (workspaceIds.Count == 0)
            {
                _logger.LogInformation("ClarificationSweepStartupSync: no workspaces, skipping.");
                return;
            }

            _registrar.RegisterAll(workspaceIds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ClarificationSweepStartupSync failed; sweep jobs will be (re)registered on next startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
