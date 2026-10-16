using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eaglin.AiManager.Providers;

/// <summary>
/// Contract every picture-making wrapper satisfies (1.7.0): a description in, pictures out. Each wraps
/// its vendor's REST API directly. Apps go through <see cref="AiHub.GenerateImagesAsync"/>.
///
/// Read from each vendor's documentation on 2026-10-16 (re-check before each release —
/// docs/PROVIDER-KEY-HELP.md and docs/PLAN.md §15):
///   OpenAI  POST /v1/images/generations — model, prompt, n, size (WxH, multiples of 16), quality
///           low|medium|high, output_format; answer data[].b64_json. A refused picture: error code
///           "moderation_blocked". An organisation may need "API Organization Verification" first.
///   Gemini  POST /v1beta/interactions — model, input, response_format {type:"image", mime_type,
///           aspect_ratio, image_size "1K"|"2K"}; the picture is a content block {type:"image", data}.
///           The older models/{m}:generateContent with responseModalities ["IMAGE"] is tried if the
///           interactions endpoint is not found. No free tier for pictures. SynthID in every picture.
///   xAI     POST /v1/images/generations — model, prompt, n (1–10), aspect_ratio, resolution 1k|2k,
///           quality low|medium (grok-imagine-image-2.0 only), response_format b64_json|url.
/// </summary>
public interface IImageGenerator
{
    AiProviderType Type { get; }
    string Model { get; }

    /// <summary>A mark the vendor puts in every picture, or null.</summary>
    string? Watermark { get; }

    /// <summary>
    /// Makes the pictures. Throws <see cref="AiException"/> for every failure the caller can act on
    /// (a refusal is <see cref="AiErrorKind.Refused"/>), <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    Task<IReadOnlyList<AiGeneratedImage>> GenerateAsync(AiImageRequest request, CancellationToken ct);
}

/// <summary>Shared plumbing for the picture APIs: sending, reading pictures, and saying what went wrong.</summary>
internal static class ImageApi
{
    public static async Task<string> PostJsonAsync(HttpClient http, AiProviderType provider, string vendor, string url,
        JsonObject body, Action<HttpRequestMessage> auth, CancellationToken ct, bool allowNotFound = false)
    {
        var context = $"{vendor} picture";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        auth(request);
        ActivityLog.AiCall(context, $"REQUEST  {url}  prompt_chars={(body["prompt"] ?? body["input"])?.ToString().Length ?? 0}");

        using var response = await ProviderHttp.SendAsync(http, provider, context, request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return string.Empty;
        if (!response.IsSuccessStatusCode)
        {
            var detail = ProviderHttp.TryExtractErrorMessage(text);
            if (Explain(provider, vendor, (int)response.StatusCode, text, detail) is { } plain)
            {
                ActivityLog.Error(context, $"HTTP {(int)response.StatusCode}: {detail}");
                throw plain;
            }
            await ProviderHttp.ThrowIfFailed(provider, context, vendor, response, ct);
        }
        return text;
    }

    /// <summary>The failures a picture call has that a text call does not, in words a person can act on.</summary>
    internal static AiException? Explain(AiProviderType provider, string vendor, int status, string body, string detail)
    {
        var all = (body + " " + detail).ToLowerInvariant();

        if (all.Contains("moderation_blocked") || all.Contains("safety system") || all.Contains("content policy")
            || all.Contains("content_policy") || all.Contains("prohibited") || all.Contains("moderation"))
            return Refused(provider, vendor, status);

        if (provider == AiProviderType.OpenAi && (status is 401 or 403) && all.Contains("verif"))
            return new AiException(AiErrorKind.ProviderError, provider,
                "This OpenAI key cannot make pictures yet: OpenAI asks for the organisation to be verified first. " +
                "Go to platform.openai.com → Settings → Organization → General → Verify Organization; pictures work " +
                $"a few minutes after that. (OpenAI said: {detail})", statusCode: status);

        if (provider == AiProviderType.Gemini && (all.Contains("free tier") || all.Contains("free_tier") || all.Contains("billing")
            || all.Contains("limit: 0")))
            return new AiException(AiErrorKind.ProviderError, provider,
                "This Google Gemini key cannot make pictures yet: Google's picture models have no free tier. Turn on " +
                "billing for the key's project (aistudio.google.com → Get API key → Set up billing), then try again. " +
                $"(Google said: {detail})", statusCode: status);

        return null;
    }

    public static AiException Refused(AiProviderType provider, string vendor, int? status = null) =>
        new(AiErrorKind.Refused, provider,
            $"{vendor} would not make this picture: its safety rules refused the request. Try different words — " +
            "for example, leave out people's names, brand names, or anything violent.", statusCode: status);

    /// <summary>Reads "data": [ {b64_json | url, revised_prompt} ] — the OpenAI-shaped answer (OpenAI, xAI). A url is downloaded at once (it may expire).</summary>
    public static async Task<List<AiGeneratedImage>> ReadDataArrayAsync(HttpClient http, AiProviderType provider, string vendor,
        string json, CancellationToken ct)
    {
        var list = new List<AiGeneratedImage>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new AiException(AiErrorKind.EmptyResponse, provider, $"{vendor} sent back something that is not a picture.", ex); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("respect_moderation", out var rm)
                && rm.ValueKind == JsonValueKind.False)
                throw Refused(provider, vendor);
            var data = ProviderHttp.Obj(root, "data");
            if (data.ValueKind == JsonValueKind.Array)
                foreach (var e in data.EnumerateArray())
                {
                    if (e.TryGetProperty("respect_moderation", out var em) && em.ValueKind == JsonValueKind.False) continue;
                    var revised = ProviderHttp.Str(e, "revised_prompt");
                    byte[]? bytes = null;
                    if (ProviderHttp.Str(e, "b64_json") is { Length: > 0 } b64) bytes = Convert.FromBase64String(b64);
                    else if (ProviderHttp.Str(e, "url") is { Length: > 0 } url) bytes = await DownloadAsync(http, provider, vendor, url, ct);
                    if (bytes is { Length: > 0 }) list.Add(Picture(bytes, revised));
                }
        }
        if (list.Count == 0)
        {
            if (json.Contains("moderation", StringComparison.OrdinalIgnoreCase)) throw Refused(provider, vendor);
            throw new AiException(AiErrorKind.EmptyResponse, provider, $"{vendor} sent back no picture. Try again, or try different words.");
        }
        return list;
    }

    public static async Task<byte[]> DownloadAsync(HttpClient http, AiProviderType provider, string vendor, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await ProviderHttp.SendAsync(http, provider, $"{vendor} picture download", request, ct);
        if (!response.IsSuccessStatusCode)
            throw new AiException(AiErrorKind.ProviderError, provider,
                $"{vendor} made the picture but it could not be downloaded ({(int)response.StatusCode}). Try again.");
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>A picture from its bytes: the type from its first bytes (not from what the vendor says), and its size where the header tells it.</summary>
    public static AiGeneratedImage Picture(byte[] bytes, string? revised)
    {
        var (type, w, h) = Sniff(bytes);
        return new AiGeneratedImage(bytes, type, w, h, string.IsNullOrWhiteSpace(revised) ? null : revised);
    }

    /// <summary>PNG, JPEG, WebP or GIF from the magic bytes, with the width and height where the header holds them (0 when not found).</summary>
    internal static (string MediaType, int Width, int Height) Sniff(byte[] b)
    {
        if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            return ("image/png", BigEndian(b, 16), BigEndian(b, 20));
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            // Walk the JPEG markers to the first start-of-frame, which holds the size.
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                    return ("image/jpeg", (b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                var len = (b[i + 2] << 8) | b[i + 3];
                if (len < 2) break;
                i += 2 + len;
            }
            return ("image/jpeg", 0, 0);
        }
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
            return ("image/webp", 0, 0);
        if (b.Length >= 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
            return ("image/gif", b[6] | (b[7] << 8), b[8] | (b[9] << 8));
        return ("image/png", 0, 0);
    }

    private static int BigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    /// <summary>"16:9" for the ratio-based APIs (Gemini, xAI).</summary>
    public static string Ratio(AiImageShape shape) => shape switch
    {
        AiImageShape.Wide16x9     => "16:9",
        AiImageShape.Landscape3x2 => "3:2",
        AiImageShape.Portrait     => "2:3",
        _                         => "1:1"
    };
}

/// <summary>OpenAI <c>/v1/images/generations</c> (the gpt-image models).</summary>
public sealed class OpenAiImageGenerator : IImageGenerator
{
    public AiProviderType Type => AiProviderType.OpenAi;
    public string Model { get; }
    public string? Watermark => null;   // C2PA was added for DALL-E 3; not confirmed for gpt-image (2026-10-16)
    private readonly string _apiKey;
    private readonly HttpClient _http;
    internal const string ApiUrl = "https://api.openai.com/v1/images/generations";

    public OpenAiImageGenerator(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    /// <summary>Pixel sizes: both sides multiples of 16, as the gpt-image models require.</summary>
    internal static string Size(AiImageShape shape) => shape switch
    {
        AiImageShape.Wide16x9     => "1536x864",
        AiImageShape.Landscape3x2 => "1536x1024",
        AiImageShape.Portrait     => "1024x1536",
        _                         => "1024x1024"
    };

    internal static string QualityWord(AiImageQuality q) => q switch
    {
        AiImageQuality.Draft => "low",
        AiImageQuality.Best  => "high",
        _                    => "medium"
    };

    internal JsonObject Body(AiImageRequest r) => new()
    {
        ["model"]         = Model,
        ["prompt"]        = r.FullPrompt,
        ["n"]             = Math.Clamp(r.Count, 1, AiHub.MaxImagesPerRequest),
        ["size"]          = Size(r.Shape),
        ["quality"]       = QualityWord(r.Quality),
        ["output_format"] = "png"
    };

    public async Task<IReadOnlyList<AiGeneratedImage>> GenerateAsync(AiImageRequest request, CancellationToken ct)
    {
        var json = await ImageApi.PostJsonAsync(_http, Type, "OpenAI", ApiUrl, Body(request),
            m => m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey), ct);
        return await ImageApi.ReadDataArrayAsync(_http, Type, "OpenAI", json, ct);
    }
}

/// <summary>xAI <c>/v1/images/generations</c> (Grok Imagine).</summary>
public sealed class XAiImageGenerator : IImageGenerator
{
    public AiProviderType Type => AiProviderType.XAi;
    public string Model { get; }
    public string? Watermark => null;   // not stated by xAI (2026-10-16)
    private readonly string _apiKey;
    private readonly HttpClient _http;
    internal const string ApiUrl = "https://api.x.ai/v1/images/generations";

    public XAiImageGenerator(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    internal JsonObject Body(AiImageRequest r)
    {
        var body = new JsonObject
        {
            ["model"]           = Model,
            ["prompt"]          = r.FullPrompt,
            ["n"]               = Math.Clamp(r.Count, 1, AiHub.MaxImagesPerRequest),
            ["aspect_ratio"]    = ImageApi.Ratio(r.Shape),
            ["resolution"]      = r.Quality == AiImageQuality.Best ? "2k" : "1k",
            ["response_format"] = "b64_json"   // a url may expire
        };
        // "quality" is only taken by grok-imagine-image-2.0.
        if (ModelIds.StartsWith(Model, "grok-imagine-image-2"))
            body["quality"] = r.Quality == AiImageQuality.Draft ? "low" : "medium";
        return body;
    }

    public async Task<IReadOnlyList<AiGeneratedImage>> GenerateAsync(AiImageRequest request, CancellationToken ct)
    {
        var json = await ImageApi.PostJsonAsync(_http, Type, "xAI", ApiUrl, Body(request),
            m => m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey), ct);
        return await ImageApi.ReadDataArrayAsync(_http, Type, "xAI", json, ct);
    }
}

/// <summary>
/// Google Gemini (the "Nano Banana" image models). One picture per call, so several pictures are
/// several calls. The interactions endpoint first; the older generateContent if it is not found.
/// </summary>
public sealed class GeminiImageGenerator : IImageGenerator
{
    public AiProviderType Type => AiProviderType.Gemini;
    public string Model { get; }
    public string? Watermark => "SynthID";   // "All generated images include a SynthID watermark" (2026-10-16)
    private readonly string _apiKey;
    private readonly HttpClient _http;
    internal const string InteractionsUrl = "https://generativelanguage.googleapis.com/v1beta/interactions";
    internal static string GenerateContentUrl(string model) =>
        $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";

    public GeminiImageGenerator(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    internal static string ImageSize(AiImageQuality q) => q == AiImageQuality.Best ? "2K" : "1K";

    internal JsonObject InteractionsBody(AiImageRequest r) => new()
    {
        ["model"] = Model,
        ["input"] = r.FullPrompt,
        ["response_format"] = new JsonObject
        {
            ["type"]         = "image",
            ["mime_type"]    = "image/png",
            ["aspect_ratio"] = ImageApi.Ratio(r.Shape),
            ["image_size"]   = ImageSize(r.Quality)
        }
    };

    internal static JsonObject GenerateContentBody(AiImageRequest r) => new()
    {
        ["contents"] = new JsonArray(new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = r.FullPrompt }) }),
        ["generationConfig"] = new JsonObject
        {
            ["responseModalities"] = new JsonArray("IMAGE"),
            ["imageConfig"] = new JsonObject { ["aspectRatio"] = ImageApi.Ratio(r.Shape), ["imageSize"] = ImageSize(r.Quality) }
        }
    };

    public async Task<IReadOnlyList<AiGeneratedImage>> GenerateAsync(AiImageRequest request, CancellationToken ct)
    {
        var list = new List<AiGeneratedImage>();
        int count = Math.Clamp(request.Count, 1, AiHub.MaxImagesPerRequest);
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            void Auth(HttpRequestMessage m) => m.Headers.Add("x-goog-api-key", _apiKey);
            var json = await ImageApi.PostJsonAsync(_http, Type, "Google Gemini", InteractionsUrl, InteractionsBody(request), Auth, ct,
                allowNotFound: true);
            if (json.Length == 0)
                json = await ImageApi.PostJsonAsync(_http, Type, "Google Gemini", GenerateContentUrl(Model), GenerateContentBody(request), Auth, ct);
            list.AddRange(ReadPictures(json));
        }
        return list;
    }

    /// <summary>
    /// Finds the pictures in either answer: a content block {type:"image", data} (interactions), an
    /// "inlineData"/"inline_data" {mimeType, data} part (generateContent), or an "output_image" {data}.
    /// A blocked request (finishReason SAFETY / PROHIBITED_CONTENT / IMAGE_SAFETY) is a refusal.
    /// </summary>
    internal List<AiGeneratedImage> ReadPictures(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new AiException(AiErrorKind.EmptyResponse, Type, "Google Gemini sent back something that is not a picture.", ex); }

        var found = new List<AiGeneratedImage>();
        var seen  = new HashSet<string>();
        string? text = null;
        Walk(root, null);
        if (found.Count > 0) return found;

        var lower = json.ToLowerInvariant();
        if (lower.Contains("safety") || lower.Contains("prohibited") || lower.Contains("blocked"))
            throw ImageApi.Refused(Type, "Google Gemini");
        throw new AiException(AiErrorKind.EmptyResponse, Type,
            "Google Gemini sent back no picture" + (string.IsNullOrWhiteSpace(text) ? "." : $": “{Trim(text!)}”") +
            " Try again, or try different words.");

        void Walk(JsonNode? node, string? name)
        {
            switch (node)
            {
                case JsonObject o:
                    var data = o["data"] is JsonValue dv && dv.TryGetValue(out string? d) ? d : null;
                    var mime = (o["mime_type"] ?? o["mimeType"]) is JsonValue mv && mv.TryGetValue(out string? m) ? m : null;
                    var type = o["type"] is JsonValue tv && tv.TryGetValue(out string? t) ? t : null;
                    bool picture = data is { Length: > 16 } &&
                                   (type == "image" || (mime?.StartsWith("image/") ?? false) ||
                                    (name?.Contains("image", StringComparison.OrdinalIgnoreCase) ?? false) ||
                                    (name?.Contains("inline", StringComparison.OrdinalIgnoreCase) ?? false));
                    if (picture && seen.Add(data!))
                    {
                        try { found.Add(ImageApi.Picture(Convert.FromBase64String(data!), null)); }
                        catch (FormatException) { /* not base64 — not a picture */ }
                        return;
                    }
                    if (o["text"] is JsonValue txt && txt.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s)) text ??= s;
                    foreach (var (k, v) in o) Walk(v, k);
                    break;
                case JsonArray a:
                    foreach (var v in a) Walk(v, name);
                    break;
            }
        }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;
}

/// <summary>Model-id prefix matching shared by the generators and the price table.</summary>
internal static class ModelIds
{
    public static bool StartsWith(string model, string prefix) =>
        Catalog.Pricing.Normalize(model).StartsWith(prefix, StringComparison.Ordinal);
}
