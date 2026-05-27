using System.Diagnostics;
using System.Text;
using ArchMind.Core.Abstractions;
using ArchMind.Infrastructure.Cloning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArchMind.Infrastructure.Graphify;

/// <summary>
/// Shells out to <c>scripts/combine_workspace_graph.py</c> to rebuild the
/// combined workspace-level graph HTML after each repo scan completes.
///
/// The Python script reads only existing <c>graphify-out/graph.json</c> files —
/// no LLM / re-extraction is triggered.
/// </summary>
public sealed class WorkspaceGraphService : IWorkspaceGraphService
{
    private readonly CloningOptions _cloning;
    private readonly WorkspaceGraphOptions _options;
    private readonly StructuralGraphService _structuralCache;
    private readonly ILogger<WorkspaceGraphService> _logger;

    public WorkspaceGraphService(
        IOptions<CloningOptions> cloning,
        IOptions<WorkspaceGraphOptions> options,
        StructuralGraphService structuralCache,
        ILogger<WorkspaceGraphService> logger)
    {
        _cloning = cloning.Value;
        _options = options.Value;
        _structuralCache = structuralCache;
        _logger = logger;
    }

    public async Task RebuildAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var workspaceDir = Path.Combine(_cloning.WorkingDirRoot, workspaceId.ToString());

        if (!Directory.Exists(workspaceDir))
        {
            _logger.LogDebug(
                "WorkspaceGraphService: workspace dir does not exist, skipping rebuild workspace={WorkspaceId}",
                workspaceId);
            return;
        }

        _logger.LogInformation(
            "WorkspaceGraphService: rebuilding combined graph workspace={WorkspaceId} dir={WorkspaceDir}",
            workspaceId, workspaceDir);

        var psi = new ProcessStartInfo
        {
            FileName = _options.PythonExecutable,
            WorkingDirectory = workspaceDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add(_options.CombineScriptPath);
        psi.ArgumentList.Add(workspaceDir);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            // Non-fatal: combined graph is a visualisation aid, not a hard requirement.
            _logger.LogWarning(
                ex,
                "WorkspaceGraphService: failed to start Python subprocess workspace={WorkspaceId}",
                workspaceId);
            return;
        }

        if (process is null)
        {
            _logger.LogWarning(
                "WorkspaceGraphService: Process.Start returned null workspace={WorkspaceId}",
                workspaceId);
            return;
        }

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuf.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            _logger.LogWarning(
                "WorkspaceGraphService: timed out after {Timeout}s workspace={WorkspaceId}",
                _options.TimeoutSeconds, workspaceId);
            return;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        process.WaitForExit();

        var exitCode = process.ExitCode;
        var stdout = stdoutBuf.ToString();
        var stderr = stderrBuf.ToString();

        if (exitCode != 0)
        {
            _logger.LogWarning(
                "WorkspaceGraphService: script exited {ExitCode} workspace={WorkspaceId} stderr={Stderr}",
                exitCode, workspaceId, stderr.Trim());
            return;
        }

        _logger.LogInformation(
            "WorkspaceGraphService: combined graph rebuilt workspace={WorkspaceId} output={Output}",
            workspaceId, stdout.Trim());

        // Drop any cached parse so the next /graph/structural call sees new repos.
        _structuralCache.Invalidate(workspaceId);
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* already exited */ }
    }
}
