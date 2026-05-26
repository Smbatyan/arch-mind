using ArchMind.Api.Auth;
using ArchMind.Api.Endpoints;
using ArchMind.Api.Mcp;
using ArchMind.Api.Middleware;
using ArchMind.Core.Abstractions;
using ArchMind.Infrastructure;
using ArchMind.Infrastructure.Auth;
using ArchMind.Infrastructure.Data;
using ArchMind.Workers;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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
    builder.Services.AddHangfireServer();

    // -----------------------------------------------------------------------
    // Auth: cookie-based session auth (no Identity, no JWT)
    // -----------------------------------------------------------------------
    builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();

    var requireHttps = builder.Configuration.GetValue<bool?>("Auth:RequireHttps")
        ?? !builder.Environment.IsDevelopment();

    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "archmind.sid";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = requireHttps
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;

            // API: return 401/403 status codes instead of redirecting to login/access-denied pages.
            options.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
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

    app.MapAuthEndpoints();
    app.MapWorkspaceEndpoints();
    app.MapRepoEndpoints();
    app.MapGraphEndpoints();
    app.MapSkillEndpoints();
    app.MapClarificationEndpoints();
    app.MapMcpEndpoints();

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
