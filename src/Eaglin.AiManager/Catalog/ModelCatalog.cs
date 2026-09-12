using System.Text;
using System.Text.Json;
using Eaglin.AiManager.Providers;
using Eaglin.AiManager.Store;

namespace Eaglin.AiManager.Catalog;

public sealed record ModelInfo(string Id, string DisplayName, DateTime? Created);

/// <summary>What a catalog lookup returns: the models, when they were fetched, or that they are the built-in fallback.</summary>
public sealed record ModelList(IReadOnlyList<ModelInfo> Models, DateTime? FetchedUtc, bool FromFallback);

/// <summary>
/// The list of models each vendor currently offers, fetched from its list-models endpoint and
/// cached in <c>{root}\models.json</c>. Without a key, or when a fetch fails and nothing is
/// cached, the built-in <see cref="AiProviderRouter.FallbackModels"/> list is returned so a
/// settings window always has something to show.
/// </summary>
public sealed class ModelCatalog
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Path { get; }
    private readonly CredentialStore _credentials;
    private readonly HttpClient _http;
    private Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ModelCatalog(string root, CredentialStore credentials, HttpClient? http = null)
    {
        Path         = AiPaths.ModelsFile(root);
        _credentials = credentials;
        _http        = http ?? ProviderHttp.DefaultClient;
        LoadCache();
    }

    /// <summary>The cached list without any network call (null when nothing is cached).</summary>
    public ModelList? Cached(AiProviderType provider) =>
        _cache.TryGetValue(provider.ToString(), out var e)
            ? new ModelList(e.Models, e.FetchedUtc, FromFallback: false)
            : null;

    public static ModelList Fallback(AiProviderType provider) =>
        new(AiProviderRouter.FallbackModels.TryGetValue(provider, out var ids)
                ? ids.Select(id => new ModelInfo(id, id, null)).ToList()
                : new List<ModelInfo>(),
            null, FromFallback: true);

    /// <summary>
    /// Cached list if there is one (unless <paramref name="refresh"/>), else fetched from the
    /// vendor when a key exists, else the built-in fallback. With <paramref name="refresh"/> a
    /// failed fetch throws <see cref="AiException"/>; otherwise it is logged and the best
    /// available list is returned.
    /// </summary>
    public async Task<ModelList> ListAsync(AiProviderType provider, bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && Cached(provider) is { } cached) return cached;

        var key = _credentials.KeyFor(provider);
        if (key.Length == 0)
        {
            if (refresh) throw ProviderHttp.NoKey(provider, AiProviderRouter.DisplayName(provider));
            return Cached(provider) ?? Fallback(provider);
        }

        try
        {
            var models = await FetchAsync(provider, key, ct);
            var entry  = new CacheEntry { FetchedUtc = DateTime.UtcNow, Models = models };
            _cache[provider.ToString()] = entry;
            SaveCache();
            ActivityLog.Info("ModelCatalog", $"{provider}: {models.Count} models fetched");
            return new ModelList(models, entry.FetchedUtc, FromFallback: false);
        }
        catch (AiException ex) when (!refresh)
        {
            ActivityLog.Warn("ModelCatalog", $"{provider}: fetch failed ({ex.Kind}); using {(Cached(provider) is null ? "fallback" : "cache")}");
            return Cached(provider) ?? Fallback(provider);
        }
    }

    // ── Vendor calls ──────────────────────────────────────────────────────────

    internal async Task<List<ModelInfo>> FetchAsync(AiProviderType provider, string key, CancellationToken ct)
    {
        var list = provider switch
        {
            AiProviderType.Claude  => await FetchClaudeAsync(key, ct),
            AiProviderType.Gemini  => await FetchGeminiAsync(key, ct),
            AiProviderType.OpenAi  => await FetchOpenAiShapedAsync(provider, "OpenAI",  "https://api.openai.com/v1/models",  key, OpenAiChatFilter, ct),
            AiProviderType.Mistral => await FetchOpenAiShapedAsync(provider, "Mistral", "https://api.mistral.ai/v1/models",  key, MistralChatFilter, ct),
            AiProviderType.XAi     => await FetchOpenAiShapedAsync(provider, "xAI",     "https://api.x.ai/v1/models",        key, _ => true, ct),
            _ => throw new ArgumentException($"Unknown provider: {provider}", nameof(provider))
        };
        return list.OrderByDescending(m => m.Created ?? DateTime.MinValue).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
    }

    private async Task<List<ModelInfo>> FetchClaudeAsync(string key, CancellationToken ct)
    {
        var result = new List<ModelInfo>();
        string? after = null;
        for (int page = 0; page < 20; page++)
        {
            var url = "https://api.anthropic.com/v1/models?limit=1000" + (after is null ? "" : $"&after_id={Uri.EscapeDataString(after)}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-api-key", key);
            req.Headers.Add("anthropic-version", ClaudeProvider.AnthropicVersion);
            using var doc = await GetJsonAsync(AiProviderType.Claude, "Anthropic", req, ct);

            foreach (var m in Arr(doc.RootElement, "data"))
            {
                var id = ProviderHttp.Str(m, "id");
                if (string.IsNullOrEmpty(id)) continue;
                result.Add(new ModelInfo(id, ProviderHttp.Str(m, "display_name") ?? id, ParseDate(ProviderHttp.Str(m, "created_at"))));
            }
            var hasMore = doc.RootElement.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
            after = ProviderHttp.Str(doc.RootElement, "last_id");
            if (!hasMore || after is null) break;
        }
        return result;
    }

    private async Task<List<ModelInfo>> FetchGeminiAsync(string key, CancellationToken ct)
    {
        var result = new List<ModelInfo>();
        string? token = null;
        for (int page = 0; page < 20; page++)
        {
            var url = GeminiProvider.BaseUrl + "models?pageSize=1000" + (token is null ? "" : $"&pageToken={Uri.EscapeDataString(token)}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-goog-api-key", key);
            using var doc = await GetJsonAsync(AiProviderType.Gemini, "Gemini", req, ct);

            foreach (var m in Arr(doc.RootElement, "models"))
            {
                var name = ProviderHttp.Str(m, "name");
                if (string.IsNullOrEmpty(name)) continue;
                var methods = ProviderHttp.Obj(m, "supportedGenerationMethods");
                var chat = methods.ValueKind != JsonValueKind.Array ||
                           methods.EnumerateArray().Any(x => x.GetString() == "generateContent");
                if (!chat) continue;
                result.Add(new ModelInfo(name, ProviderHttp.Str(m, "displayName") ?? name, null));
            }
            token = ProviderHttp.Str(doc.RootElement, "nextPageToken");
            if (string.IsNullOrEmpty(token)) break;
        }
        return result;
    }

    private async Task<List<ModelInfo>> FetchOpenAiShapedAsync(
        AiProviderType provider, string vendor, string url, string key, Func<JsonElement, bool> filter, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"Bearer {key}");
        using var doc = await GetJsonAsync(provider, vendor, req, ct);

        var result = new List<ModelInfo>();
        foreach (var m in Arr(doc.RootElement, "data"))
        {
            var id = ProviderHttp.Str(m, "id");
            if (string.IsNullOrEmpty(id) || !filter(m)) continue;
            DateTime? created = m.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(c.GetInt64()).UtcDateTime : null;
            result.Add(new ModelInfo(id, ProviderHttp.Str(m, "name") ?? id, created));
        }
        return result;
    }

    /// <summary>OpenAI lists everything (embeddings, audio, images…); keep the chat models.</summary>
    internal static bool OpenAiChatFilter(JsonElement m)
    {
        var id = ProviderHttp.Str(m, "id") ?? string.Empty;
        var chatFamily = id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
                         (id.Length > 1 && id[0] == 'o' && char.IsDigit(id[1]));
        if (!chatFamily) return false;
        string[] notChat = { "realtime", "audio", "tts", "transcribe", "embedding", "image", "search", "instruct", "moderation", "whisper", "dall-e" };
        return !notChat.Any(x => id.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool MistralChatFilter(JsonElement m)
    {
        var caps = ProviderHttp.Obj(m, "capabilities");
        if (caps.ValueKind != JsonValueKind.Object) return true;
        return !caps.TryGetProperty("completion_chat", out var v) || v.ValueKind != JsonValueKind.False;
    }

    private async Task<JsonDocument> GetJsonAsync(AiProviderType provider, string vendor, HttpRequestMessage req, CancellationToken ct)
    {
        var context = $"{vendor}/models";
        using var response = await ProviderHttp.SendAsync(_http, provider, context, req, ct);
        await ProviderHttp.ThrowIfFailed(provider, context, vendor, response, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        try { return JsonDocument.Parse(body); }
        catch (JsonException ex)
        {
            throw new AiException(AiErrorKind.ProviderError, provider, $"{vendor} returned an unreadable model list.", ex);
        }
    }

    private static IEnumerable<JsonElement> Arr(JsonElement root, string name)
    {
        var a = ProviderHttp.Obj(root, name);
        return a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    // ── Cache file ────────────────────────────────────────────────────────────

    private sealed class CacheEntry
    {
        public DateTime FetchedUtc { get; set; }
        public List<ModelInfo> Models { get; set; } = new();
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(Path)) return;
            var dict = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(Path, Encoding.UTF8), Json);
            if (dict is not null) _cache = new Dictionary<string, CacheEntry>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            ActivityLog.Error("ModelCatalog", $"Could not read {Path}; ignoring cache", ex);
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache, Json), Encoding.UTF8);
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception ex)
        {
            ActivityLog.Error("ModelCatalog", $"Could not write {Path}", ex);
        }
    }
}
