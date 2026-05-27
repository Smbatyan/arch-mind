using System.Text;
using ArchMind.Api.Auth;
using ArchMind.Api.Endpoints;
using ArchMind.Api.Mcp;
using ArchMind.Api.Middleware;
using ArchMind.Api.Smoke;
using ArchMind.Core.Abstractions;
using ArchMind.Infrastructure;
using ArchMind.Infrastructure.Auth;
using ArchMind.Infrastructure.Data;
using ArchMind.Workers;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;

// ---------------------------------------------------------------------------
// Bootstrap Serilog as early as possible so even startup failures are logged.
// ---------------------------------------------------------------------------
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // -----------------------------------------------------------------------
    // Kestrel server limits (DO-008)
    //
    // SSE-friendly tuning for the MCP endpoint at /mcp/{workspaceSlug}:
    //   * Raise KeepAliveTimeout well beyond the 15s heartbeat cadence so
    //     intermediaries that honor it don't drop idle streams.
    //   * Cap request bodies at 10 MiB (well above any JSON-RPC payload).
    //   * No per-request timeout — SSE cancellation is driven by client
    //     disconnect / app-level CancellationToken inside HandleGetAsync.
    // -----------------------------------------------------------------------
    builder.WebHost.ConfigureKestrel(o =>
    {
        o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
        o.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
        // No per-request timeout for SSE — handled by app-level cancellation.
    });

    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------
    var connectionString = builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

    var logDirectory = builder.Configuration["Logging:FilePath"] ?? "/var/log/archmind/";

    // -----------------------------------------------------------------------
    // Serilog: JSON to stdout + rolling file. Fall back to ./logs/ if the
    // configured directory isn't writable (typical on dev macs/windows).
    // -----------------------------------------------------------------------
    var resolvedLogDirectory = ResolveWritableLogDirectory(logDirectory);
    var logFilePath = Path.Combine(resolvedLogDirectory, "archmind-.log");

    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentName()
            .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
            .WriteTo.File(
                new Serilog.Formatting.Json.JsonFormatter(),
                logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7);
    });

    // -----------------------------------------------------------------------
    // EF Core / Postgres
    // -----------------------------------------------------------------------
    builder.Services.AddDbContext<ArchMindDbContext>(options =>
        options.UseNpgsql(connectionString));

    // Named HttpClient for outbound calls (e.g. RepoEndpoints.DiscoverOrgAsync
    // hitting api.github.com). 30s timeout covers paginated repo listings.
    builder.Services.AddHttpClient("github", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(30);
    });

    // -----------------------------------------------------------------------
    // Hangfire (Postgres storage, same connection string)
    // -----------------------------------------------------------------------
    builder.Services.AddHangfire(config =>
    {
        config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString));
    });
    // Default-queue server: per-file LlmExtractionJob + everything that doesn't
    // opt into a dedicated queue. Tight worker cap protects the Anthropic
    // rate-limit (2 workers × ~6 LLM calls per file = 12 concurrent requests,
    // well under the 50 req/min org limit).
    builder.Services.AddHangfireServer(opts =>
    {
        opts.ServerName = "default-worker";
        opts.WorkerCount = 2;
        opts.Queues = new[] { "default" };
    });

    // Scan-queue server: caps concurrent repo scans (InitialScanJob /
    // FullRescanJob) at 5. Each scan is mostly IO (git clone + graphify
    // structural extract); the heavy per-file LLM work is enqueued onto the
    // default queue where the tighter worker cap above governs throughput.
    builder.Services.AddHangfireServer(opts =>
    {
        opts.ServerName = "scan-worker";
        opts.WorkerCount = 5;
        opts.Queues = new[] { "scan" };
    });

    // -----------------------------------------------------------------------
    // Auth: stateless JWT bearer. Token is read from either the Authorization
    // header or the "archmind.sid" cookie so SSR pages and bearer clients both
    // work without any session storage. 10-day fixed expiry (no sliding).
    // -----------------------------------------------------------------------
    builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();

    var jwtKey = builder.Configuration["Jwt:Key"]
        ?? throw new InvalidOperationException("Jwt:Key is not configured.");
    var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "archmind";
    var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "archmind";

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtIssuer,
                ValidAudience = jwtAudience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                ClockSkew = TimeSpan.FromMinutes(1),
            };
            // Also accept the JWT carried as the "archmind.sid" cookie so SSR
            // pages that already read that cookie keep working unchanged.
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    if (string.IsNullOrEmpty(ctx.Token) &&
                        ctx.Request.Cookies.TryGetValue("archmind.sid", out var cookieToken) &&
                        !string.IsNullOrEmpty(cookieToken))
                    {
                        ctx.Token = cookieToken;
                    }
                    return Task.CompletedTask;
                },
            };
        });

    // Require auth by default; individual endpoints opt out via [AllowAnonymous].
    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });

    // -----------------------------------------------------------------------
    // CORS for the admin UI (cookies require AllowCredentials + explicit origins)
    // -----------------------------------------------------------------------
    var allowedOrigins = builder.Configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>() ?? new[] { "http://localhost:3000" };

    const string CorsPolicy = "ArchMindCors";
    builder.Services.AddCors(options =>
    {
        options.AddPolicy(CorsPolicy, policy =>
        {
            policy
                .WithOrigins(allowedOrigins)
                .AllowCredentials()
                .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                .AllowAnyHeader();
        });
    });

    // -----------------------------------------------------------------------
    // Infrastructure: GitHub, Anthropic, LLM router, cache
    // -----------------------------------------------------------------------
    builder.Services.AddArchMindInfrastructure();
    builder.Services.AddArchMindWorkers();

    // -----------------------------------------------------------------------
    // MCP-over-SSE (BE-027): in-memory session store + handshake handler.
    // Endpoints registered below as part of the routing block.
    // -----------------------------------------------------------------------
    builder.Services.AddSingleton<IMcpSessionStore, InMemoryMcpSessionStore>();
    builder.Services.AddSingleton<McpHandshakeHandler>();
    builder.Services.AddSingleton<McpResourcesHandler>();
    builder.Services.AddSingleton<McpToolsHandler>();
    builder.Services.AddSingleton<McpPromptsHandler>();

    // BE-031: get_relevant_context tool handler. Scoped because it depends on
    // EF-scoped services (ISkillMatcher, IFileExtractionRepository). Sibling
    // McpToolsHandler should resolve it from httpContext.RequestServices.
    builder.Services.AddScoped<ArchMind.Api.Mcp.Tools.GetRelevantContextHandler>();

    // -----------------------------------------------------------------------
    // ASP.NET Core services
    // -----------------------------------------------------------------------
    builder.Services.AddOpenApi();

    var app = builder.Build();

    // -----------------------------------------------------------------------
    // BE-046: --smoke CLI mode. Run the smoke probe against the resolved
    // service provider, print a table to stdout, and exit. Used for CI and
    // for docker healthcheck escalation. We deliberately skip MigrateAsync
    // here so a broken DB still produces a useful report instead of crashing
    // before the probe can run.
    // -----------------------------------------------------------------------
    if (args.Contains("--smoke"))
    {
        using var smokeCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var exitCode = await SmokeRunner.RunOnceAsync(app.Services, smokeCts.Token);
        Log.CloseAndFlush();
        Environment.Exit(exitCode);
        return;
    }

    // -----------------------------------------------------------------------
    // Apply migrations in non-production. Log any failure but don't crash.
    // -----------------------------------------------------------------------
    if (!app.Environment.IsProduction())
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchMindDbContext>();
        try
        {
            await db.Database.MigrateAsync();
            Log.Information("Database migrations applied successfully.");

            // Seed default admin user if there are no users yet.
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var seederLogger = scope.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(UserSeeder));
            await UserSeeder.SeedAsync(db, hasher, seederLogger);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to apply database migrations on startup.");
        }
    }

    // -----------------------------------------------------------------------
    // BE-044: fire-and-forget graph schema drift check. Compares declared
    // GraphSchema against the AGE catalog (ag_label rows) and emits a
    // warning if anything is missing or extra. 10s timeout so a stuck
    // Postgres can't strand startup. Wrapped in its own scope because
    // IGraphSchemaValidator is registered as scoped.
    // -----------------------------------------------------------------------
    _ = Task.Run(async () =>
    {
        try
        {
            using var driftCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var scope = app.Services.CreateScope();
            var validator = scope.ServiceProvider.GetRequiredService<IGraphSchemaValidator>();
            var report = await validator.CheckLiveSchemaAsync(driftCts.Token);

            if (report.HasDrift)
            {
                Log.Warning(
                    "Graph schema drift detected. Missing node labels: [{MissingNodes}]; " +
                    "extra node labels: [{ExtraNodes}]; missing edge labels: [{MissingEdges}]; " +
                    "extra edge labels: [{ExtraEdges}].",
                    string.Join(", ", report.MissingNodeLabels),
                    string.Join(", ", report.ExtraNodeLabels),
                    string.Join(", ", report.MissingEdgeLabels),
                    string.Join(", ", report.ExtraEdgeLabels));
            }
            else
            {
                Log.Information("Graph schema drift check OK — declared schema matches AGE catalog.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Graph schema drift check failed; continuing without it.");
        }
    });

    // -----------------------------------------------------------------------
    // HTTP pipeline
    // -----------------------------------------------------------------------
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseHttpsRedirection();
    app.UseSerilogRequestLogging();
    app.UseRouting();
    app.UseCors(CorsPolicy);
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<WorkspaceLogContextMiddleware>();

    // Hangfire dashboard now requires an authenticated session.
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new SessionHangfireAuthorizationFilter() }
    });

    // -----------------------------------------------------------------------
    // Endpoints
    // -----------------------------------------------------------------------
    app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "archmind" }))
        .AllowAnonymous();

    app.MapGet("/health/db", async (ArchMindDbContext db) =>
    {
        try
        {
            var canConnect = await db.Database.CanConnectAsync();
            return canConnect
                ? Results.Ok(new { status = "ok" })
                : Results.Json(new { status = "db unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Database connectivity check failed.");
            return Results.Json(new { status = "db unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }).AllowAnonymous();

    // BE-044: tiny LLM reachability probe. Issues a 1-token Haiku completion so
    // the response is end-to-end (auth + network + model) but bills only a few
    // tokens per check. Uptime monitors should poll this no more than once a
    // minute; for higher cadence prefer /health which is free.
    app.MapGet("/health/llm", async (IAnthropicClient anthropic, CancellationToken httpCt) =>
    {
        // Cap server-side at 5s so a slow Anthropic call doesn't hold an HTTP
        // worker indefinitely. The probe itself completes in well under 1s
        // when healthy.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(httpCt);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await anthropic.CompleteTextAsync(
                systemPrompt: "Reply with exactly: ok",
                userPrompt: "ping",
                model: AnthropicModel.Haiku,
                maxTokens: 8,
                ct: cts.Token);
            sw.Stop();

            return Results.Ok(new
            {
                status = "ok",
                model = result.ModelId,
                input_tokens = result.InputTokens,
                output_tokens = result.OutputTokens,
                latency_ms = (long)sw.ElapsedMilliseconds,
            });
        }
        catch (OperationCanceledException)
        {
            return Results.Json(
                new { status = "llm timeout" },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LLM health probe failed.");
            return Results.Json(
                new { status = "llm unreachable", reason = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }).AllowAnonymous();

    app.MapAuthEndpoints();
    app.MapWorkspaceEndpoints();
    app.MapRepoEndpoints();
    app.MapGraphEndpoints();
    app.MapSkillEndpoints();
    app.MapClarificationEndpoints();
    app.MapMcpEndpoints();
    app.MapReportEndpoints();
    app.MapSmokeEndpoints();
    app.MapJobsEndpoints();

    // -----------------------------------------------------------------------
    // Enqueue a one-time job to prove Hangfire is operating.
    // -----------------------------------------------------------------------
    try
    {
        BackgroundJob.Enqueue(() => HangfireTestJob.Run());
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to enqueue Hangfire startup test job.");
    }

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
static string ResolveWritableLogDirectory(string preferred)
{
    try
    {
        Directory.CreateDirectory(preferred);
        var probe = Path.Combine(preferred, ".write_probe");
        File.WriteAllText(probe, string.Empty);
        File.Delete(probe);
        return preferred;
    }
    catch
    {
        var fallback = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}

/// <summary>
/// Tiny static job used to prove Hangfire is wired and a worker is processing jobs.
/// </summary>
public static class HangfireTestJob
{
    public static void Run()
    {
        Log.Information("Hangfire test job ran successfully");
    }
}
