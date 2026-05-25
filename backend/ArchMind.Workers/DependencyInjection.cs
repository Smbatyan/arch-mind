using ArchMind.Workers.Jobs;
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
        return services;
    }
}
