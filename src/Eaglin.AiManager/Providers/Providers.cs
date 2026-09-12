using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eaglin.AiManager.Providers;

/// <summary>Shared plumbing: one HttpClient, uniform error mapping to <see cref="AiException"/>.</summary>
internal static class ProviderHttp
{
    public static readonly HttpClient DefaultClient = new() { Timeout = TimeSpan.FromSeconds(300) };

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, AiProviderType provider, string context, HttpRequestMessage request,
        CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            return await client.SendAsync(request, completion, ct);
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
    public static string TryExtractErrorMessage(string body)
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

    public static void LogRequest(string context, int maxTokens, string system, string user, bool stream = false) =>
        ActivityLog.AiCall(context, $"REQUEST{(stream ? " (stream)" : "")}  max_tokens={maxTokens}  system_chars={system.Length}  user_chars={user.Length}");

    public static void LogResponse(string context, string? stop, int inTok, int outTok, int chars, int maxTokens)
    {
        ActivityLog.AiCall(context, $"RESPONSE  stop={stop}  in={inTok}  out={outTok}  chars={chars}");
        if (stop is "max_tokens" or "length" or "MAX_TOKENS")
            ActivityLog.Warn(context, $"Output truncated at {outTok} tokens (max_tokens={maxTokens}).");
    }

    /// <summary>Opens a streaming response (headers only) and checks the status.</summary>
    public static async Task<HttpResponseMessage> OpenStreamAsync(
        HttpClient client, AiProviderType provider, string context, string vendor, HttpRequestMessage request, CancellationToken ct)
    {
        var response = await SendAsync(client, provider, context, request, ct, HttpCompletionOption.ResponseHeadersRead);
        try
        {
            await ThrowIfFailed(provider, context, vendor, response, ct);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
}

// ── Claude (Anthropic) ────────────────────────────────────────────────────────

/// <summary>Anthropic Messages API (<c>/v1/messages</c>), whole and streamed.</summary>
public sealed class ClaudeProvider : IAiProvider
{
    public AiProviderType Type => AiProviderType.Claude;
    public string ProviderName => "Claude (Anthropic)";
    public string Model { get; }

    private readonly string _apiKey;
    private readonly HttpClient _http;
    internal const string ApiUrl = "https://api.anthropic.com/v1/messages";
    internal const string AnthropicVersion = "2023-06-01";

    public ClaudeProvider(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    private HttpRequestMessage BuildRequest(string systemPrompt, string userPrompt, int maxTokens, bool stream)
    {
        var request = new
        {
            model      = Model,
            max_tokens = maxTokens,
            system     = systemPrompt,
            stream     = stream,
            messages   = new[] { new { role = "user", content = userPrompt } }
        };
        var http = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        http.Headers.Add("x-api-key", _apiKey);
        http.Headers.Add("anthropic-version", AnthropicVersion);
        http.Content = JsonContent.Create(request);
        return http;
    }

    public async Task<ProviderResult> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        if (!HasApiKey) throw ProviderHttp.NoKey(Type, "Claude");

        var context = $"Claude/{Model}";
        ProviderHttp.LogRequest(context, maxTokens, systemPrompt, userPrompt);

        using var http = BuildRequest(systemPrompt, userPrompt, maxTokens, stream: false);
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
        ProviderHttp.LogResponse(context, result.StopReason, inTok, outTok, text.Length, maxTokens);
        return new ProviderResult(text, inTok, outTok, result.StopReason);
    }

    public async IAsyncEnumerable<AiChunk> StreamAsync(
        string systemPrompt, string userPrompt, int maxTokens, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!HasApiKey) throw ProviderHttp.NoKey(Type, "Claude");

        var context = $"Claude/{Model}";
        ProviderHttp.LogRequest(context, maxTokens, systemPrompt, userPrompt, stream: true);

        using var http = BuildRequest(systemPrompt, userPrompt, maxTokens, stream: true);
        using var response = await ProviderHttp.OpenStreamAsync(_http, Type, context, "Anthropic", http, ct);
        using var stream = await response.Content.ReadAsStreamAsync(ct);

        int inTok = 0, outTok = 0, chars = 0;
        string? stop = null;

        await foreach (var e in SseReader.ReadAsync(stream, ct))
        {
            if (e.Data.Length == 0) continue;
            using var doc = JsonDocument.Parse(e.Data);
            var root = doc.RootElement;
            var type = e.Event ?? ProviderHttp.Str(root, "type");

            switch (type)
            {
                case "message_start":
                    inTok = ProviderHttp.Int(ProviderHttp.Obj(ProviderHttp.Obj(root, "message"), "usage"), "input_tokens");
                    break;

                case "content_block_delta":
                {
                    var delta = ProviderHttp.Obj(root, "delta");
                    if (ProviderHttp.Str(delta, "type") == "text_delta" && ProviderHttp.Str(delta, "text") is { Length: > 0 } text)
                    {
                        chars += text.Length;
                        yield return new AiChunk(text);
                    }
                    break;
                }

                case "message_delta":
                    stop   = ProviderHttp.Str(ProviderHttp.Obj(root, "delta"), "stop_reason") ?? stop;
                    outTok = Math.Max(outTok, ProviderHttp.Int(ProviderHttp.Obj(root, "usage"), "output_tokens"));
                    break;

                case "error":
                {
                    var msg = ProviderHttp.Str(ProviderHttp.Obj(root, "error"), "message") ?? e.Data;
                    ActivityLog.Error(context, $"stream error: {msg}");
                    throw new AiException(AiErrorKind.ProviderError, Type, $"Anthropic reported an error mid-stream: {msg}");
                }
            }
        }

        if (stop == "refusal")
        {
            ActivityLog.Warn(context, "stop_reason=refusal");
            throw new AiException(AiErrorKind.Refused, Type, "Claude declined this request (safety refusal). Rephrase and try again.");
        }
        if (chars == 0)
            throw new AiException(AiErrorKind.EmptyResponse, Type, "Claude returned no text.");

        ProviderHttp.LogResponse(context, stop, inTok, outTok, chars, maxTokens);
        yield return AiChunk.Final(inTok, outTok, stop);
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
    internal const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/";

    public GeminiProvider(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    internal static string ModelSegment(string model)
    {
        var s = model.TrimStart('/');
        return s.StartsWith("models/", StringComparison.Ordinal) ? s : "models/" + s;
    }

    private HttpRequestMessage BuildRequest(string systemPrompt, string userPrompt, int maxTokens, bool stream)
    {
        var url = BaseUrl + ModelSegment(Model) + (stream ? ":streamGenerateContent?alt=sse" : ":generateContent");
        var request = new
        {
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            contents          = new[] { new { parts = new[] { new { text = userPrompt } } } },
            generationConfig  = new { maxOutputTokens = maxTokens }
        };
        var http = new HttpRequestMessage(HttpMethod.Post, url);
        http.Headers.Add("x-goog-api-key", _apiKey);
        http.Content = JsonContent.Create(request);
        return http;
    }

    public async Task<ProviderResult> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        if (!HasApiKey) throw ProviderHttp.NoKey(Type, "Gemini");

        var context = $"Gemini/{Model}";
        ProviderHttp.LogRequest(context, maxTokens, systemPrompt, userPrompt);

        using var http = BuildRequest(systemPrompt, userPrompt, maxTokens, stream: false);
        using var response = await ProviderHttp.SendAsync(_http, Type, context, http, ct);
        await ProviderHttp.ThrowIfFailed(Type, context, "Gemini", response, ct);

        var result = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: ct);
        var candidate = result?.Candidates?.FirstOrDefault();
        var text = string.Concat(candidate?.Content?.Parts?.Select(p => p.Text) ?? Array.Empty<string>());
        if (string.IsNullOrEmpty(text))
            throw new AiException(AiErrorKind.EmptyResponse, Type, "Gemini returned an empty response.");

        var inTok  = result?.Usage?.PromptTokenCount     ?? 0;
        var outTok = result?.Usage?.CandidatesTokenCount ?? 0;
        ProviderHttp.LogResponse(context, candidate?.FinishReason, inTok, outTok, text.Length, maxTokens);
        return new ProviderResult(text, inTok, outTok, candidate?.FinishReason);
    }

    public async IAsyncEnumerable<AiChunk> StreamAsync(
        string systemPrompt, string userPrompt, int maxTokens, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!HasApiKey) throw ProviderHttp.NoKey(Type, "Gemini");

        var context = $"Gemini/{Model}";
        ProviderHttp.LogRequest(context, maxTokens, systemPrompt, userPrompt, stream: true);

        using var http = BuildRequest(systemPrompt, userPrompt, maxTokens, stream: true);
        using var response = await ProviderHttp.OpenStreamAsync(_http, Type, context, "Gemini", http, ct);
        using var stream = await response.Content.ReadAsStreamAsync(ct);

        int inTok = 0, outTok = 0, chars = 0;
        string? finish = null;

        await foreach (var e in SseReader.ReadAsync(stream, ct))
        {
            if (e.Data.Length == 0) continue;
            using var doc = JsonDocument.Parse(e.Data);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out _))
            {
                var msg = ProviderHttp.TryExtractErrorMessage(e.Data);
                ActivityLog.Error(context, $"stream error: {msg}");
                throw new AiException(AiErrorKind.ProviderError, Type, $"Gemini reported an error mid-stream: {msg}");
            }

            var usage = ProviderHttp.Obj(root, "usageMetadata");
            inTok  = Math.Max(inTok,  ProviderHttp.Int(usage, "promptTokenCount"));
            outTok = Math.Max(outTok, ProviderHttp.Int(usage, "candidatesTokenCount"));

            var candidates = ProviderHttp.Obj(root, "candidates");
            if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0) continue;
            var candidate = candidates[0];
            finish = ProviderHttp.Str(candidate, "finishReason") ?? finish;

            var parts = ProviderHttp.Obj(ProviderHttp.Obj(candidate, "content"), "parts");
            if (parts.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in parts.EnumerateArray())
            {
                if (ProviderHttp.Str(part, "text") is { Length: > 0 } text)
                {
                    chars += text.Length;
                    yield return new AiChunk(text);
                }
            }
        }

        if (chars == 0)
            throw new AiException(AiErrorKind.EmptyResponse, Type, "Gemini returned an empty response.");

        ProviderHttp.LogResponse(context, finish, inTok, outTok, chars, maxTokens);
        yield return AiChunk.Final(inTok, outTok, finish);
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
    internal const string ApiUrl = "https://api.openai.com/v1/chat/completions";

    public OpenAiProvider(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    public Task<ProviderResult> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct) =>
        ChatCompletions.GenerateAsync(_http, Type, "OpenAI", ApiUrl, _apiKey, Model, systemPrompt, userPrompt, maxTokens, ct);

    public IAsyncEnumerable<AiChunk> StreamAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct) =>
        ChatCompletions.StreamAsync(_http, Type, "OpenAI", ApiUrl, _apiKey, Model, systemPrompt, userPrompt, maxTokens, includeUsageOption: true, ct);
}

public sealed class MistralProvider : IAiProvider
{
    public AiProviderType Type => AiProviderType.Mistral;
    public string ProviderName => "Mistral AI";
    public string Model { get; }
    private readonly string _apiKey;
    private readonly HttpClient _http;
    internal const string ApiUrl = "https://api.mistral.ai/v1/chat/completions";

    public MistralProvider(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    public Task<ProviderResult> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct) =>
        ChatCompletions.GenerateAsync(_http, Type, "Mistral", ApiUrl, _apiKey, Model, systemPrompt, userPrompt, maxTokens, ct);

    // Mistral reports usage in its last chunk without being asked and rejects unknown fields.
    public IAsyncEnumerable<AiChunk> StreamAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct) =>
        ChatCompletions.StreamAsync(_http, Type, "Mistral", ApiUrl, _apiKey, Model, systemPrompt, userPrompt, maxTokens, includeUsageOption: false, ct);
}

/// <summary>xAI's Grok models; the API is OpenAI-shaped.</summary>
public sealed class XAiProvider : IAiProvider
{
    public AiProviderType Type => AiProviderType.XAi;
    public string ProviderName => "xAI (Grok)";
    public string Model { get; }
    private readonly string _apiKey;
    private readonly HttpClient _http;
    internal const string ApiUrl = "https://api.x.ai/v1/chat/completions";

    public XAiProvider(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    public Task<ProviderResult> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct) =>
        ChatCompletions.GenerateAsync(_http, Type, "xAI", ApiUrl, _apiKey, Model, systemPrompt, userPrompt, maxTokens, ct);

    public IAsyncEnumerable<AiChunk> StreamAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct) =>
        ChatCompletions.StreamAsync(_http, Type, "xAI", ApiUrl, _apiKey, Model, systemPrompt, userPrompt, maxTokens, includeUsageOption: true, ct);
}

/// <summary>OpenAI-style chat-completions calls shared by OpenAI, Mistral and xAI.</summary>
internal static class ChatCompletions
{
    private static HttpRequestMessage BuildRequest(
        string url, string apiKey, string model, string systemPrompt, string userPrompt, int maxTokens, bool stream, bool includeUsageOption)
    {
        object request = stream && includeUsageOption
            ? new
            {
                model, max_tokens = maxTokens, stream = true,
                stream_options = new { include_usage = true },
                messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } }
            }
            : new
            {
                model, max_tokens = maxTokens, stream,
                messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } }
            };

        var http = new HttpRequestMessage(HttpMethod.Post, url);
        http.Headers.Add("Authorization", $"Bearer {apiKey}");
        http.Content = JsonContent.Create(request, request.GetType());
        return http;
    }

    public static async Task<ProviderResult> GenerateAsync(
        HttpClient client, AiProviderType provider, string vendor, string url, string apiKey, string model,
        string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw ProviderHttp.NoKey(provider, vendor);

        var context = $"{vendor}/{model}";
        ProviderHttp.LogRequest(context, maxTokens, systemPrompt, userPrompt);

        using var http = BuildRequest(url, apiKey, model, systemPrompt, userPrompt, maxTokens, stream: false, includeUsageOption: false);
        using var response = await ProviderHttp.SendAsync(client, provider, context, http, ct);
        await ProviderHttp.ThrowIfFailed(provider, context, vendor, response, ct);

        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct);
        var choice = result?.Choices?.FirstOrDefault();
        var text   = choice?.Message?.Content;
        if (string.IsNullOrEmpty(text))
            throw new AiException(AiErrorKind.EmptyResponse, provider, $"{vendor} returned an empty response.");

        var inTok  = result?.Usage?.PromptTokens     ?? 0;
        var outTok = result?.Usage?.CompletionTokens ?? 0;
        ProviderHttp.LogResponse(context, choice?.FinishReason, inTok, outTok, text.Length, maxTokens);
        return new ProviderResult(text, inTok, outTok, choice?.FinishReason);
    }

    public static async IAsyncEnumerable<AiChunk> StreamAsync(
        HttpClient client, AiProviderType provider, string vendor, string url, string apiKey, string model,
        string systemPrompt, string userPrompt, int maxTokens, bool includeUsageOption, [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw ProviderHttp.NoKey(provider, vendor);

        var context = $"{vendor}/{model}";
        ProviderHttp.LogRequest(context, maxTokens, systemPrompt, userPrompt, stream: true);

        using var http = BuildRequest(url, apiKey, model, systemPrompt, userPrompt, maxTokens, stream: true, includeUsageOption);
        using var response = await ProviderHttp.OpenStreamAsync(client, provider, context, vendor, http, ct);
        using var stream = await response.Content.ReadAsStreamAsync(ct);

        int inTok = 0, outTok = 0, chars = 0;
        string? finish = null;

        await foreach (var e in SseReader.ReadAsync(stream, ct))
        {
            if (e.Data.Length == 0) continue;
            if (e.Data == "[DONE]") break;

            using var doc = JsonDocument.Parse(e.Data);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out _))
            {
                var msg = ProviderHttp.TryExtractErrorMessage(e.Data);
                ActivityLog.Error(context, $"stream error: {msg}");
                throw new AiException(AiErrorKind.ProviderError, provider, $"{vendor} reported an error mid-stream: {msg}");
            }

            var usage = ProviderHttp.Obj(root, "usage");
            inTok  = Math.Max(inTok,  ProviderHttp.Int(usage, "prompt_tokens"));
            outTok = Math.Max(outTok, ProviderHttp.Int(usage, "completion_tokens"));

            var choices = ProviderHttp.Obj(root, "choices");
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            finish = ProviderHttp.Str(choice, "finish_reason") ?? finish;

            if (ProviderHttp.Str(ProviderHttp.Obj(choice, "delta"), "content") is { Length: > 0 } text)
            {
                chars += text.Length;
                yield return new AiChunk(text);
            }
        }

        if (chars == 0)
            throw new AiException(AiErrorKind.EmptyResponse, provider, $"{vendor} returned an empty response.");

        ProviderHttp.LogResponse(context, finish, inTok, outTok, chars, maxTokens);
        yield return AiChunk.Final(inTok, outTok, finish);
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
