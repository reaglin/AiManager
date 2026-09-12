using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eaglin.AiManager.Providers;

/// <summary>Shared plumbing: one HttpClient, uniform error mapping to <see cref="AiException"/>.</summary>
internal static class ProviderHttp
{
    public static readonly HttpClient DefaultClient = new() { Timeout = TimeSpan.FromSeconds(300) };

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, AiProviderType provider, string context, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(request, ct);
        }
        catch (TaskCanceledException tce) when (!ct.IsCancellationRequested)
        {
            ActivityLog.Error(context, "TIMEOUT", tce);
            throw new AiException(AiErrorKind.Timeout, provider,
                $"The request timed out after {client.Timeout.TotalSeconds:0} seconds.", tce);
        }
        catch (HttpRequestException hre)
        {
            ActivityLog.Error(context, "NETWORK", hre);
            throw new AiException(AiErrorKind.Network, provider, $"Network error: {hre.Message}", hre);
        }
    }

    public static async Task ThrowIfFailed(
        AiProviderType provider, string context, string vendor, HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body   = await response.Content.ReadAsStringAsync(ct);
        var detail = TryExtractErrorMessage(body);
        var status = (int)response.StatusCode;
        ActivityLog.Error(context, $"HTTP {status}: {detail}");

        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AiErrorKind.Auth,
            HttpStatusCode.TooManyRequests                          => AiErrorKind.RateLimit,
            _                                                       => AiErrorKind.ProviderError
        };
        var message = kind switch
        {
            AiErrorKind.Auth      => $"{vendor} rejected the API key ({status}): {detail}",
            AiErrorKind.RateLimit => $"{vendor} is rate-limiting requests (429): {detail}",
            _                     => $"{vendor} returned {status}: {detail}"
        };
        throw new AiException(kind, provider, message, statusCode: status);
    }

    /// <summary>Pulls "error.message" (all vendors use that shape) from an error body.</summary>
    private static string TryExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var msg))
                    return msg.GetString() ?? body;
                if (err.ValueKind == JsonValueKind.String) return err.GetString() ?? body;
            }
        }
        catch { /* not JSON */ }
        return body.Length > 600 ? body[..600] + "…" : body;
    }

    public static AiException NoKey(AiProviderType provider, string vendor) =>
        new(AiErrorKind.NoKey, provider, $"No {vendor} API key is stored. Add one in AI Settings.");

    public static void LogRequest(string context, int maxTokens, string system, string user) =>
        ActivityLog.AiCall(context, $"REQUEST  max_tokens={maxTokens}  system_chars={system.Length}  user_chars={user.Length}");
}

// ── Claude (Anthropic) ────────────────────────────────────────────────────────

/// <summary>Anthropic Messages API (<c>/v1/messages</c>).</summary>
public sealed class ClaudeProvider : IAiProvider
{
    public AiProviderType Type => AiProviderType.Claude;
    public string ProviderName => "Claude (Anthropic)";
    public string Model { get; }

    private readonly string _apiKey;
    private readonly HttpClient _http;
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    public ClaudeProvider(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<ProviderResult> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        if (!HasApiKey) throw ProviderHttp.NoKey(Type, "Claude");

        var context = $"Claude/{Model}";
        ProviderHttp.LogRequest(context, maxTokens, systemPrompt, userPrompt);

        var request = new
        {
            model      = Model,
            max_tokens = maxTokens,
            system     = systemPrompt,
            messages   = new[] { new { role = "user", content = userPrompt } }
        };

        using var http = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        http.Headers.Add("x-api-key", _apiKey);
        http.Headers.Add("anthropic-version", AnthropicVersion);
        http.Content = JsonContent.Create(request);

        using var response = await ProviderHttp.SendAsync(_http, Type, context, http, ct);
        await ProviderHttp.ThrowIfFailed(Type, context, "Anthropic", response, ct);

        var result = await response.Content.ReadFromJsonAsync<ClaudeResponse>(cancellationToken: ct)
                     ?? throw new AiException(AiErrorKind.EmptyResponse, Type, "Claude returned an empty response.");

        if (result.StopReason == "refusal")
        {
            ActivityLog.Warn(context, "stop_reason=refusal");
            throw new AiException(AiErrorKind.Refused, Type, "Claude declined this request (safety refusal). Rephrase and try again.");
        }

        var text = string.Concat(result.Content?.Where(c => c.Type == "text").Select(c => c.Text) ?? Array.Empty<string>());
        if (string.IsNullOrEmpty(text))
            throw new AiException(AiErrorKind.EmptyResponse, Type, "Claude returned no text.");

        var inTok  = result.Usage?.InputTokens  ?? 0;
        var outTok = result.Usage?.OutputTokens ?? 0;
        ActivityLog.AiCall(context, $"RESPONSE  stop={result.StopReason}  in={inTok}  out={outTok}  chars={text.Length}");
        if (result.StopReason == "max_tokens")
            ActivityLog.Warn(context, $"Output truncated at {outTok} tokens (max_tokens={maxTokens}).");

        return new ProviderResult(text, inTok, outTok, result.StopReason);
    }

    private sealed record ClaudeResponse(
        [property: JsonPropertyName("content")]     ClaudeContent[]? Content,
        [property: JsonPropertyName("usage")]       ClaudeUsage?     Usage,
        [property: JsonPropertyName("stop_reason")] string?          StopReason);
    private sealed record ClaudeContent(
        [property: JsonPropertyName("type")] string  Type,
        [property: JsonPropertyName("text")] string? Text);
    private sealed record ClaudeUsage(
        [property: JsonPropertyName("input_tokens")]  int InputTokens,
        [property: JsonPropertyName("output_tokens")] int OutputTokens);
}

// ── Gemini (Google) ───────────────────────────────────────────────────────────

public sealed class GeminiProvider : IAiProvider
{
    public AiProviderType Type => AiProviderType.Gemini;
    public string ProviderName => "Gemini (Google)";
    public string Model { get; }
    private readonly string _apiKey;
    private readonly HttpClient _http;

    public GeminiProvider(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<ProviderResult> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        if (!HasApiKey) throw ProviderHttp.NoKey(Type, "Gemini");

        var context = $"Gemini/{Model}";
        ProviderHttp.LogRequest(context, maxTokens, systemPrompt, userPrompt);

        var modelSegment = Model.TrimStart('/');
        if (!modelSegment.StartsWith("models/", StringComparison.Ordinal)) modelSegment = "models/" + modelSegment;
        var url = $"https://generativelanguage.googleapis.com/v1beta/{modelSegment}:generateContent";

        var request = new
        {
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            contents          = new[] { new { parts = new[] { new { text = userPrompt } } } },
            generationConfig  = new { maxOutputTokens = maxTokens }
        };

        using var http = new HttpRequestMessage(HttpMethod.Post, url);
        http.Headers.Add("x-goog-api-key", _apiKey);
        http.Content = JsonContent.Create(request);

        using var response = await ProviderHttp.SendAsync(_http, Type, context, http, ct);
        await ProviderHttp.ThrowIfFailed(Type, context, "Gemini", response, ct);

        var result = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: ct);
        var candidate = result?.Candidates?.FirstOrDefault();
        var text = string.Concat(candidate?.Content?.Parts?.Select(p => p.Text) ?? Array.Empty<string>());
        if (string.IsNullOrEmpty(text))
            throw new AiException(AiErrorKind.EmptyResponse, Type, "Gemini returned an empty response.");

        var inTok  = result?.Usage?.PromptTokenCount     ?? 0;
        var outTok = result?.Usage?.CandidatesTokenCount ?? 0;
        ActivityLog.AiCall(context, $"RESPONSE  finish={candidate?.FinishReason}  in={inTok}  out={outTok}  chars={text.Length}");
        return new ProviderResult(text, inTok, outTok, candidate?.FinishReason);
    }

    private sealed record GeminiResponse(
        [property: JsonPropertyName("candidates")]    GeminiCandidate[]? Candidates,
        [property: JsonPropertyName("usageMetadata")] GeminiUsage?       Usage);
    private sealed record GeminiCandidate(
        [property: JsonPropertyName("content")]      GeminiContent? Content,
        [property: JsonPropertyName("finishReason")] string?        FinishReason);
    private sealed record GeminiContent([property: JsonPropertyName("parts")] GeminiPart[]? Parts);
    private sealed record GeminiPart([property: JsonPropertyName("text")] string? Text);
    private sealed record GeminiUsage(
        [property: JsonPropertyName("promptTokenCount")]     int PromptTokenCount,
        [property: JsonPropertyName("candidatesTokenCount")] int CandidatesTokenCount);
}

// ── OpenAI-shaped chat completions: OpenAI, Mistral, xAI ──────────────────────

public sealed class OpenAiProvider : IAiProvider
{
    public AiProviderType Type => AiProviderType.OpenAi;
    public string ProviderName => "OpenAI";
    public string Model { get; }
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private const string ApiUrl = "https://api.openai.com/v1/chat/completions";

    public OpenAiProvider(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    public Task<ProviderResult> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct) =>
        ChatCompletions.GenerateAsync(_http, Type, "OpenAI", ApiUrl, _apiKey, Model, systemPrompt, userPrompt, maxTokens, ct);
}

public sealed class MistralProvider : IAiProvider
{
    public AiProviderType Type => AiProviderType.Mistral;
    public string ProviderName => "Mistral AI";
    public string Model { get; }
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private const string ApiUrl = "https://api.mistral.ai/v1/chat/completions";

    public MistralProvider(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    public Task<ProviderResult> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct) =>
        ChatCompletions.GenerateAsync(_http, Type, "Mistral", ApiUrl, _apiKey, Model, systemPrompt, userPrompt, maxTokens, ct);
}

/// <summary>xAI's Grok models; the API is OpenAI-shaped.</summary>
public sealed class XAiProvider : IAiProvider
{
    public AiProviderType Type => AiProviderType.XAi;
    public string ProviderName => "xAI (Grok)";
    public string Model { get; }
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private const string ApiUrl = "https://api.x.ai/v1/chat/completions";

    public XAiProvider(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    public Task<ProviderResult> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct) =>
        ChatCompletions.GenerateAsync(_http, Type, "xAI", ApiUrl, _apiKey, Model, systemPrompt, userPrompt, maxTokens, ct);
}

/// <summary>OpenAI-style chat-completions call shared by OpenAI, Mistral and xAI.</summary>
internal static class ChatCompletions
{
    public static async Task<ProviderResult> GenerateAsync(
        HttpClient client, AiProviderType provider, string vendor, string url, string apiKey, string model,
        string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw ProviderHttp.NoKey(provider, vendor);

        var context = $"{vendor}/{model}";
        ProviderHttp.LogRequest(context, maxTokens, systemPrompt, userPrompt);

        var request = new
        {
            model      = model,
            max_tokens = maxTokens,
            messages   = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userPrompt   }
            }
        };

        using var http = new HttpRequestMessage(HttpMethod.Post, url);
        http.Headers.Add("Authorization", $"Bearer {apiKey}");
        http.Content = JsonContent.Create(request);

        using var response = await ProviderHttp.SendAsync(client, provider, context, http, ct);
        await ProviderHttp.ThrowIfFailed(provider, context, vendor, response, ct);

        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct);
        var choice = result?.Choices?.FirstOrDefault();
        var text   = choice?.Message?.Content;
        if (string.IsNullOrEmpty(text))
            throw new AiException(AiErrorKind.EmptyResponse, provider, $"{vendor} returned an empty response.");

        var inTok  = result?.Usage?.PromptTokens     ?? 0;
        var outTok = result?.Usage?.CompletionTokens ?? 0;
        ActivityLog.AiCall(context, $"RESPONSE  finish={choice?.FinishReason}  in={inTok}  out={outTok}  chars={text.Length}");
        return new ProviderResult(text, inTok, outTok, choice?.FinishReason);
    }

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] ChatChoice[]? Choices,
        [property: JsonPropertyName("usage")]   ChatUsage?    Usage);
    private sealed record ChatChoice(
        [property: JsonPropertyName("message")]       ChatMessage? Message,
        [property: JsonPropertyName("finish_reason")] string?      FinishReason);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);
    private sealed record ChatUsage(
        [property: JsonPropertyName("prompt_tokens")]     int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens);
}
