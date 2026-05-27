using ArchMind.Core.Abstractions;
using ArchMind.Core.Extraction;
using ArchMind.Infrastructure.Anthropic;
using ArchMind.Infrastructure.Auth;
using ArchMind.Infrastructure.Clarifications;
using ArchMind.Infrastructure.Cloning;
using ArchMind.Infrastructure.Data;
using ArchMind.Infrastructure.Extraction;
using ArchMind.Infrastructure.GitHub;
using ArchMind.Infrastructure.Graph;
using ArchMind.Infrastructure.Graphify;
using ArchMind.Infrastructure.Llm;
using ArchMind.Infrastructure.Manifests;
using ArchMind.Infrastructure.Services;
using ArchMind.Infrastructure.Telemetry;
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

        // Singleton cache of the on-disk graphify-out/graph.json, shared across
        // all per-file LlmExtractionJob calls for the same (workspace, repo) pair.
        services.AddSingleton<IGraphifyContextService, GraphifyContextService>();

        // Workspace-level combined graph: merges all per-repo graph.json files,
        // colours nodes by repo, writes combined_graph.html + combined_graph.json
        // to <workspaceDir>/graphify-out/ after every successful scan.
        services.AddOptions<WorkspaceGraphOptions>().BindConfiguration("WorkspaceGraph");
        services.AddSingleton<IWorkspaceGraphService, WorkspaceGraphService>();

        // Read-side: parses combined_graph.json for the admin UI's Structural
        // tab and for LLM-assisted node search. In-memory cached per workspace
        // (15-min TTL); invalidated explicitly by WorkspaceGraphService after rebuild.
        services.AddSingleton<StructuralGraphService>();
        services.AddSingleton<IStructuralGraphService>(sp => sp.GetRequiredService<StructuralGraphService>());

        // Deterministic manifest scanner (csproj / package.json / pyproject).
        // Used to synthesise a Service node when LLM extraction failed to
        // identify one, and to emit cross-repo SHARES_PACKAGE_WITH edges.
        services.AddSingleton<IRepoManifestService, RepoManifestService>();

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

        // BE-044: graph schema validator. Enforces declared vertex/edge
        // labels and required-properties at the write boundary, and
        // surfaces drift between GraphSchema and the live AGE catalog.
        services.AddScoped<IGraphSchemaValidator, GraphSchemaValidator>();

        // BE-028: workspace API key issuance / validation / revocation.
        services.AddScoped<IApiKeyService, ApiKeyService>();

        // BE-032: MCP + LLM telemetry sink (best-effort EF inserts).
        services.AddScoped<ITelemetryRecorder, TelemetryRecorder>();

        // BE-035: heuristic skill matcher (no embeddings yet).
        services.AddScoped<ISkillMatcher, ArchMind.Infrastructure.Skills.SkillMatcher>();

        // BE-041 (Sprint 5): clarification dedupe-insert writer.
        services.AddScoped<IClarificationWriter, ClarificationWriter>();

        // BE-036 (Sprint 5): LLM-backed clarifying question generator (Haiku).
        services.AddScoped<IClarificationQuestionGenerator, ClarificationQuestionGenerator>();

        // BE-038 (Sprint 5): clarification prioritizer + orchestration intake.
        services.AddScoped<IClarificationPrioritizer, ClarificationPrioritizer>();
        services.AddScoped<IClarificationIntake, ClarificationIntakeService>();

        // BE-040 (Sprint 5): lookup of answered clarifications relevant to a
        // file under extraction. Consumed by LlmExtractionJob to inject ground
        // truth into the user prompt.
        services.AddScoped<IAnsweredClarificationLookup, AnsweredClarificationLookup>();

        return services;
    }
}
