// BE-045 (Sprint 6): end-to-end validation harness.
//
// Drives the live ArchMind HTTP API against the public blot-stars Go repo
// and reports pass/fail per checkpoint. Intentionally NOT a test framework —
// no xUnit, no Microsoft.Testing.Platform — just a console app whose exit
// code reflects whether every required checkpoint passed.
//
// Exit codes:
//   0  -> all required checkpoints passed (skips are not failures)
//   1  -> one or more required checkpoints failed (or fail-fast aborted)
//
// Run from backend/:
//   dotnet run --project ArchMind.Validation/ArchMind.Validation.csproj -- \
//       --api-url http://localhost:5000 \
//       --admin-email admin@archmind.dev \
//       --admin-password changeme
//
// See scripts/validate-e2e.sh for a wrapper that brings docker compose up
// before running.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ArchMind.Validation;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<int> Main(string[] args)
    {
        var opts = Options.Parse(args);
        Console.WriteLine($"ArchMind validation harness");
        Console.WriteLine($"  API: {opts.ApiUrl}");
        Console.WriteLine($"  Admin: {opts.AdminEmail}");
        Console.WriteLine($"  Test repo: {opts.TestRepo}");
        Console.WriteLine($"  Workspace slug: {opts.WorkspaceSlug}");
        Console.WriteLine($"  Timeout: {opts.TimeoutMinutes}m");
        Console.WriteLine();

        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            // Dev backend runs on http with self-signed certs sometimes; tolerate both.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AllowAutoRedirect = false,
        };
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(opts.ApiUrl),
            Timeout = TimeSpan.FromMinutes(2),
        };

        var ctx = new ValidationContext(opts, http);

        // Each step appends to ctx.Results. Steps that mark RequiredAndFailed=true
        // abort the remaining steps (fail-fast) — used for health + auth.
        await Step("Health probes", ctx, RunHealthProbesAsync, failFast: true);
        if (!ctx.Aborted) await Step("Auth", ctx, RunAuthAsync, failFast: true);
        if (!ctx.Aborted) await Step("Workspace setup", ctx, RunWorkspaceAsync, failFast: true);
        if (!ctx.Aborted) await Step("Repo registration", ctx, RunRepoRegistrationAsync, failFast: true);
        if (!ctx.Aborted) await Step("Initial scan", ctx, RunScanWaitAsync, failFast: false);
        if (!ctx.Aborted) await Step("Graph populated", ctx, RunGraphAsync, failFast: false);
        if (!ctx.Aborted) await Step("File extractions", ctx, RunExtractionsAsync, failFast: false);
        if (!ctx.Aborted) await Step("MCP handshake", ctx, RunMcpAsync, failFast: false);
        if (!ctx.Aborted) await Step("Clarifications", ctx, RunClarificationsAsync, failFast: false);
        if (!ctx.Aborted) await Step("Cleanup", ctx, RunCleanupAsync, failFast: false);

        return PrintSummary(ctx);
    }

    // -----------------------------------------------------------------------
    // Step harness
    // -----------------------------------------------------------------------
    private static async Task Step(
        string name,
        ValidationContext ctx,
        Func<ValidationContext, Task> body,
        bool failFast)
    {
        Console.WriteLine($"=== {name} ===");
        var startCount = ctx.Results.Count;
        try
        {
            await body(ctx);
        }
        catch (Exception ex)
        {
            ctx.Fail(name, $"unhandled {ex.GetType().Name}: {Truncate(ex.Message, 200)}");
        }

        if (failFast)
        {
            for (int i = startCount; i < ctx.Results.Count; i++)
            {
                if (ctx.Results[i].Outcome == CheckOutcome.Fail)
                {
                    ctx.Aborted = true;
                    Console.WriteLine("  (fail-fast: aborting remaining steps)");
                    return;
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Step 1: Health probes
    // -----------------------------------------------------------------------
    private static async Task RunHealthProbesAsync(ValidationContext ctx)
    {
        await ProbeAsync(ctx, "GET /health", "/health", body =>
        {
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            return status == "ok" ? null : $"status was '{status}', expected 'ok'";
        });

        await ProbeAsync(ctx, "GET /health/db", "/health/db", _ => null);

        await ProbeAsync(ctx, "GET /smoke", "/smoke", body =>
        {
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            // Anthropic check skipping is fine in CI — degraded is acceptable here.
            return status is "ok" or "degraded" ? null : $"smoke status was '{status}'";
        });
    }

    private static async Task ProbeAsync(
        ValidationContext ctx,
        string label,
        string path,
        Func<string, string?> validator)
    {
        try
        {
            using var resp = await ctx.Http.GetAsync(path);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Fail(label, $"http {(int)resp.StatusCode}: {Truncate(body, 200)}");
                return;
            }
            var err = validator(body);
            if (err is null) ctx.Pass(label);
            else ctx.Fail(label, err);
        }
        catch (Exception ex)
        {
            ctx.Fail(label, $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}");
        }
    }

    // -----------------------------------------------------------------------
    // Step 2: Auth — cookie-based session login at /api/auth/login.
    // -----------------------------------------------------------------------
    private static async Task RunAuthAsync(ValidationContext ctx)
    {
        try
        {
            var loginBody = new { Email = ctx.Options.AdminEmail, Password = ctx.Options.AdminPassword };
            using var resp = await ctx.Http.PostAsJsonAsync("/api/auth/login", loginBody);
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Fail("POST /api/auth/login", $"http {(int)resp.StatusCode}: {Truncate(raw, 200)}");
                return;
            }
            ctx.Pass("POST /api/auth/login");
        }
        catch (Exception ex)
        {
            ctx.Fail("POST /api/auth/login", $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}");
            return;
        }

        try
        {
            using var resp = await ctx.Http.GetAsync("/api/auth/me");
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Fail("GET /api/auth/me", $"http {(int)resp.StatusCode}: {Truncate(raw, 200)}");
                return;
            }
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("user", out var userEl)
                || !userEl.TryGetProperty("email", out var emailEl))
            {
                ctx.Fail("GET /api/auth/me", "missing user.email in response");
                return;
            }
            ctx.Pass($"GET /api/auth/me (user={emailEl.GetString()})");
        }
        catch (Exception ex)
        {
            ctx.Fail("GET /api/auth/me", $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}");
        }
    }

    // -----------------------------------------------------------------------
    // Step 3: Workspace setup. POST /api/workspaces/ expects {Name, Slug}
    // and returns 200 OK (not 201) per WorkspaceEndpoints.CreateAsync.
    // -----------------------------------------------------------------------
    private static async Task RunWorkspaceAsync(ValidationContext ctx)
    {
        try
        {
            var body = new { Name = "Validation", Slug = ctx.Options.WorkspaceSlug };
            using var resp = await ctx.Http.PostAsJsonAsync("/api/workspaces/", body);
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Fail("POST /api/workspaces/", $"http {(int)resp.StatusCode}: {Truncate(raw, 200)}");
                return;
            }
            using var doc = JsonDocument.Parse(raw);
            var id = doc.RootElement.GetProperty("id").GetGuid();
            var slug = doc.RootElement.GetProperty("slug").GetString()!;
            ctx.WorkspaceId = id;
            ctx.WorkspaceSlug = slug;
            ctx.Pass($"POST /api/workspaces/ (id={id}, slug={slug})");
        }
        catch (Exception ex)
        {
            ctx.Fail("POST /api/workspaces/", $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}");
        }
    }

    // -----------------------------------------------------------------------
    // Step 4: Repo registration. POST /api/workspaces/{slug}/repos/ expects
    // {GitHubUrl, DefaultBranch, PatToken}. PatToken is required by the
    // validation regex on the API — for a public repo we pass a placeholder
    // (env VALIDATION_GITHUB_PAT overrides). Endpoint returns 201 Created.
    // The initial scan is kicked automatically inside CreateAsync.
    // -----------------------------------------------------------------------
    private static async Task RunRepoRegistrationAsync(ValidationContext ctx)
    {
        try
        {
            var pat = Environment.GetEnvironmentVariable("VALIDATION_GITHUB_PAT");
            if (string.IsNullOrWhiteSpace(pat))
            {
                // Placeholder is fine for public repos — only the format is
                // validated server-side, not the credential itself.
                pat = "validation-placeholder-token";
            }

            var body = new
            {
                GitHubUrl = ctx.Options.TestRepo,
                DefaultBranch = "main",
                PatToken = pat,
            };
            using var resp = await ctx.Http.PostAsJsonAsync(
                $"/api/workspaces/{ctx.WorkspaceSlug}/repos/", body);
            var raw = await resp.Content.ReadAsStringAsync();
            if (resp.StatusCode != HttpStatusCode.Created && resp.StatusCode != HttpStatusCode.OK)
            {
                ctx.Fail("POST /api/workspaces/{slug}/repos/", $"http {(int)resp.StatusCode}: {Truncate(raw, 200)}");
                return;
            }
            using var doc = JsonDocument.Parse(raw);
            var id = doc.RootElement.GetProperty("id").GetGuid();
            ctx.RepoId = id;
            ctx.Pass($"POST /api/workspaces/{{slug}}/repos/ (id={id}, scan auto-enqueued)");
        }
        catch (Exception ex)
        {
            ctx.Fail("POST /api/workspaces/{slug}/repos/", $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}");
        }
    }

    // -----------------------------------------------------------------------
    // Step 5: poll the repo until status leaves {pending, scanning}.
    // Terminal states from the worker: "scanned" (success) or "failed".
    // -----------------------------------------------------------------------
    private static async Task RunScanWaitAsync(ValidationContext ctx)
    {
        if (ctx.RepoId is null)
        {
            ctx.Skip("scan-wait", "repo registration didn't capture an id");
            return;
        }

        var deadline = DateTime.UtcNow.AddMinutes(ctx.Options.TimeoutMinutes);
        var label = "scan reaches terminal state";
        var lastStatus = "";

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var resp = await ctx.Http.GetAsync(
                    $"/api/workspaces/{ctx.WorkspaceSlug}/repos/{ctx.RepoId}");
                var raw = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    ctx.Fail(label, $"http {(int)resp.StatusCode}: {Truncate(raw, 200)}");
                    return;
                }
                using var doc = JsonDocument.Parse(raw);
                lastStatus = doc.RootElement.GetProperty("status").GetString() ?? "";
                if (lastStatus != ctx.LastSeenScanStatus)
                {
                    Console.WriteLine($"  status: {lastStatus}");
                    ctx.LastSeenScanStatus = lastStatus;
                }

                if (lastStatus == "scanned" || lastStatus == "active")
                {
                    ctx.Pass($"{label} (status={lastStatus})");
                    return;
                }
                if (lastStatus == "failed")
                {
                    var err = doc.RootElement.TryGetProperty("errorMessage", out var em)
                        ? em.GetString()
                        : null;
                    ctx.Fail(label, $"scan failed: {Truncate(err ?? "(no message)", 200)}");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  poll exception (continuing): {ex.GetType().Name}: {Truncate(ex.Message, 120)}");
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        ctx.Fail(label, $"timeout after {ctx.Options.TimeoutMinutes}m (last status={lastStatus})");
    }

    // -----------------------------------------------------------------------
    // Step 6: graph populated. The repo's actual graph endpoint is
    // /api/workspaces/{slug}/graph/labels (count per vertex label) — there
    // is no /overview path. /schema-report carries the live counts + drift.
    // -----------------------------------------------------------------------
    private static async Task RunGraphAsync(ValidationContext ctx)
    {
        try
        {
            using var resp = await ctx.Http.GetAsync(
                $"/api/workspaces/{ctx.WorkspaceSlug}/graph/labels");
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Fail("GET /graph/labels", $"http {(int)resp.StatusCode}: {Truncate(raw, 200)}");
            }
            else
            {
                using var doc = JsonDocument.Parse(raw);
                int totalVertices = 0;
                int serviceCount = 0;
                int endpointCount = 0;
                if (doc.RootElement.TryGetProperty("vertices", out var vs))
                {
                    foreach (var v in vs.EnumerateArray())
                    {
                        var label = v.GetProperty("label").GetString();
                        var count = v.GetProperty("count").GetInt32();
                        totalVertices += count;
                        if (label == "Service") serviceCount = count;
                        else if (label == "Endpoint") endpointCount = count;
                    }
                }
                // Lenient: any non-empty graph passes. Blot-stars is a small Go
                // service so we expect at least a Service node but don't make
                // Endpoint hard-required (depends on whether HTTP routes were
                // detectable from the LLM extraction).
                if (totalVertices == 0)
                {
                    ctx.Fail("GET /graph/labels", "graph is empty (no vertices)");
                }
                else if (serviceCount == 0)
                {
                    ctx.Fail("GET /graph/labels",
                        $"no Service vertex (total={totalVertices})");
                }
                else
                {
                    ctx.Pass($"GET /graph/labels (services={serviceCount}, endpoints={endpointCount}, total={totalVertices})");
                }
            }
        }
        catch (Exception ex)
        {
            ctx.Fail("GET /graph/labels", $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}");
        }

        // Schema report: log drift but don't fail.
        try
        {
            using var resp = await ctx.Http.GetAsync(
                $"/api/workspaces/{ctx.WorkspaceSlug}/graph/schema-report");
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Skip("GET /graph/schema-report", $"http {(int)resp.StatusCode}");
            }
            else
            {
                using var doc = JsonDocument.Parse(raw);
                var hasDrift = doc.RootElement.GetProperty("drift").GetProperty("hasDrift").GetBoolean();
                ctx.Pass($"GET /graph/schema-report (drift={hasDrift})");
                if (hasDrift)
                {
                    Console.WriteLine($"  (warn) schema drift detected — informational only");
                }
            }
        }
        catch (Exception ex)
        {
            ctx.Skip("GET /graph/schema-report", $"{ex.GetType().Name}: {Truncate(ex.Message, 120)}");
        }
    }

    // -----------------------------------------------------------------------
    // Step 7: file extractions via the report summary endpoint (BE-043).
    // -----------------------------------------------------------------------
    private static async Task RunExtractionsAsync(ValidationContext ctx)
    {
        try
        {
            using var resp = await ctx.Http.GetAsync(
                $"/api/workspaces/{ctx.WorkspaceSlug}/report/summary");
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Fail("GET /report/summary", $"http {(int)resp.StatusCode}: {Truncate(raw, 200)}");
                return;
            }
            using var doc = JsonDocument.Parse(raw);
            var total = doc.RootElement.GetProperty("extractions").GetProperty("totalFiles").GetInt32();
            if (total < 1)
            {
                ctx.Fail("GET /report/summary", $"extractions.totalFiles={total}, expected >= 1");
                return;
            }
            ctx.Pass($"GET /report/summary (extractions.totalFiles={total})");
        }
        catch (Exception ex)
        {
            ctx.Fail("GET /report/summary", $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}");
        }
    }

    // -----------------------------------------------------------------------
    // Step 8: MCP handshake + tools. Bearer token from the api-keys endpoint.
    // -----------------------------------------------------------------------
    private static async Task RunMcpAsync(ValidationContext ctx)
    {
        // Mint a workspace API key. Response shape: {id, name, plaintext, prefix, createdAt}.
        string? plaintext;
        try
        {
            var body = new { Name = "validation" };
            using var resp = await ctx.Http.PostAsJsonAsync(
                $"/api/workspaces/{ctx.WorkspaceSlug}/api-keys", body);
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Fail("POST /api/workspaces/{slug}/api-keys", $"http {(int)resp.StatusCode}: {Truncate(raw, 200)}");
                return;
            }
            using var doc = JsonDocument.Parse(raw);
            plaintext = doc.RootElement.GetProperty("plaintext").GetString();
            if (string.IsNullOrEmpty(plaintext))
            {
                ctx.Fail("POST /api/workspaces/{slug}/api-keys", "missing plaintext");
                return;
            }
            ctx.Pass("POST /api/workspaces/{slug}/api-keys");
        }
        catch (Exception ex)
        {
            ctx.Fail("POST /api/workspaces/{slug}/api-keys", $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}");
            return;
        }

        // Use a *non-cookie* http client for MCP — the bearer-token path
        // and the cookie path are distinct on the server.
        using var mcp = new HttpClient
        {
            BaseAddress = new Uri(ctx.Options.ApiUrl),
            Timeout = TimeSpan.FromMinutes(2),
        };
        mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);

        // initialize
        string? sessionId = null;
        try
        {
            var initParams = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "archmind-validation", version = "1.0" },
            };
            var (resp, raw) = await McpCallAsync(mcp, ctx.WorkspaceSlug!, "initialize", initParams, id: 1);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Fail("MCP initialize", $"http {(int)resp.StatusCode}: {Truncate(raw, 200)}");
                return;
            }
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("protocolVersion", out var pv))
            {
                ctx.Fail("MCP initialize", $"missing result.protocolVersion: {Truncate(raw, 200)}");
                return;
            }
            sessionId = resp.Headers.TryGetValues("Mcp-Session-Id", out var sids)
                ? sids.FirstOrDefault()
                : null;
            if (sessionId is not null) mcp.DefaultRequestHeaders.Add("Mcp-Session-Id", sessionId);
            ctx.Pass($"MCP initialize (protocolVersion={pv.GetString()})");
        }
        catch (Exception ex)
        {
            ctx.Fail("MCP initialize", $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}");
            return;
        }

        // tools/list — expect get_relevant_context in the list.
        try
        {
            var (resp, raw) = await McpCallAsync(mcp, ctx.WorkspaceSlug!, "tools/list", new { }, id: 2);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Fail("MCP tools/list", $"http {(int)resp.StatusCode}: {Truncate(raw, 200)}");
            }
            else
            {
                using var doc = JsonDocument.Parse(raw);
                bool found = false;
                if (doc.RootElement.TryGetProperty("result", out var result)
                    && result.TryGetProperty("tools", out var tools))
                {
                    foreach (var t in tools.EnumerateArray())
                    {
                        if (t.TryGetProperty("name", out var n) && n.GetString() == "get_relevant_context")
                        {
                            found = true;
                            break;
                        }
                    }
                }
                if (found) ctx.Pass("MCP tools/list (get_relevant_context present)");
                else ctx.Fail("MCP tools/list", $"get_relevant_context missing: {Truncate(raw, 200)}");
            }
        }
        catch (Exception ex)
        {
            ctx.Fail("MCP tools/list", $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}");
        }

        // tools/call search_concepts query=service
        try
        {
            var callParams = new
            {
                name = "search_concepts",
                arguments = new { query = "service", limit = 5 },
            };
            var (resp, raw) = await McpCallAsync(mcp, ctx.WorkspaceSlug!, "tools/call", callParams, id: 3);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Fail("MCP tools/call search_concepts", $"http {(int)resp.StatusCode}: {Truncate(raw, 200)}");
            }
            else
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    ctx.Fail("MCP tools/call search_concepts",
                        $"json-rpc error: {Truncate(err.ToString(), 200)}");
                }
                else
                {
                    ctx.Pass("MCP tools/call search_concepts");
                }
            }
        }
        catch (Exception ex)
        {
            ctx.Fail("MCP tools/call search_concepts", $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}");
        }

        // tools/call get_relevant_context task="Describe the architecture"
        try
        {
            var callParams = new
            {
                name = "get_relevant_context",
                arguments = new { task = "Describe the architecture" },
            };
            var (resp, raw) = await McpCallAsync(mcp, ctx.WorkspaceSlug!, "tools/call", callParams, id: 4);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Fail("MCP tools/call get_relevant_context", $"http {(int)resp.StatusCode}: {Truncate(raw, 200)}");
                return;
            }
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                ctx.Fail("MCP tools/call get_relevant_context", $"json-rpc error: {Truncate(err.ToString(), 200)}");
                return;
            }
            // The MCP tool result wraps payload in result.content[0].text as a
            // JSON string; we just check for the expected key names anywhere
            // in the raw response (cheap + tolerates shape evolution).
            var lower = raw.ToLowerInvariant();
            var hasSkills = lower.Contains("\"skills\"");
            var hasGraph = lower.Contains("\"graph\"");
            var hasFiles = lower.Contains("\"files\"");
            if (hasSkills && hasGraph && hasFiles)
            {
                ctx.Pass("MCP tools/call get_relevant_context (skills+graph+files)");
            }
            else
            {
                ctx.Fail("MCP tools/call get_relevant_context",
                    $"missing keys (skills={hasSkills}, graph={hasGraph}, files={hasFiles})");
            }
        }
        catch (Exception ex)
        {
            ctx.Fail("MCP tools/call get_relevant_context", $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}");
        }
    }

    private static async Task<(HttpResponseMessage Resp, string Raw)> McpCallAsync(
        HttpClient mcp, string workspaceSlug, string method, object @params, int id)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params,
        };
        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        var resp = await mcp.PostAsync($"/mcp/{workspaceSlug}", content);
        var raw = await resp.Content.ReadAsStringAsync();
        return (resp, raw);
    }

    // -----------------------------------------------------------------------
    // Step 9: Clarifications. Empty list is fine.
    // -----------------------------------------------------------------------
    private static async Task RunClarificationsAsync(ValidationContext ctx)
    {
        Guid? firstOpenId = null;
        try
        {
            using var resp = await ctx.Http.GetAsync(
                $"/api/workspaces/{ctx.WorkspaceSlug}/clarifications/?status=open");
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Fail("GET /clarifications", $"http {(int)resp.StatusCode}: {Truncate(raw, 200)}");
                return;
            }
            using var doc = JsonDocument.Parse(raw);
            var count = doc.RootElement.GetArrayLength();
            ctx.Pass($"GET /clarifications?status=open (count={count})");
            if (count > 0)
            {
                firstOpenId = doc.RootElement[0].GetProperty("id").GetGuid();
            }
        }
        catch (Exception ex)
        {
            ctx.Fail("GET /clarifications", $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}");
            return;
        }

        if (firstOpenId is null)
        {
            ctx.Skip("POST /clarifications/{id}/answer", "no open clarifications");
            return;
        }

        try
        {
            var body = new { Answer = "validation-test", UserId = (string?)null };
            using var resp = await ctx.Http.PostAsJsonAsync(
                $"/api/workspaces/{ctx.WorkspaceSlug}/clarifications/{firstOpenId}/answer", body);
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Fail("POST /clarifications/{id}/answer", $"http {(int)resp.StatusCode}: {Truncate(raw, 200)}");
            }
            else
            {
                ctx.Pass($"POST /clarifications/{firstOpenId}/answer");
            }
        }
        catch (Exception ex)
        {
            ctx.Fail("POST /clarifications/{id}/answer", $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}");
        }
    }

    // -----------------------------------------------------------------------
    // Step 10: cleanup. Workspace DELETE doesn't exist (verified against
    // WorkspaceEndpoints.cs). We do delete the repo we registered, which
    // unwinds its polling job; the workspace itself is left in place.
    // -----------------------------------------------------------------------
    private static async Task RunCleanupAsync(ValidationContext ctx)
    {
        if (ctx.RepoId is null)
        {
            ctx.Skip("DELETE /repos/{id}", "no repo id captured");
        }
        else
        {
            try
            {
                using var resp = await ctx.Http.DeleteAsync(
                    $"/api/workspaces/{ctx.WorkspaceSlug}/repos/{ctx.RepoId}");
                if (resp.StatusCode == HttpStatusCode.NoContent)
                {
                    ctx.Pass($"DELETE /repos/{ctx.RepoId}");
                }
                else
                {
                    var raw = await resp.Content.ReadAsStringAsync();
                    ctx.Skip("DELETE /repos/{id}", $"http {(int)resp.StatusCode}: {Truncate(raw, 120)}");
                }
            }
            catch (Exception ex)
            {
                ctx.Skip("DELETE /repos/{id}", $"{ex.GetType().Name}: {Truncate(ex.Message, 120)}");
            }
        }

        // No workspace DELETE endpoint exists in the MVP — record as skipped.
        ctx.Skip("DELETE /api/workspaces/{slug}", "endpoint not implemented in MVP");
    }

    // -----------------------------------------------------------------------
    // Summary
    // -----------------------------------------------------------------------
    private static int PrintSummary(ValidationContext ctx)
    {
        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        int pass = 0, fail = 0, skip = 0;
        foreach (var r in ctx.Results)
        {
            switch (r.Outcome)
            {
                case CheckOutcome.Pass: pass++; break;
                case CheckOutcome.Fail: fail++; break;
                case CheckOutcome.Skip: skip++; break;
            }
        }

        Console.WriteLine($"  PASS: {pass}");
        Console.WriteLine($"  FAIL: {fail}");
        Console.WriteLine($"  SKIP: {skip}");
        Console.WriteLine($"  total: {ctx.Results.Count}");

        if (fail == 0)
        {
            Console.WriteLine("Result: OK");
            return 0;
        }
        else
        {
            Console.WriteLine("Result: FAILED");
            return 1;
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}

// ---------------------------------------------------------------------------
// Support types
// ---------------------------------------------------------------------------
internal enum CheckOutcome { Pass, Fail, Skip }

internal sealed record CheckResult(string Name, CheckOutcome Outcome, string? Detail);

internal sealed class ValidationContext
{
    public ValidationContext(Options options, HttpClient http)
    {
        Options = options;
        Http = http;
        WorkspaceSlug = options.WorkspaceSlug;
    }

    public Options Options { get; }
    public HttpClient Http { get; }
    public List<CheckResult> Results { get; } = new();
    public bool Aborted { get; set; }

    public Guid? WorkspaceId { get; set; }
    public string WorkspaceSlug { get; set; }
    public Guid? RepoId { get; set; }
    public string LastSeenScanStatus { get; set; } = "";

    public void Pass(string name)
    {
        Results.Add(new CheckResult(name, CheckOutcome.Pass, null));
        Console.WriteLine($"  [PASS] {name}");
    }

    public void Fail(string name, string detail)
    {
        Results.Add(new CheckResult(name, CheckOutcome.Fail, detail));
        Console.WriteLine($"  [FAIL] {name}: {detail}");
    }

    public void Skip(string name, string detail)
    {
        Results.Add(new CheckResult(name, CheckOutcome.Skip, detail));
        Console.WriteLine($"  [SKIP] {name}: {detail}");
    }
}

internal sealed class Options
{
    public required string ApiUrl { get; init; }
    public required string AdminEmail { get; init; }
    public required string AdminPassword { get; init; }
    public required string TestRepo { get; init; }
    public required int TimeoutMinutes { get; init; }
    public required string WorkspaceSlug { get; init; }

    public static Options Parse(string[] args)
    {
        string apiUrl = "http://localhost:5000";
        // The dev seeder defaults are admin@archmind.dev / changeme. Spec
        // mentions admin@archmind.local but the seeder uses .dev — match the
        // seeder so the harness works out of the box, override via flag if needed.
        string adminEmail = "admin@archmind.dev";
        string adminPassword = Environment.GetEnvironmentVariable("AdminSeed__Password")
            ?? "changeme";
        string testRepo = "https://github.com/Smbatyan/blot-stars";
        int timeoutMinutes = 30;
        string? workspaceSlug = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (a)
            {
                case "--api-url" when next is not null: apiUrl = next; i++; break;
                case "--admin-email" when next is not null: adminEmail = next; i++; break;
                case "--admin-password" when next is not null: adminPassword = next; i++; break;
                case "--test-repo" when next is not null: testRepo = next; i++; break;
                case "--timeout-minutes" when next is not null:
                    timeoutMinutes = int.Parse(next, CultureInfo.InvariantCulture); i++; break;
                case "--workspace-slug" when next is not null: workspaceSlug = next; i++; break;
                case "-h":
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        workspaceSlug ??= "validation-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        return new Options
        {
            ApiUrl = apiUrl.TrimEnd('/'),
            AdminEmail = adminEmail,
            AdminPassword = adminPassword,
            TestRepo = testRepo,
            TimeoutMinutes = timeoutMinutes,
            WorkspaceSlug = workspaceSlug,
        };
    }

    private static void PrintHelp()
    {
        Console.WriteLine("ArchMind validation harness");
        Console.WriteLine("  --api-url            (default http://localhost:5000)");
        Console.WriteLine("  --admin-email        (default admin@archmind.dev)");
        Console.WriteLine("  --admin-password     (default $AdminSeed__Password or 'changeme')");
        Console.WriteLine("  --test-repo          (default https://github.com/Smbatyan/blot-stars)");
        Console.WriteLine("  --timeout-minutes    (default 30)");
        Console.WriteLine("  --workspace-slug     (default validation-<timestamp>)");
        Console.WriteLine("Env: VALIDATION_GITHUB_PAT  optional PAT for private repos");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 = all checks pass; 1 = at least one failure.");
    }
}
