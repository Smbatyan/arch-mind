using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ArchMind.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArchMind.Infrastructure.Anthropic;

/// <summary>
/// Raw-HTTP implementation of <see cref="IAnthropicClient"/> against the Anthropic
/// Messages API. Forces structured output via the tool-use feature. Retries
/// transient failures (429, 5xx) up to 3 times with exponential backoff + jitter.
/// </summary>
public class AnthropicClient : IAnthropicClient
{
    private const string MessagesEndpoint = "/v1/messages";
    private const int MaxAttempts = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly AnthropicOptions _options;
    private readonly ILogger<AnthropicClient> _logger;
    private readonly Random _jitter = new();

    public AnthropicClient(
        HttpClient httpClient,
        IOptions<AnthropicOptions> options,
        ILogger<AnthropicClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AnthropicCallResult<T>> CompleteStructuredAsync<T>(
        string systemPrompt,
        string userPrompt,
        AnthropicModel model,
        string toolName,
        string toolDescription,
        string jsonSchema,
        CancellationToken ct = default
    ) where T : class
    {
        JsonElement parsedSchema;
        try
        {
            using var doc = JsonDocument.Parse(jsonSchema);
            parsedSchema = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new AnthropicResponseException("Invalid jsonSchema supplied to CompleteStructuredAsync.", ex);
        }

        var modelId = AnthropicPricing.ModelId(model);
        var requestBody = new
        {
            model = modelId,
            max_tokens = 4096,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userPrompt }
            },
            tools = new object[]
            {
                new
                {
                    name = toolName,
                    description = toolDescription,
                    input_schema = parsedSchema
                }
            },
            tool_choice = new { type = "tool", name = toolName }
        };

        var (responseJson, elapsed) = await SendWithRetryAsync(requestBody, ct);

        using var responseDoc = JsonDocument.Parse(responseJson);
        var root = responseDoc.RootElement;

        var (inputTokens, outputTokens) = ReadUsage(root);

        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            throw new AnthropicResponseException("Anthropic response missing 'content' array.");
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "tool_use" &&
                block.TryGetProperty("name", out var nameProp) &&
                nameProp.GetString() == toolName &&
                block.TryGetProperty("input", out var inputProp))
            {
                T? deserialized;
                try
                {
                    deserialized = JsonSerializer.Deserialize<T>(inputProp.GetRawText(), JsonOptions);
                }
                catch (JsonException ex)
                {
                    throw new AnthropicResponseException(
                        $"Failed to deserialize tool_use 'input' to {typeof(T).Name}.", ex);
                }

                if (deserialized is null)
                {
                    throw new AnthropicResponseException(
                        $"tool_use 'input' deserialized to null for {typeof(T).Name}.");
                }

                var cost = AnthropicPricing.ComputeCostUsd(model, inputTokens, outputTokens);
                return new AnthropicCallResult<T>(deserialized, modelId, inputTokens, outputTokens, cost, elapsed);
            }
        }

        throw new AnthropicResponseException("expected tool_use in response");
    }

    public async Task<AnthropicCallResult<string>> CompleteTextAsync(
        string systemPrompt,
        string userPrompt,
        AnthropicModel model,
        int maxTokens = 4096,
        CancellationToken ct = default
    )
    {
        var modelId = AnthropicPricing.ModelId(model);
        var requestBody = new
        {
            model = modelId,
            max_tokens = maxTokens,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userPrompt }
            }
        };

        var (responseJson, elapsed) = await SendWithRetryAsync(requestBody, ct);

        using var responseDoc = JsonDocument.Parse(responseJson);
        var root = responseDoc.RootElement;

        var (inputTokens, outputTokens) = ReadUsage(root);

        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            throw new AnthropicResponseException("Anthropic response missing 'content' array.");
        }

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "text" &&
                block.TryGetProperty("text", out var textProp))
            {
                sb.Append(textProp.GetString());
            }
        }

        var cost = AnthropicPricing.ComputeCostUsd(model, inputTokens, outputTokens);
        return new AnthropicCallResult<string>(sb.ToString(), modelId, inputTokens, outputTokens, cost, elapsed);
    }

    private static (int input, int output) ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            throw new AnthropicResponseException("Anthropic response missing 'usage' object.");
        }

        var input = usage.TryGetProperty("input_tokens", out var i) && i.ValueKind == JsonValueKind.Number
            ? i.GetInt32()
            : throw new AnthropicResponseException("usage.input_tokens missing or not a number.");

        var output = usage.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number
            ? o.GetInt32()
            : throw new AnthropicResponseException("usage.output_tokens missing or not a number.");

        return (input, output);
    }

    private async Task<(string responseJson, TimeSpan elapsed)> SendWithRetryAsync(
        object requestBody,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new AnthropicResponseException("Anthropic API key is not configured (Anthropic:ApiKey).");
        }

        var payload = JsonSerializer.Serialize(requestBody, JsonOptions);
        Exception? lastError = null;
        var stopwatch = Stopwatch.StartNew();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    stopwatch.Stop();
                    return (body, stopwatch.Elapsed);
                }

                var statusCode = response.StatusCode;
                var errorBody = await SafeReadAsync(response, ct);

                if (IsTransient(statusCode) && attempt < MaxAttempts)
                {
                    _logger.LogWarning(
                        "Anthropic request failed (attempt {Attempt}/{Max}) with status {Status}. Retrying. Body: {Body}",
                        attempt, MaxAttempts, (int)statusCode, Truncate(errorBody, 500));
                    await DelayWithBackoffAsync(attempt, ct);
                    continue;
                }

                throw new AnthropicResponseException(
                    $"Anthropic API returned non-success status {(int)statusCode} ({statusCode}). Body: {Truncate(errorBody, 1000)}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                lastError = ex;
                _logger.LogWarning(ex,
                    "Anthropic request transport failure (attempt {Attempt}/{Max}). Retrying.",
                    attempt, MaxAttempts);
                await DelayWithBackoffAsync(attempt, ct);
            }
            finally
            {
                response?.Dispose();
            }
        }

        throw new AnthropicResponseException(
            "Anthropic API call failed after retries.", lastError ?? new Exception("unknown transport failure"));
    }

    private static bool IsTransient(HttpStatusCode status)
    {
        if (status == HttpStatusCode.TooManyRequests) return true;
        return (int)status >= 500 && (int)status < 600;
    }

    private async Task DelayWithBackoffAsync(int attempt, CancellationToken ct)
    {
        // Exponential: 1s, 2s, 4s; plus 0-250ms jitter.
        var baseMs = (int)Math.Pow(2, attempt - 1) * 1000;
        int jitterMs;
        lock (_jitter)
        {
            jitterMs = _jitter.Next(0, 250);
        }
        await Task.Delay(baseMs + jitterMs, ct);
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}
