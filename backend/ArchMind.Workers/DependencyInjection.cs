using ArchMind.Core.Abstractions;
using ArchMind.Workers.Jobs;
using ArchMind.Workers.Pipelines;
using ArchMind.Workers.Polling;
using Microsoft.Extensions.DependencyInjection;

namespace ArchMind.Workers;

/// <summary>
/// DI registrations for Hangfire job classes that live in <c>ArchMind.Workers</c>.
/// Lives in the Workers project (rather than <c>ArchMind.Infrastructure</c>) because
/// jobs now depend on <c>ArchMindDbContext</c> from Infrastructure, so Workers
/// references Infrastructure — the inverse would be a circular project reference.
/// Called from <c>Program.cs</c> after <c>AddArchMindInfrastructure</c>.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddArchMindWorkers(this IServiceCollection services)
    {
        services.AddScoped<LlmExtractionJob>();
        services.AddScoped<InitialScanJob>();
        services.AddScoped<DiffScanJob>();
        services.AddScoped<FullRescanJob>();
        services.AddScoped<IRepoScanPipeline, RepoScanPipeline>();

        // BE-026: cross-file correlator (Sonnet). Enqueued by the scan pipeline
        // after per-file extraction has settled.
        services.AddScoped<CrossFileCorrelationJob>();

        // Workspace-scoped graph connector. Scheduled by CrossFileCorrelationJob
        // after each repo's correlation settles, to wire CALLS edges across repos.
        services.AddScoped<CrossRepoCorrelationJob>();

        // BE-024: per-repo recurring poll job + registration plumbing.
        services.AddScoped<PollRepoJob>();
        services.AddOptions<PollingOptions>().BindConfiguration("Polling");
        services.AddSingleton<IPollingRegistrar, PollingRegistrar>();
        services.AddHostedService<PollingStartupSync>();

        // BE-042: per-workspace recurring clarification sweep + startup sync.
        services.AddScoped<ClarificationCandidateSweepJob>();
        services.AddSingleton<IClarificationSweepRegistrar, ClarificationSweepRegistrar>();
        services.AddHostedService<ClarificationSweepStartupSync>();

        return services;
    }
}
