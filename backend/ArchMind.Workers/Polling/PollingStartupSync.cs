using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArchMind.Workers.Polling;

/// <summary>
/// Hosted service that runs once on application startup to register a
/// recurring poll job for every existing repo (BE-024). Wraps the synchronize
/// call in try/catch — startup must not fail just because Hangfire isn't
/// ready or the database is briefly unreachable. The next process start (or
/// repo-create call) will recover.
/// </summary>
public sealed class PollingStartupSync : IHostedService
{
    private readonly IPollingRegistrar _registrar;
    private readonly IOptionsMonitor<PollingOptions> _options;
    private readonly ILogger<PollingStartupSync> _logger;

    public PollingStartupSync(
        IPollingRegistrar registrar,
        IOptionsMonitor<PollingOptions> options,
        ILogger<PollingStartupSync> logger)
    {
        _registrar = registrar;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            _logger.LogInformation("PollingStartupSync: polling disabled, skipping.");
            return;
        }

        try
        {
            await _registrar.SynchronizeAllAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "PollingStartupSync failed; recurring polls will be (re)registered on next startup or repo create.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
