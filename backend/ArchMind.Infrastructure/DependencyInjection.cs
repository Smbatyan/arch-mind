using ArchMind.Core.Abstractions;
using ArchMind.Core.Extraction;
using ArchMind.Infrastructure.Anthropic;
using ArchMind.Infrastructure.Cloning;
using ArchMind.Infrastructure.Data;
using ArchMind.Infrastructure.Extraction;
using ArchMind.Infrastructure.GitHub;
using ArchMind.Infrastructure.Graph;
using ArchMind.Infrastructure.Graphify;
using ArchMind.Infrastructure.Llm;
using ArchMind.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using GitHubClientImpl = ArchMind.Infrastructure.GitHub.GitHubClient;

namespace ArchMind.Infrastructure;

/// <summary>
/// Composition root for ArchMind.Infrastructure services.
/// Wired in Program.cs once Wave 1 agents converge.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddArchMindInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<ILlmExtractionCacheService, LlmExtractionCacheService>();
        services.AddSingleton<IGitHubClient, GitHubClientImpl>();

        // BE-014: Anthropic API client (raw HTTP, retries 3x with backoff).
        services.AddOptions<AnthropicOptions>().BindConfiguration("Anthropic");
        services.AddHttpClient<IAnthropicClient, AnthropicClient>(c =>
        {
            c.BaseAddress = new Uri("https://api.anthropic.com");
            c.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        });

        // BE-015: LLM router (Haiku vs Sonnet dispatch + telemetry log).
        services.AddScoped<ILlmRouter, LlmRouter>();

        // BE-012: Repo cloning service (shells out to `git`; stateless).
        services.AddOptions<CloningOptions>().BindConfiguration("Cloning");
        services.AddSingleton<IRepoCloneService, RepoCloneService>();

        // BE-013: Graphify subprocess wrapper (extracts structural AST graph).
        services.AddOptions<GraphifyOptions>().BindConfiguration("Graphify");
        services.AddSingleton<IGraphifyRunner, GraphifyRunner>();

        // BE-017 / BE-018: extraction prompt library + per-file content resolver
        // and repository. The Hangfire job class itself is registered in
        // ArchMind.Workers' AddArchMindWorkers() extension (Workers depends on
        // Infrastructure, so registrations live downstream to avoid a circular
        // project reference).
        services.AddSingleton<IReadOnlyDictionary<ExtractionPromptId, ExtractionPrompt>>(
            _ => ExtractionPromptLibrary.All);
        services.AddScoped<IFileContentResolver, FileContentResolver>();
        services.AddScoped<IFileExtractionRepository, FileExtractionRepository>();

        // BE-021: Dapper-backed graph write API over Apache AGE.
        // NpgsqlConnectionFactory is singleton (only holds the connection
        // string); GraphWriter is scoped so callers can pull it inside a
        // request/job lifetime.
        services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
        services.AddScoped<IGraphWriter, GraphWriter>();

        // BE-022: Dapper-backed graph read API over Apache AGE. Shares the
        // IDbConnectionFactory registered above (LOAD 'age' + search_path
        // bootstrap runs per-connection there).
        services.AddScoped<IGraphReader, GraphReader>();

        return services;
    }
}
