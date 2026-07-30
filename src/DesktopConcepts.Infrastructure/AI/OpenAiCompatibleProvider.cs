using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopConcepts.Domain;
using Microsoft.Extensions.Logging;

namespace DesktopConcepts.Infrastructure.AI;

/// <summary>
/// Single implementation that covers any OpenAI-compatible /v1/chat/completions endpoint:
/// LM Studio, Ollama (OpenAI-compat mode), OpenAI, Anthropic via proxy, etc.
/// Only BaseUrl / Model / ApiKey differ between providers — nothing else in the app changes.
///
/// Error contract: this class throws on any failure (network, non-2xx, bad JSON).
/// The Application layer catches, logs, and surfaces Retry/Settings UI.
/// </summary>
public sealed class OpenAiCompatibleProvider : IConceptProvider
{
    private readonly HttpClient _http;
    private readonly ProviderSettings _settings;
    private readonly ILogger<OpenAiCompatibleProvider> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OpenAiCompatibleProvider(
        HttpClient http,
        ProviderSettings settings,
        ILogger<OpenAiCompatibleProvider> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;

        if (!string.IsNullOrEmpty(settings.ApiKey))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
    }

    public async Task<Concept> GenerateConceptAsync(
        string category,
        IReadOnlyCollection<string> recentTitlesToAvoid,
        CancellationToken cancellationToken)
    {
        var avoidClause = recentTitlesToAvoid.Count > 0
            ? $" Avoid repeating any of these previous titles: {string.Join(", ", recentTitlesToAvoid)}."
            : string.Empty;

        var prompt =
            $"Explain one specific, highly technical {category} concept in 5-8 sentences, " +
            $"in a way a curious beginner can follow. " +
            $"Respond ONLY as a JSON object with exactly two fields: " +
            $"{{\"title\": \"...\", \"explanation\": \"...\"}}.{avoidClause}";

        var request = new ChatRequest(
            Model: _settings.Model,
            Messages: [new ChatMessage(Role: "user", Content: prompt)],
            Temperature: 0.8);

        var url = $"{_settings.BaseUrl.TrimEnd('/')}/chat/completions";
        _logger.LogDebug("Requesting concept for category '{Category}' from {Url}", category, url);

        var response = await _http.PostAsJsonAsync(url, request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Empty response body from AI provider.");

        var raw = payload.Choices[0].Message.Content.Trim();

        // Strip markdown code fences if the model wraps the JSON in ```json ... ```
        if (raw.StartsWith("```"))
        {
            var firstNewline = raw.IndexOf('\n');
            var lastFence    = raw.LastIndexOf("```");
            if (firstNewline > 0 && lastFence > firstNewline)
                raw = raw[(firstNewline + 1)..lastFence].Trim();
        }

        var parsed = JsonSerializer.Deserialize<ConceptPayload>(raw, JsonOptions)
            ?? throw new InvalidOperationException("AI response could not be parsed as JSON.");

        return new Concept(
            Title:       parsed.Title,
            Explanation: parsed.Explanation,
            Category:    category,
            GeneratedOn: DateOnly.FromDateTime(DateTime.Now));
    }

    // ── Private DTOs (never leak outside this class) ──────────────────────────

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")]    string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("temperature")] double Temperature);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")]    string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] ChatChoice[] Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage Message);

    private sealed record ConceptPayload(
        [property: JsonPropertyName("title")]       string Title,
        [property: JsonPropertyName("explanation")] string Explanation);
}
