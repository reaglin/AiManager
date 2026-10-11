using System.Net.Http.Headers;
using System.Text.Json;

namespace Eaglin.AiManager.Providers;

/// <summary>
/// Contract every speech-to-text wrapper satisfies (1.5.0): an audio file in, text with timings
/// out. Each wraps its vendor's REST API directly. Apps go through <see cref="AiHub.TranscribeAsync"/>.
/// </summary>
public interface ISpeechToText
{
    AiProviderType Type { get; }
    string Model { get; }

    /// <summary>
    /// Sends the file and returns what was said. Throws <see cref="AiException"/> for every failure
    /// the caller can act on, and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    Task<SpeechResult> TranscribeAsync(string audioPath, string? language, string? prompt, bool words, CancellationToken ct);
}

public sealed record SpeechResult(string Text, IReadOnlyList<AiTimedText> Segments, IReadOnlyList<AiTimedText> Words,
                                  string? Language, double AudioSeconds);

/// <summary>Shared plumbing for the OpenAI-shaped <c>/audio/transcriptions</c> endpoints.</summary>
internal static class Transcriptions
{
    public static async Task<SpeechResult> SendAsync(HttpClient http, AiProviderType provider, string vendor, string url,
        string apiKey, string audioPath, IEnumerable<(string Name, string Value)> fields, CancellationToken ct)
    {
        var context = $"{vendor} transcribe";
        await using var file = File.OpenRead(audioPath);
        using var form = new MultipartFormDataContent();
        foreach (var (name, value) in fields) form.Add(new StringContent(value), name);
        var audio = new StreamContent(file);
        audio.Headers.ContentType = new MediaTypeHeaderValue(MediaType(audioPath));
        form.Add(audio, "file", Path.GetFileName(audioPath));

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        ActivityLog.AiCall(context, $"REQUEST  file={Path.GetFileName(audioPath)}  bytes={file.Length}");

        using var response = await ProviderHttp.SendAsync(http, provider, context, request, ct);
        await ProviderHttp.ThrowIfFailed(provider, context, vendor, response, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        SpeechResult result;
        try
        {
            using var doc = JsonDocument.Parse(body);
            result = Parse(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new AiException(AiErrorKind.EmptyResponse, provider, $"{vendor} sent back something that is not a transcript.", ex);
        }
        if (result.Text.Length == 0 && result.Segments.Count == 0)
            throw new AiException(AiErrorKind.EmptyResponse, provider, $"{vendor} heard no speech in the audio.");
        ActivityLog.AiCall(context, $"RESPONSE  seconds={result.AudioSeconds:0.0}  segments={result.Segments.Count}  words={result.Words.Count}");
        return result;
    }

    /// <summary>Reads {text, language, duration, segments[], words[], usage.prompt_audio_seconds}; every part optional but text.</summary>
    public static SpeechResult Parse(JsonElement root)
    {
        var text     = (ProviderHttp.Str(root, "text") ?? string.Empty).Trim();
        var segments = Timed(ProviderHttp.Obj(root, "segments"));
        var words    = Timed(ProviderHttp.Obj(root, "words"));
        var seconds  = Num(root, "duration")
                    ?? Num(ProviderHttp.Obj(root, "usage"), "prompt_audio_seconds")
                    ?? (segments.Count > 0 ? segments[^1].End : words.Count > 0 ? words[^1].End : 0);
        return new SpeechResult(text, segments, words, ProviderHttp.Str(root, "language"), seconds);
    }

    private static List<AiTimedText> Timed(JsonElement array)
    {
        var list = new List<AiTimedText>();
        if (array.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in array.EnumerateArray())
        {
            // OpenAI names a word's text "word"; segments (and Mistral) use "text"
            var t = (ProviderHttp.Str(e, "text") ?? ProviderHttp.Str(e, "word") ?? string.Empty).Trim();
            if (t.Length == 0 || Num(e, "start") is not { } s || Num(e, "end") is not { } en) continue;
            list.Add(new AiTimedText(s, en, t));
        }
        return list;
    }

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3"  => "audio/mpeg",
        ".m4a"  => "audio/mp4",
        ".mp4"  => "audio/mp4",
        ".wav"  => "audio/wav",
        ".ogg"  => "audio/ogg",
        ".webm" => "audio/webm",
        ".flac" => "audio/flac",
        _       => "application/octet-stream"
    };
}

/// <summary>OpenAI <c>/v1/audio/transcriptions</c>. whisper-1 is the model that returns word and segment times (verbose_json).</summary>
public sealed class OpenAiSpeechToText : ISpeechToText
{
    public AiProviderType Type => AiProviderType.OpenAi;
    public string Model { get; }
    private readonly string _apiKey;
    private readonly HttpClient _http;
    internal const string ApiUrl = "https://api.openai.com/v1/audio/transcriptions";

    public OpenAiSpeechToText(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    public Task<SpeechResult> TranscribeAsync(string audioPath, string? language, string? prompt, bool words, CancellationToken ct)
    {
        var fields = new List<(string, string)> { ("model", Model), ("response_format", "verbose_json"), ("timestamp_granularities[]", "segment") };
        if (words) fields.Add(("timestamp_granularities[]", "word"));
        if (!string.IsNullOrWhiteSpace(language)) fields.Add(("language", language.Trim()));
        if (!string.IsNullOrWhiteSpace(prompt)) fields.Add(("prompt", prompt.Trim()));
        return Transcriptions.SendAsync(_http, Type, "OpenAI", ApiUrl, _apiKey, audioPath, fields, ct);
    }
}

/// <summary>
/// Mistral <c>/v1/audio/transcriptions</c> (Voxtral). Asks for segment times only: Mistral's docs
/// show one granularity at a time and say timestamps cannot be combined with <c>language</c>, so the
/// language is left to detection whenever times are wanted (always, here).
/// </summary>
public sealed class MistralSpeechToText : ISpeechToText
{
    public AiProviderType Type => AiProviderType.Mistral;
    public string Model { get; }
    private readonly string _apiKey;
    private readonly HttpClient _http;
    internal const string ApiUrl = "https://api.mistral.ai/v1/audio/transcriptions";

    public MistralSpeechToText(string apiKey, string model, HttpClient? http = null)
    { _apiKey = apiKey; Model = model; _http = http ?? ProviderHttp.DefaultClient; }

    public Task<SpeechResult> TranscribeAsync(string audioPath, string? language, string? prompt, bool words, CancellationToken ct)
    {
        var fields = new List<(string, string)> { ("model", Model), ("timestamp_granularities", "segment") };
        return Transcriptions.SendAsync(_http, Type, "Mistral", ApiUrl, _apiKey, audioPath, fields, ct);
    }
}
