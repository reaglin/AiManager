using System.Net;
using Eaglin.AiManager.Catalog;
using Eaglin.AiManager.Providers;

namespace Eaglin.AiManager.Tests;

public class SpeechTests
{
    private const string OpenAiBody =
        "{ \"text\": \" So I was racing. \", \"language\": \"english\", \"duration\": 120.0," +
        "  \"segments\": [ { \"start\": 1.48, \"end\": 3.2, \"text\": \" So I was racing. \" } ]," +
        "  \"words\": [ { \"word\": \"So\", \"start\": 1.48, \"end\": 1.7 }, { \"word\": \"I\", \"start\": 1.7, \"end\": 1.8 }," +
        "               { \"word\": \"racing\", \"start\": 2.4, \"end\": 3.2 } ] }";

    private static string Audio(TempRoot t, long bytes = 1000)
    {
        var path = Path.Combine(t.Path, "story.mp3");
        using (var f = File.Create(path)) f.SetLength(bytes);
        return path;
    }

    private static AiHub HubWithKeys(TempRoot t, params AiProviderType[] providers)
    {
        var hub = AiHub.Open("TestApp", t.Path);
        foreach (var p in providers) hub.Credentials.SetKey(p, $"key-{p}", "test");
        hub.Credentials.Save();
        return hub;
    }

    [Fact]
    public async Task OpenAi_sends_whisper_1_with_verbose_json_and_both_granularities_and_reads_words_and_segments()
    {
        using var t = new TempRoot();
        var h = new FakeHandler(HttpStatusCode.OK, OpenAiBody);
        var r = await new OpenAiSpeechToText("sk", "whisper-1", h.Client())
            .TranscribeAsync(Audio(t), "en", "Myakka, Manny Otero", words: true, CancellationToken.None);

        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", h.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer sk", h.LastRequest.Headers.Authorization!.ToString());
        var body = h.LastBody!;
        Assert.Contains("multipart/form-data", h.LastRequest.Content!.Headers.ContentType!.ToString());
        Assert.Contains("whisper-1", body);
        Assert.Contains("verbose_json", body);
        Assert.Equal(2, body.Split("timestamp_granularities[]").Length - 1);
        Assert.Contains("segment", body);
        Assert.Contains("word", body);
        Assert.Contains("Myakka, Manny Otero", body);
        Assert.Contains("filename=story.mp3", body);

        Assert.Equal("So I was racing.", r.Text);
        Assert.Equal(new AiTimedText(1.48, 3.2, "So I was racing."), Assert.Single(r.Segments));
        Assert.Equal(3, r.Words.Count);
        Assert.Equal(new AiTimedText(2.4, 3.2, "racing"), r.Words[2]);
        Assert.Equal(120.0, r.AudioSeconds);
        Assert.Equal("english", r.Language);
    }

    [Fact]
    public async Task Mistral_asks_for_segments_without_a_language_and_reads_usage_seconds()
    {
        using var t = new TempRoot();
        var h = new FakeHandler(HttpStatusCode.OK,
            "{ \"model\": \"voxtral-mini-latest\", \"text\": \"Deep Hole.\", \"language\": \"en\"," +
            "  \"segments\": [ { \"text\": \" Deep Hole. \", \"start\": 31.05, \"end\": 31.82 } ]," +
            "  \"usage\": { \"prompt_audio_seconds\": 101, \"total_tokens\": 900 } }");
        var r = await new MistralSpeechToText("mk", "voxtral-mini-latest", h.Client())
            .TranscribeAsync(Audio(t), "en", null, words: true, CancellationToken.None);

        Assert.Equal("https://api.mistral.ai/v1/audio/transcriptions", h.LastRequest!.RequestUri!.ToString());
        Assert.Contains("voxtral-mini-latest", h.LastBody);
        Assert.Contains("segment", h.LastBody);
        Assert.DoesNotContain("language", h.LastBody);   // Mistral: timestamps cannot be combined with a language
        Assert.Equal(new AiTimedText(31.05, 31.82, "Deep Hole."), Assert.Single(r.Segments));
        Assert.Empty(r.Words);
        Assert.Equal(101, r.AudioSeconds);
    }

    [Fact]
    public void Seconds_fall_back_to_the_last_segment_end()
    {
        var r = Transcriptions.Parse(System.Text.Json.JsonDocument.Parse(
            "{ \"text\": \"a\", \"segments\": [ { \"text\": \"a\", \"start\": 0, \"end\": 4.5 } ] }").RootElement);
        Assert.Equal(4.5, r.AudioSeconds);
    }

    [Fact]
    public async Task A_rejected_key_is_an_Auth_error()
    {
        using var t = new TempRoot();
        var h = new FakeHandler(HttpStatusCode.Unauthorized, "{ \"error\": { \"message\": \"bad key\" } }");
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            new OpenAiSpeechToText("sk", "whisper-1", h.Client()).TranscribeAsync(Audio(t), null, null, true, CancellationToken.None));
        Assert.Equal(AiErrorKind.Auth, ex.Kind);
    }

    [Fact]
    public async Task A_file_over_the_limit_is_refused_before_anything_is_sent()
    {
        using var t = new TempRoot();
        var hub = HubWithKeys(t, AiProviderType.OpenAi);
        var called = false;
        hub.SpeechFactory = (p, k, m) => { called = true; return new FakeSpeech(p, m); };
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            hub.TranscribeAsync(new AiTranscriptionRequest { AudioPath = Audio(t, AiHub.MaxTranscriptionBytes + 1) }));
        Assert.Equal(AiErrorKind.ProviderError, ex.Kind);
        Assert.Contains("too large", ex.Message);
        Assert.False(called);
    }

    [Fact]
    public async Task The_default_provider_transcribes_when_it_can_otherwise_one_with_a_key()
    {
        using var t = new TempRoot();
        var hub = HubWithKeys(t, AiProviderType.Claude, AiProviderType.Mistral, AiProviderType.OpenAi);
        var used = new List<(AiProviderType, string)>();
        hub.SpeechFactory = (p, k, m) => { used.Add((p, m)); return new FakeSpeech(p, m); };

        // Claude is the default and cannot transcribe: the first transcription provider with a key, OpenAI
        await hub.TranscribeAsync(new AiTranscriptionRequest { AudioPath = Audio(t) });
        Assert.Equal((AiProviderType.OpenAi, "whisper-1"), used[^1]);

        hub.Settings.DefaultProvider = AiProviderType.Mistral;
        await hub.TranscribeAsync(new AiTranscriptionRequest { AudioPath = Audio(t) });
        Assert.Equal((AiProviderType.Mistral, "voxtral-mini-latest"), used[^1]);

        await hub.TranscribeAsync(new AiTranscriptionRequest { AudioPath = Audio(t), Provider = AiProviderType.OpenAi, Model = "gpt-4o-transcribe" });
        Assert.Equal((AiProviderType.OpenAi, "gpt-4o-transcribe"), used[^1]);

        var ex = await Assert.ThrowsAsync<AiException>(() =>
            hub.TranscribeAsync(new AiTranscriptionRequest { AudioPath = Audio(t), Provider = AiProviderType.Claude }));
        Assert.Contains("cannot transcribe", ex.Message);
    }

    [Fact]
    public async Task Without_a_transcription_key_it_says_which_keys_would_do()
    {
        using var t = new TempRoot();
        var hub = HubWithKeys(t, AiProviderType.Claude);
        Assert.Empty(hub.TranscriptionProvidersWithKeys);
        var ex = await Assert.ThrowsAsync<AiException>(() => hub.TranscribeAsync(new AiTranscriptionRequest { AudioPath = Audio(t) }));
        Assert.Equal(AiErrorKind.NoKey, ex.Kind);
        Assert.Contains("OpenAI or Mistral AI", ex.Message);
    }

    [Fact]
    public async Task A_transcription_is_ledgered_with_its_seconds_and_cost_and_raises_Transcribed()
    {
        using var t = new TempRoot();
        var hub = HubWithKeys(t, AiProviderType.OpenAi);
        hub.SpeechFactory = (p, k, m) => new FakeSpeech(p, m) { Seconds = 120 };
        AiTranscript? seen = null;
        hub.Transcribed += x => seen = x;

        var r = await hub.TranscribeAsync(new AiTranscriptionRequest { AudioPath = Audio(t), Purpose = "story-transcript" });

        Assert.Equal(0.012, r.EstimatedCostUsd!.Value, 6);   // two minutes at $0.006
        Assert.Same(r, seen);
        Assert.Equal("TestApp", r.App);
        var rec = Assert.Single(hub.Usage.Read(DateTime.Now));
        Assert.Equal(120, rec.AudioSec);
        Assert.Equal(0.012, rec.CostUsd!.Value, 6);
        Assert.Equal("story-transcript", rec.Purpose);
        Assert.True(rec.Ok);
    }

    [Fact]
    public async Task A_cancelled_transcription_is_recorded_as_cancelled()
    {
        using var t = new TempRoot();
        var hub = HubWithKeys(t, AiProviderType.OpenAi);
        hub.SpeechFactory = (p, k, m) => new FakeSpeech(p, m) { Cancel = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hub.TranscribeAsync(new AiTranscriptionRequest { AudioPath = Audio(t) }));
        var rec = Assert.Single(hub.Usage.Read(DateTime.Now));
        Assert.True(rec.Cancelled);
        Assert.Null(rec.AudioSec);
    }

    [Fact]
    public void Per_minute_prices_match_by_prefix()
    {
        Assert.Equal(0.003, Pricing.EstimateAudio("voxtral-mini-2507", 60)!.Value, 6);
        Assert.Equal(0.003, Pricing.EstimateAudio("gpt-4o-mini-transcribe", 60)!.Value, 6);
        Assert.Equal(0.006, Pricing.EstimateAudio("gpt-4o-transcribe", 60)!.Value, 6);
        Assert.Null(Pricing.EstimateAudio("claude-opus-5", 60));
        Assert.True(AiHub.CanTranscribe(AiProviderType.Mistral));
        Assert.False(AiHub.CanTranscribe(AiProviderType.Gemini));
    }

    private sealed class FakeSpeech(AiProviderType type, string model) : ISpeechToText
    {
        public AiProviderType Type => type;
        public string Model => model;
        public double Seconds { get; init; } = 10;
        public bool Cancel { get; init; }

        public Task<SpeechResult> TranscribeAsync(string audioPath, string? language, string? prompt, bool words, CancellationToken ct)
        {
            if (Cancel) throw new OperationCanceledException();
            return Task.FromResult(new SpeechResult("hello", [new AiTimedText(0, 1, "hello")], [], "en", Seconds));
        }
    }
}
