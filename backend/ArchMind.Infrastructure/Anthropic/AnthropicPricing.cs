using ArchMind.Core.Abstractions;

namespace ArchMind.Infrastructure.Anthropic;

/// <summary>
/// Pricing constants and model-ID mapping for Anthropic models used in ArchMind.
/// Per ArchMind-MVP-Technical.md.
/// </summary>
public static class AnthropicPricing
{
    public const string HaikuModelId = "claude-haiku-4-5-20251001";
    public const string SonnetModelId = "claude-sonnet-4-6";

    public const decimal HaikuInputPerMillion = 0.80m;
    public const decimal HaikuOutputPerMillion = 4.00m;
    public const decimal SonnetInputPerMillion = 3.00m;
    public const decimal SonnetOutputPerMillion = 15.00m;

    public static string ModelId(AnthropicModel m) => m switch
    {
        AnthropicModel.Haiku => HaikuModelId,
        AnthropicModel.Sonnet => SonnetModelId,
        _ => throw new ArgumentOutOfRangeException(nameof(m))
    };

    public static decimal ComputeCostUsd(AnthropicModel m, int inputTokens, int outputTokens) => m switch
    {
        AnthropicModel.Haiku => (inputTokens * HaikuInputPerMillion + outputTokens * HaikuOutputPerMillion) / 1_000_000m,
        AnthropicModel.Sonnet => (inputTokens * SonnetInputPerMillion + outputTokens * SonnetOutputPerMillion) / 1_000_000m,
        _ => throw new ArgumentOutOfRangeException(nameof(m))
    };
}
