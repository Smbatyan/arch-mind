namespace ArchMind.Workers.Polling;

/// <summary>
/// Configuration for the per-repo Hangfire recurring poll (BE-024).
/// Bound from the <c>Polling</c> section of configuration. Disable via
/// <see cref="Enabled"/> = <c>false</c> in tests or dev to silence
/// background polling chatter.
/// </summary>
public sealed class PollingOptions
{
    /// <summary>
    /// Cron expression that controls how often each repo is polled for
    /// new commits. Default: every 30 minutes.
    /// </summary>
    public string CronExpression { get; set; } = "*/30 * * * *";

    /// <summary>
    /// When <c>false</c>, the startup synchronization is skipped and
    /// <see cref="IPollingRegistrar.RegisterRepo"/> becomes a no-op.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
