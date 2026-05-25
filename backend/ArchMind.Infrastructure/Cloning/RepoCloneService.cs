using System.Diagnostics;
using System.Text;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArchMind.Infrastructure.Cloning;

/// <summary>
/// Clones, updates, and cleans up GitHub repositories on local disk by shelling out
/// to the <c>git</c> CLI.
///
/// Design notes:
/// <list type="bullet">
///   <item>
///     PAT injection avoids persisting the token to <c>.git/config</c>:
///     for the initial clone we use the <c>https://x-access-token:&lt;PAT&gt;@github.com/...</c>
///     URL form, then immediately rewrite the remote to the clean URL.
///     For subsequent fetches we pass <c>-c http.extraHeader=...</c> at the command line.
///   </item>
///   <item>Stateless — safe to register as a singleton.</item>
/// </list>
/// </summary>
public sealed class RepoCloneService : IRepoCloneService
{
    private readonly CloningOptions _options;
    private readonly ILogger<RepoCloneService> _logger;

    public RepoCloneService(IOptions<CloningOptions> options, ILogger<RepoCloneService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task CloneAsync(Repo repo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrWhiteSpace(repo.WorkingDirPath))
        {
            throw new ArgumentException("Repo.WorkingDirPath must be set before cloning.", nameof(repo));
        }
        if (string.IsNullOrWhiteSpace(repo.GitHubUrl))
        {
            throw new ArgumentException("Repo.GitHubUrl must be set before cloning.", nameof(repo));
        }

        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "Cloning repo {RepoId} (workspace {WorkspaceId}) from {Url} into {Path}",
            repo.Id, repo.WorkspaceId, repo.GitHubUrl, repo.WorkingDirPath);

        // Initial-clone path: if a working dir already exists, blow it away.
        if (Directory.Exists(repo.WorkingDirPath))
        {
            _logger.LogWarning(
                "Working dir {Path} already exists for repo {RepoId}; deleting before re-clone",
                repo.WorkingDirPath, repo.Id);
            Directory.Delete(repo.WorkingDirPath, recursive: true);
        }

        // Ensure parent directory exists so `git clone` can create the leaf itself.
        var parent = Path.GetDirectoryName(repo.WorkingDirPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var cleanUrl = BuildCleanHttpsUrl(repo.GitHubUrl);
        var tokenUrl = BuildTokenUrl(repo.GitHubUrl, repo.PatToken);
        var branch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch;

        // git clone --depth 1 --single-branch --branch <branch> <tokenUrl> <workingDir>
        // Run from a stable directory; the leaf dir will be created by git.
        var cloneArgs = new[]
        {
            "clone",
            "--depth", "1",
            "--single-branch",
            "--branch", branch,
            tokenUrl,
            repo.WorkingDirPath,
        };

        await RunGitAsync(
            args: cloneArgs,
            workingDirectory: parent ?? Environment.CurrentDirectory,
            redactArg: tokenUrl,
            redactReplacement: cleanUrl,
            ct: ct).ConfigureAwait(false);

        // Immediately scrub the token from the remote URL.
        await RunGitAsync(
            args: new[] { "remote", "set-url", "origin", cleanUrl },
            workingDirectory: repo.WorkingDirPath,
            ct: ct).ConfigureAwait(false);

        // Sanity check: .git directory must exist.
        var gitDir = Path.Combine(repo.WorkingDirPath, ".git");
        if (!Directory.Exists(gitDir))
        {
            throw new RepoCloneException(
                $"Clone reported success but no .git directory found at {gitDir}",
                stderr: string.Empty);
        }

        sw.Stop();
        _logger.LogInformation(
            "Cloned repo {RepoId} in {ElapsedMs} ms",
            repo.Id, sw.ElapsedMilliseconds);
    }

    public async Task<RepoUpdateResult> UpdateAsync(Repo repo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (!Directory.Exists(Path.Combine(repo.WorkingDirPath, ".git")))
        {
            throw new RepoCloneException(
                $"Cannot update repo {repo.Id}: no .git directory at {repo.WorkingDirPath}",
                stderr: string.Empty);
        }

        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "Updating repo {RepoId} (workspace {WorkspaceId}) at {Path}",
            repo.Id, repo.WorkspaceId, repo.WorkingDirPath);

        var previousSha = await GetCurrentShaAsync(repo, ct).ConfigureAwait(false);
        var branch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch;

        // Authenticate the fetch via -c http.extraHeader so credentials never touch .git/config.
        var authHeader = BuildBasicAuthHeader(repo.PatToken);
        var redactReplacement = "Authorization: Basic ***";

        await RunGitAsync(
            args: new[]
            {
                "-c", $"http.extraHeader={authHeader}",
                "fetch", "--depth=1", "origin", branch,
            },
            workingDirectory: repo.WorkingDirPath,
            redactArg: authHeader,
            redactReplacement: redactReplacement,
            ct: ct).ConfigureAwait(false);

        await RunGitAsync(
            args: new[] { "reset", "--hard", $"origin/{branch}" },
            workingDirectory: repo.WorkingDirPath,
            ct: ct).ConfigureAwait(false);

        var currentSha = await GetCurrentShaAsync(repo, ct).ConfigureAwait(false);

        sw.Stop();
        _logger.LogInformation(
            "Updated repo {RepoId}: {Previous} -> {Current} in {ElapsedMs} ms",
            repo.Id, previousSha, currentSha, sw.ElapsedMilliseconds);

        return new RepoUpdateResult(previousSha, currentSha, !string.Equals(previousSha, currentSha, StringComparison.Ordinal));
    }

    public Task CleanupAsync(Repo repo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrWhiteSpace(repo.WorkingDirPath))
        {
            return Task.CompletedTask;
        }

        if (Directory.Exists(repo.WorkingDirPath))
        {
            _logger.LogInformation(
                "Cleaning up working dir for repo {RepoId} at {Path}",
                repo.Id, repo.WorkingDirPath);
            Directory.Delete(repo.WorkingDirPath, recursive: true);
            _logger.LogInformation("Removed working dir for repo {RepoId}", repo.Id);
        }
        else
        {
            _logger.LogDebug(
                "Cleanup requested for repo {RepoId} but {Path} does not exist",
                repo.Id, repo.WorkingDirPath);
        }

        return Task.CompletedTask;
    }

    public async Task<string> GetCurrentShaAsync(Repo repo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var result = await RunGitAsync(
            args: new[] { "rev-parse", "HEAD" },
            workingDirectory: repo.WorkingDirPath,
            ct: ct).ConfigureAwait(false);

        return result.Stdout.Trim();
    }

    // ---- internals ---------------------------------------------------------

    private async Task<GitResult> RunGitAsync(
        IEnumerable<string> args,
        string workingDirectory,
        CancellationToken ct,
        string? redactArg = null,
        string? redactReplacement = null)
    {
        var argList = args.ToList();

        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in argList)
        {
            psi.ArgumentList.Add(a);
        }

        // Avoid any interactive credential prompts hanging the process.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        var displayArgs = RenderArgsForLogging(argList, redactArg, redactReplacement);
        _logger.LogDebug("Running: git {Args} (cwd={Cwd})", displayArgs, workingDirectory);

        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) stdoutBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderrBuilder.AppendLine(e.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new RepoCloneException($"Failed to start git process: {ex.Message}", stderr: string.Empty, inner: ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.GitTimeoutSeconds));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            // Hit our timeout budget.
            var stderrOnTimeout = Redact(stderrBuilder.ToString(), redactArg, redactReplacement);
            throw new RepoCloneException(
                $"git {displayArgs} timed out after {timeout.TotalSeconds:F0}s",
                stderr: stderrOnTimeout);
        }

        // Ensure the async readers have flushed.
        process.WaitForExit();

        var stdout = stdoutBuilder.ToString();
        var stderr = Redact(stderrBuilder.ToString(), redactArg, redactReplacement);

        if (process.ExitCode != 0)
        {
            _logger.LogError(
                "git {Args} exited {ExitCode}. stderr: {Stderr}",
                displayArgs, process.ExitCode, stderr);

            if (LooksLikeAuthFailure(stderr))
            {
                throw new RepoAuthException(
                    $"git authentication failed for `git {displayArgs}` (exit {process.ExitCode}).",
                    stderr);
            }
            if (LooksLikeNetworkFailure(stderr))
            {
                throw new RepoNetworkException(
                    $"git network failure for `git {displayArgs}` (exit {process.ExitCode}).",
                    stderr);
            }
            throw new RepoCloneException(
                $"git {displayArgs} exited {process.ExitCode}.",
                stderr);
        }

        return new GitResult(stdout, stderr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static bool LooksLikeAuthFailure(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        return stderr.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Invalid username or password", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("fatal: Authentication", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNetworkFailure(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        return stderr.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }

    private static string RenderArgsForLogging(IEnumerable<string> args, string? redactArg, string? redactReplacement)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var a in args)
        {
            if (!first) sb.Append(' ');
            first = false;

            var rendered = a;
            if (!string.IsNullOrEmpty(redactArg) && rendered.Contains(redactArg, StringComparison.Ordinal))
            {
                rendered = rendered.Replace(redactArg, redactReplacement ?? "***", StringComparison.Ordinal);
            }
            sb.Append(rendered);
        }
        return sb.ToString();
    }

    private static string Redact(string input, string? redactArg, string? redactReplacement)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(redactArg)) return input;
        return input.Replace(redactArg, redactReplacement ?? "***", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the HTTPS clone URL with no embedded credentials and a trailing <c>.git</c>.
    /// Works for both <c>https://...</c> and <c>git@github.com:...</c> inputs.
    /// </summary>
    private static string BuildCleanHttpsUrl(string githubUrl)
    {
        var (owner, name) = GitHubUrlParser.Parse(githubUrl);
        return $"https://github.com/{owner}/{name}.git";
    }

    /// <summary>
    /// Returns an HTTPS clone URL with a <c>x-access-token:&lt;PAT&gt;</c> userinfo segment.
    /// Used only as the argv to <c>git clone</c>; the remote is rewritten immediately afterwards.
    /// </summary>
    private static string BuildTokenUrl(string githubUrl, string patToken)
    {
        var (owner, name) = GitHubUrlParser.Parse(githubUrl);
        var encodedToken = Uri.EscapeDataString(patToken ?? string.Empty);
        return $"https://x-access-token:{encodedToken}@github.com/{owner}/{name}.git";
    }

    /// <summary>
    /// Builds the value for <c>http.extraHeader</c>:
    /// <c>Authorization: Basic base64(x-access-token:PAT)</c>.
    /// </summary>
    private static string BuildBasicAuthHeader(string patToken)
    {
        var raw = $"x-access-token:{patToken ?? string.Empty}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        return $"Authorization: Basic {b64}";
    }

    private readonly record struct GitResult(string Stdout, string Stderr);
}
