using System.Diagnostics;
using System.Net.Http;
using System.Text;
using ArchMind.Core.Abstractions;
using ArchMind.Infrastructure.Anthropic;
using ArchMind.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArchMind.Api.Smoke;

/// <summary>
/// BE-046: runtime smoke probe. The MVP doesn't ship unit tests; instead we
/// exercise the critical-path dependencies (DB, AGE, Hangfire, Anthropic, disk)
/// and report a structured pass/fail/skipped result. Used by both:
///   * the <c>GET /smoke</c> endpoint (returns JSON, 200 ok|degraded / 503 fail)
///   * the <c>--smoke</c> CLI flag (prints a table and exits 0/1)
///
/// Required checks (failing → overall <c>fail</c>): DB, AGE, Hangfire.
/// Warning checks (failing → overall <c>degraded</c>): Anthropic, disk.
/// </summary>
public static class SmokeRunner
{
    private const int CheckTimeoutSeconds = 5;
    private const int AnthropicTimeoutSeconds = 3;

    // ---------------------------------------------------------------------
    // DTOs (also used by the JSON endpoint)
    // ---------------------------------------------------------------------
    public sealed record SmokeCheckResult(
        string Name,
        string Status,        // "pass" | "fail" | "skipped"
        long DurationMs,
        bool Required,
        string? Error);

    public sealed record SmokeReport(
        string Status,        // "ok" | "degraded" | "fail"
        IReadOnlyList<SmokeCheckResult> Checks);

    // ---------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------

    /// <summary>
    /// Run all smoke checks against the supplied service provider. Each check
    /// has its own short timeout so a hung dependency can't stall the probe.
    /// </summary>
    public static async Task<SmokeReport> RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var results = new List<SmokeCheckResult>(capacity: 5);

        results.Add(await RunCheckAsync("db", required: true,
            (s, c) => DbCheckAsync(s, c), sp, ct));

        results.Add(await RunCheckAsync("age", required: true,
            (s, c) => AgeCheckAsync(s, c), sp, ct));

        results.Add(await RunCheckAsync("hangfire", required: true,
            (s, c) => HangfireCheckAsync(s, c), sp, ct));

        results.Add(await RunCheckAsync("anthropic", required: false,
            (s, c) => AnthropicCheckAsync(s, c), sp, ct));

        results.Add(await RunCheckAsync("disk", required: false,
            (s, c) => DiskCheckAsync(s, c), sp, ct));

        var status = ComputeOverallStatus(results);
        return new SmokeReport(status, results);
    }

    /// <summary>
    /// CLI entry point used by the <c>--smoke</c> flag in Program.cs. Prints a
    /// table to stdout and returns the appropriate exit code (0 if ok|degraded,
    /// 1 if any required check failed).
    /// </summary>
    public static async Task<int> RunOnceAsync(IServiceProvider sp, CancellationToken ct)
    {
        var report = await RunAsync(sp, ct);
        PrintTable(report, Console.Out);
        return report.Status == "fail" ? 1 : 0;
    }

    // ---------------------------------------------------------------------
    // Individual checks
    // ---------------------------------------------------------------------
    private static async Task<string?> DbCheckAsync(IServiceProvider sp, CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchMindDbContext>();
        var ok = await db.Database.CanConnectAsync(ct).ConfigureAwait(false);
        return ok ? null : "CanConnectAsync returned false";
    }

    private static async Task<string?> AgeCheckAsync(IServiceProvider sp, CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var graph = scope.ServiceProvider.GetRequiredService<IGraphReader>();
        var available = await graph.IsAvailableAsync(ct).ConfigureAwait(false);
        return available ? null : "AGE no-op cypher failed";
    }

    private static Task<string?> HangfireCheckAsync(IServiceProvider sp, CancellationToken ct)
    {
        // Two-tier probe — either is sufficient.
        //   1. Servers() registered in the monitoring API: cheap, requires
        //      that a Hangfire server is running in the same process.
        //   2. Fallback: enqueue a noop job and confirm a job id comes back.
        //      Proves the Postgres storage is reachable + writable even when
        //      no server is registered (e.g. the CLI smoke flag path).
        try
        {
            var monitor = JobStorage.Current.GetMonitoringApi();
            var servers = monitor.Servers();
            if (servers is not null && servers.Count > 0)
            {
                return Task.FromResult<string?>(null);
            }

            var jobId = BackgroundJob.Enqueue(() => SmokeNoop.Run());
            return Task.FromResult<string?>(
                string.IsNullOrEmpty(jobId) ? "Hangfire enqueue returned no id" : null);
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>(ex.Message);
        }
    }

    private static async Task<string?> AnthropicCheckAsync(IServiceProvider sp, CancellationToken ct)
    {
        var options = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            // Caller treats SKIP_SENTINEL as a "skipped" outcome.
            return SkipSentinel;
        }

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(AnthropicTimeoutSeconds),
        };
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
        req.Headers.TryAddWithoutValidation("x-api-key", options.ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        // 2xx is the happy path. 401/403 means the host is reachable but the
        // key is bad — that's still "Anthropic API is up", which is what the
        // smoke probe is meant to verify.
        var code = (int)resp.StatusCode;
        if (code >= 200 && code < 500)
        {
            return null;
        }

        return $"HTTP {code}";
    }

    private static async Task<string?> DiskCheckAsync(IServiceProvider sp, CancellationToken ct)
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var logDir = config["Logging:FilePath"] ?? "/var/log/archmind/";

        var probeRoot = TryProbeDirectory(logDir)
            ?? TryProbeDirectory(Path.Combine(AppContext.BaseDirectory, "logs"))
            ?? TryProbeDirectory(Path.GetTempPath());

        if (probeRoot is null)
        {
            return "no writable directory found (logs, fallback, temp)";
        }

        var path = Path.Combine(probeRoot, $".smoke_probe_{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(path, "smoke", ct).ConfigureAwait(false);
            var read = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            if (read != "smoke") return "read-back mismatch";
            return null;
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* swallow */ }
        }
    }

    private static string? TryProbeDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------
    private const string SkipSentinel = "__skip__";

    private static async Task<SmokeCheckResult> RunCheckAsync(
        string name,
        bool required,
        Func<IServiceProvider, CancellationToken, Task<string?>> body,
        IServiceProvider sp,
        CancellationToken outerCt)
    {
        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(CheckTimeoutSeconds));

        try
        {
            var error = await body(sp, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();

            if (error == SkipSentinel)
            {
                return new SmokeCheckResult(name, "skipped", sw.ElapsedMilliseconds, required, null);
            }
            if (error is null)
            {
                return new SmokeCheckResult(name, "pass", sw.ElapsedMilliseconds, required, null);
            }
            return new SmokeCheckResult(name, "fail", sw.ElapsedMilliseconds, required, error);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !outerCt.IsCancellationRequested)
        {
            sw.Stop();
            return new SmokeCheckResult(name, "fail", sw.ElapsedMilliseconds, required,
                $"timed out after {CheckTimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SmokeCheckResult(name, "fail", sw.ElapsedMilliseconds, required, ex.Message);
        }
    }

    private static string ComputeOverallStatus(IReadOnlyList<SmokeCheckResult> results)
    {
        var anyRequiredFail = results.Any(r => r.Required && r.Status == "fail");
        if (anyRequiredFail) return "fail";

        var anyWarnFail = results.Any(r => !r.Required && r.Status == "fail");
        return anyWarnFail ? "degraded" : "ok";
    }

    private static void PrintTable(SmokeReport report, TextWriter writer)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ArchMind smoke probe — overall: {report.Status.ToUpperInvariant()}");
        sb.AppendLine(new string('-', 70));
        sb.AppendLine($"{ "CHECK",-12} { "STATUS",-9} { "REQ",-5} { "MS",-7}  ERROR");
        sb.AppendLine(new string('-', 70));
        foreach (var c in report.Checks)
        {
            var req = c.Required ? "yes" : "no";
            var err = c.Error ?? string.Empty;
            sb.AppendLine($"{c.Name,-12} {c.Status,-9} {req,-5} {c.DurationMs,-7}  {err}");
        }
        sb.AppendLine(new string('-', 70));
        writer.WriteLine(sb.ToString());
    }
}

/// <summary>
/// Static no-op for Hangfire's <c>BackgroundJob.Enqueue</c>. Public so
/// Hangfire's serializer can reach it.
/// </summary>
public static class SmokeNoop
{
    public static void Run() { /* no-op */ }
}
