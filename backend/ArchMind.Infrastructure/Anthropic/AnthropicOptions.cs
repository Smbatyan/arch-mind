namespace ArchMind.Infrastructure.Anthropic;

/// <summary>
/// Bound from configuration section "Anthropic" (env: Anthropic__ApiKey).
/// </summary>
public class AnthropicOptions
{
    public string ApiKey { get; set; } = string.Empty;
}
