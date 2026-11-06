using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eaglin.AiManager.Catalog;
using Eaglin.AiManager.Legacy;
using Eaglin.AiManager.Speech;
using Eaglin.AiManager.Store;
using Eaglin.AiManager.Usage;

namespace Eaglin.AiManager.Tests;

/// <summary>1.8.0: Google text-to-speech — credential, voices, chunking, joining, auth, errors, ledger.</summary>
public class TextToSpeechTests
{
    private const string VoicesBody =
        "{ \"voices\": [" +
        "  { \"languageCodes\": [\"en-US\"], \"name\": \"en-US-Chirp3-HD-Charon\", \"ssmlGender\": \"MALE\", \"naturalSampleRateHertz\": 24000 }," +
        "  { \"languageCodes\": [\"en-US\"], \"name\": \"en-US-Studio-O\", \"ssmlGender\": \"FEMALE\", \"naturalSampleRateHertz\": 24000 }," +
        "  { \"languageCodes\": [\"en-GB\"], \"name\": \"en-GB-Neural2-B\", \"ssmlGender\": \"MALE\", \"naturalSampleRateHertz\": 24000 }," +
        "  { \"languageCodes\": [\"en-US\"], \"name\": \"en-US-Wavenet-D\", \"ssmlGender\": \"MALE\" }," +
        "  { \"languageCodes\": [\"de-DE\"], \"name\": \"de-DE-Standard-A\", \"ssmlGender\": \"FEMALE\" }," +
        "  { \"languageCodes\": [\"en-US\"], \"name\": \"en-US-Polyglot-1\", \"ssmlGender\": \"MALE\" }," +
        "  { \"languageCodes\": [\"en-US\"], \"name\": \"Kore\", \"ssmlGender\": \"FEMALE\" }" +
        "] }";

    private static GoogleTtsCredential ApiKey(string key = "AIzaTestKey1234") =>
        new(GoogleTtsCredentialKind.ApiKey, key, null, null, DateTime.UtcNow, "test");

    private static string AudioBody(byte[] audio) => $"{{ \"audioContent\": \"{Convert.ToBase64String(audio)}\" }}";

    private static string ServiceAccountJson(RSA rsa, string email = "tts@proj.iam.gserviceaccount.com") =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account", ["project_id"] = "proj", ["private_key_id"] = "abc",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(), ["client_email"] = email,
            ["token_uri"] = "https://oauth2.googleapis.com/token"
        });

    private static AiHub HubWithTts(TempRoot t, FakeHandler h, string app = "Author+")
    {
        var hub = AiHub.Open(app, t.Path);
        hub.Credentials.SetGoogleTtsApiKey("AIzaTestKey1234", "test");
        hub.Credentials.Save();
        hub.TtsFactory = c => new GoogleTextToSpeech(c, h.Client());
        return hub;
    }

    // ── Credential ────────────────────────────────────────────────────────────

    [Fact]
    public void Credential_round_trips_api_key_and_service_account_and_keeps_the_provider_keys()
    {
        using var t = new TempRoot();
        using var rsa = RSA.Create(2048);
        var store = new CredentialStore(t.Path);
        store.SetKey(AiProviderType.Claude, "sk-ant-x", "test");
        store.SetGoogleTtsApiKey("  AIzaKey9876  ", "Author+");
        store.Save();

        var back = new CredentialStore(t.Path);
        Assert.Equal("sk-ant-x", back.KeyFor(AiProviderType.Claude));
        Assert.Equal(GoogleTtsCredentialKind.ApiKey, back.GoogleTts!.Kind);
        Assert.Equal("AIzaKey9876", back.GoogleTts.Secret);
        Assert.Equal("Author+", back.GoogleTts.UpdatedBy);
        Assert.DoesNotContain("AIzaKey9876", back.GoogleTts.ToString());
        Assert.Contains("…9876", back.GoogleTts.Describe());
        Assert.DoesNotContain(AiProviderType.GoogleTts, back.ProvidersWithKeys);
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("AIzaKey9876", File.ReadAllText(store.Path));

        back.SetGoogleTtsServiceAccount(ServiceAccountJson(rsa), "test");
        back.Save();
        var sa = new CredentialStore(t.Path).GoogleTts!;
        Assert.Equal(GoogleTtsCredentialKind.ServiceAccount, sa.Kind);
        Assert.Equal("proj", sa.ProjectId);
        Assert.Equal("tts@proj.iam.gserviceaccount.com", sa.ClientEmail);
        Assert.Contains("PRIVATE KEY", sa.Secret);

        var cleared = new CredentialStore(t.Path);
        cleared.ClearGoogleTts();
        cleared.Save();
        Assert.Null(new CredentialStore(t.Path).GoogleTts);
        Assert.Equal("sk-ant-x", new CredentialStore(t.Path).KeyFor(AiProviderType.Claude));
    }

    [Fact]
    public void A_file_that_is_not_a_service_account_is_refused_in_plain_words()
    {
        using var t = new TempRoot();
        var store = new CredentialStore(t.Path);
        var ex = Assert.Throws<ArgumentException>(() => store.SetGoogleTtsServiceAccount("{ \"type\": \"authorized_user\" }", "test"));
        Assert.Contains("service-account key", ex.Message);
        Assert.Throws<ArgumentException>(() => store.SetGoogleTtsServiceAccount("not json", "test"));
        Assert.Null(store.GoogleTts);
        Assert.Throws<ArgumentException>(() => store.SetKey(AiProviderType.GoogleTts, "x", "test"));
    }

    [Fact]
    public void GoogleTts_is_not_a_chat_provider()
    {
        Assert.DoesNotContain(AiProviderType.GoogleTts, AiProviderRouter.AllProviders);
        Assert.Equal("Google text-to-speech", AiHub.DisplayName(AiProviderType.GoogleTts));
    }

    [Fact]
    public void Legacy_import_copies_PreseMakers_service_account_only_when_none_is_stored()
    {
        using var t = new TempRoot();
        using var rsa = RSA.Create(2048);
        var saFile = Path.Combine(t.Path, "tts-sa.json");
        File.WriteAllText(saFile, ServiceAccountJson(rsa));
        var settings = Path.Combine(t.Path, "app_settings.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new { TtsCredentialsFilePath = saFile }));
        var paths = new LegacyPaths { PreseMaker = settings, AuthorPlus = Path.Combine(t.Path, "no1"), CiatleFce = Path.Combine(t.Path, "no2") };

        Assert.Equal(saFile, LegacyCredentials.FindGoogleTtsServiceAccount(paths));
        var store = new CredentialStore(t.Path);
        Assert.True(LegacyCredentials.ImportGoogleTts(store, paths));
        Assert.Equal(GoogleTtsCredentialKind.ServiceAccount, store.GoogleTts!.Kind);
        Assert.Equal("import:PreseMaker", store.GoogleTts.UpdatedBy);

        store.SetGoogleTtsApiKey("AIzaMine", "test");
        Assert.False(LegacyCredentials.ImportGoogleTts(store, paths));            // a stored credential is kept
        Assert.Equal("AIzaMine", store.GoogleTts!.Secret);

        File.WriteAllText(settings, JsonSerializer.Serialize(new { TtsCredentialsFilePath = Path.Combine(t.Path, "gone.json") }));
        Assert.Null(LegacyCredentials.FindGoogleTtsServiceAccount(paths));
    }

    // ── Voices ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("en-US-Chirp3-HD-Charon", "Chirp 3: HD", "Charon", 30.0)]
    [InlineData("en-US-Chirp-HD-F", "Chirp HD", "F", 30.0)]
    [InlineData("en-US-Studio-O", "Studio", "O", 160.0)]
    [InlineData("en-GB-Neural2-B", "Neural2", "B", 16.0)]
    [InlineData("en-US-Wavenet-D", "WaveNet", "D", 4.0)]
    [InlineData("de-DE-Standard-A", "Standard", "A", 4.0)]
    [InlineData("en-US-Polyglot-1", "Polyglot", "1", 16.0)]
    [InlineData("en-US-News-K", "News", "K", 16.0)]
    [InlineData("en-US-Casual-K", "Casual", "K", 16.0)]
    [InlineData("cmn-CN-Wavenet-A", "WaveNet", "A", 4.0)]
    public void Family_speaker_and_price_come_from_the_name(string name, string family, string speaker, double price)
    {
        Assert.Equal(family, GoogleVoiceNames.Family(name));
        Assert.Equal(speaker, GoogleVoiceNames.Speaker(name));
        Assert.Equal(price, AiHub.SpeechPricePerMillion(name));
    }

    [Fact]
    public void Unknown_voices_are_Other_with_no_price_and_markup_rules_follow_the_kind()
    {
        Assert.Equal("Other", GoogleVoiceNames.Family("Kore"));
        Assert.Null(Pricing.SpeechPricePerMillion("Kore"));
        Assert.Equal("", GoogleVoiceNames.Language("Kore"));
        Assert.Equal("cmn-CN", GoogleVoiceNames.Language("cmn-CN-Wavenet-A"));
        Assert.False(GoogleVoiceNames.AcceptsSsml("en-US-Chirp3-HD-Charon"));
        Assert.True(GoogleVoiceNames.AcceptsSsml("en-US-Studio-O"));
        Assert.False(GoogleVoiceNames.AcceptsSub("en-US-Studio-O"));
        Assert.True(GoogleVoiceNames.AcceptsSub("en-US-Neural2-A"));
        Assert.Equal(0.03, Pricing.EstimateSpeech("en-US-Chirp3-HD-Charon", 1000)!.Value, 6);
    }

    [Fact]
    public void Voices_parse_with_gender_family_price_and_label()
    {
        using var doc = JsonDocument.Parse(VoicesBody);
        var voices = GoogleTextToSpeech.ParseVoices(doc.RootElement);
        Assert.Equal(6, voices.Count);                                                 // "Kore" (Gemini-TTS) is left out
        var charon = voices[0];
        Assert.Equal("en-US-Chirp3-HD-Charon", charon.Name);
        Assert.Equal("en-US", charon.LanguageCode);
        Assert.Equal("Male", charon.Gender);
        Assert.Equal("Chirp 3: HD", charon.Family);
        Assert.Equal(30.0, charon.PricePerMillionChars);
        Assert.Equal(24000, charon.NaturalSampleRateHertz);
        Assert.Equal("Charon — Chirp 3: HD, male (en-US)", charon.Label);
        Assert.DoesNotContain(voices, v => v.Name == "Kore");
    }

    [Fact]
    public async Task Voices_are_fetched_once_cached_on_disk_filtered_by_language_and_refreshed_on_request()
    {
        using var t = new TempRoot();
        var h = new FakeHandler(HttpStatusCode.OK, VoicesBody);
        var hub = HubWithTts(t, h);

        var us = await hub.ListVoicesAsync("en-US");
        Assert.Equal(4, us.Count);
        Assert.All(us, v => Assert.Equal("en-US", v.LanguageCode));
        Assert.Equal(5, (await hub.ListVoicesAsync("en")).Count);
        Assert.Equal(6, (await hub.ListVoicesAsync()).Count);
        Assert.Single(h.Requests);
        Assert.Equal("https://texttospeech.googleapis.com/v1/voices", h.LastRequest!.RequestUri!.ToString());
        Assert.Equal("AIzaTestKey1234", h.LastRequest.Headers.GetValues("X-Goog-Api-Key").Single());
        Assert.True(File.Exists(AiPaths.VoicesFile(t.Path)));

        // another program (a new hub) reads the disk cache: no request
        var other = HubWithTts(t, h, "Gamify+");
        Assert.Equal(4, (await other.ListVoicesAsync("en-US")).Count);
        Assert.Single(h.Requests);

        await other.ListVoicesAsync(refresh: true);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Without_a_credential_speech_says_where_to_add_one()
    {
        using var t = new TempRoot();
        var hub = AiHub.Open("Author+", t.Path);
        Assert.False(hub.CanSpeak);
        var ex = await Assert.ThrowsAsync<AiException>(() => hub.ListVoicesAsync());
        Assert.Equal(AiErrorKind.NoKey, ex.Kind);
        Assert.Contains("Add a Google text-to-speech key in AI Settings", ex.Message);
        ex = await Assert.ThrowsAsync<AiException>(() => hub.SpeakAsync(new AiSpeechRequest { Text = "Hi.", Voice = "en-US-Neural2-A" }));
        Assert.Equal(AiErrorKind.NoKey, ex.Kind);
    }

    // ── Chunking ──────────────────────────────────────────────────────────────

    [Fact]
    public void Short_text_is_one_piece()
    {
        Assert.Equal(["Hello there."], SpeechText.Split("  Hello there.  "));
        Assert.Empty(SpeechText.Split("   "));
    }

    [Fact]
    public void Long_text_breaks_at_sentences_under_the_byte_limit_and_loses_nothing()
    {
        var sentences = Enumerable.Range(1, 400).Select(i => $"This is sentence number {i}, and it says something.").ToList();
        var text = string.Join(" ", sentences);
        var pieces = SpeechText.Split(text);

        Assert.True(pieces.Count > 1);
        Assert.All(pieces, p => Assert.True(SpeechText.Bytes(p) <= SpeechText.DefaultMaxBytes));
        Assert.All(pieces, p => Assert.EndsWith("something.", p));                    // every piece ends a sentence
        Assert.Equal(Normalize(text), Normalize(string.Join(" ", pieces)));
    }

    [Fact]
    public void Multibyte_text_is_measured_in_UTF8_bytes_not_characters()
    {
        // 3 bytes per character: 2,000 characters = 6,000 bytes, over the limit though under 4,500 characters
        var sentence = new string('語', 99) + "。";
        var text = string.Concat(Enumerable.Repeat(sentence, 20));
        Assert.True(text.Length < SpeechText.DefaultMaxBytes);
        var pieces = SpeechText.Split(text);
        Assert.True(pieces.Count >= 2);
        Assert.All(pieces, p => Assert.True(SpeechText.Bytes(p) <= SpeechText.DefaultMaxBytes));
        Assert.Equal(text, string.Concat(pieces));
    }

    [Fact]
    public void A_sentence_too_long_breaks_at_commas_then_spaces_then_characters_without_splitting_a_character()
    {
        var clauses = string.Join(", ", Enumerable.Range(1, 300).Select(i => $"clause {i}"));
        var pieces = SpeechText.Split(clauses, maxBytes: 200);
        Assert.All(pieces, p => Assert.True(SpeechText.Bytes(p) <= 200));
        Assert.All(pieces.SkipLast(1), p => Assert.EndsWith(",", p));

        var word = string.Concat(Enumerable.Repeat("😀", 100));                      // 4 bytes each, surrogate pairs
        var hard = SpeechText.Split(word, maxBytes: 30);
        Assert.All(hard, p => Assert.True(SpeechText.Bytes(p) <= 30));
        Assert.Equal(word, string.Concat(hard));
        Assert.All(hard, p => Assert.False(char.IsHighSurrogate(p[^1])));
    }

    [Fact]
    public void SSML_breaks_between_elements_and_sentences_and_each_piece_is_a_speak_document()
    {
        var body = string.Concat(Enumerable.Range(1, 200).Select(i => $"Line {i} is here. <break time=\"300ms\"/><emphasis level=\"strong\">Word {i}.</emphasis> "));
        var pieces = SpeechText.SplitSsml($"<?xml version=\"1.0\"?><speak version=\"1.1\">{body}</speak>");
        Assert.True(pieces.Count > 1);
        foreach (var p in pieces)
        {
            Assert.True(SpeechText.Bytes(p) <= SpeechText.DefaultMaxBytes);
            Assert.StartsWith("<speak>", p);
            Assert.EndsWith("</speak>", p);
            var inner = SpeechText.Unwrap(p);
            Assert.Equal(Count(inner, "<emphasis"), Count(inner, "</emphasis>"));   // no element cut in half
        }
        Assert.Equal(["<speak>Hi.</speak>"], SpeechText.SplitSsml("Hi."));

        var huge = $"<speak><prosody rate=\"slow\">{new string('a', 6000)}</prosody></speak>";
        var ex = Assert.Throws<AiException>(() => SpeechText.SplitSsml(huge));
        Assert.Contains("longer than Google accepts", ex.Message);
    }

    // ── Joining ───────────────────────────────────────────────────────────────

    [Fact]
    public void MP3_pieces_join_end_to_end_and_later_ID3_tags_are_dropped()
    {
        byte[] id3 = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 5, 1, 2, 3, 4, 5];
        byte[] a = [.. id3, 0xFF, 0xFB, 1];
        byte[] b = [.. id3, 0xFF, 0xFB, 2];
        var joined = AudioJoin.Mp3([a, b]);
        Assert.Equal([.. a, 0xFF, 0xFB, 2], joined);
        Assert.Equal(15, AudioJoin.Id3Length(a));
        Assert.Equal(0, AudioJoin.Id3Length([0xFF, 0xFB, 1]));
    }

    [Fact]
    public void WAV_pieces_merge_their_samples_under_one_header()
    {
        var a = Wav([1, 2, 3, 4], 24000);
        var b = Wav([5, 6], 24000);
        var joined = AudioJoin.Wav([a, b]);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(joined, 0, 4));
        Assert.Equal(joined.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(joined.AsSpan(4)));
        var (fmt, data) = AudioJoin.ReadWav(joined);
        Assert.Equal(24000, BinaryPrimitives.ReadInt32LittleEndian(fmt!.AsSpan(4)));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, data.ToArray());
    }

    private static byte[] Wav(byte[] pcm, int rate)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    // ── Speaking ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Speak_sends_each_piece_in_order_reports_progress_joins_and_writes_one_ledger_line()
    {
        using var t = new TempRoot();
        int n = 0;
        var h = new FakeHandler(_ => (HttpStatusCode.OK, AudioBody([0xFF, 0xFB, (byte)++n])));
        var hub = HubWithTts(t, h);
        AiSpeech? spoke = null;
        hub.Spoke += s => spoke = s;
        var reports = new List<(int, int)>();

        var text = string.Join(" ", Enumerable.Range(1, 300).Select(i => $"Sentence {i} of the chapter is read aloud."));
        var r = await hub.SpeakAsync(new AiSpeechRequest { Text = text, Voice = "en-US-Chirp3-HD-Charon", SpeakingRate = 1.1, Purpose = "chapter-audio" },
                                     new SyncProgress<(int, int)>(reports.Add));

        var pieces = SpeechText.Split(text);
        Assert.Equal(pieces.Count, h.Requests.Count);
        Assert.Equal(pieces.Count, r.Requests);
        Assert.Equal(pieces.Sum(p => p.Length), r.Characters);
        Assert.Equal(pieces.Sum(p => p.Length), AiHub.SpeechCharacters(new AiSpeechRequest { Text = text, Voice = "x" }));
        Assert.Equal((0, pieces.Count), reports[0]);
        Assert.Equal((pieces.Count, pieces.Count), reports[^1]);
        Assert.Equal(Enumerable.Range(1, pieces.Count).SelectMany(i => new byte[] { 0xFF, 0xFB, (byte)i }).ToArray(), r.Audio);
        Assert.Same(r, spoke);
        Assert.Equal(".mp3", r.FileExtension);

        using var body = JsonDocument.Parse(h.Bodies[0]!);
        var root = body.RootElement;
        Assert.Equal(pieces[0], root.GetProperty("input").GetProperty("text").GetString());
        Assert.False(root.GetProperty("input").TryGetProperty("ssml", out _));
        Assert.Equal("en-US", root.GetProperty("voice").GetProperty("languageCode").GetString());
        Assert.Equal("en-US-Chirp3-HD-Charon", root.GetProperty("voice").GetProperty("name").GetString());
        Assert.Equal("MP3", root.GetProperty("audioConfig").GetProperty("audioEncoding").GetString());
        Assert.Equal(1.1, root.GetProperty("audioConfig").GetProperty("speakingRate").GetDouble(), 6);
        Assert.Equal("https://texttospeech.googleapis.com/v1/text:synthesize", h.LastRequest!.RequestUri!.ToString());

        var line = Assert.Single(hub.Usage.Read(DateTime.Now));
        Assert.Equal(AiProviderType.GoogleTts, line.Provider);
        Assert.Equal("en-US-Chirp3-HD-Charon", line.Model);
        Assert.Equal("chapter-audio", line.Purpose);
        Assert.Equal(r.Characters, line.Chars);
        Assert.True(line.Ok);
        Assert.Equal(r.Characters / 1_000_000.0 * 30, line.CostUsd!.Value, 9);
        Assert.Equal(r.EstimatedCostUsd, line.CostUsd);
        Assert.Equal(r.Characters, hub.Usage.Summarize(DateTime.Now).Totals.Characters);
        Assert.Contains($",{r.Characters}\n", UsageLedger.ToCsv([line]));
    }

    [Fact]
    public async Task WAV_asks_for_LINEAR16_and_SSML_is_sent_as_ssml()
    {
        using var t = new TempRoot();
        var h = new FakeHandler(_ => (HttpStatusCode.OK, AudioBody(Wav([9, 9], 24000))));
        var hub = HubWithTts(t, h);
        var r = await hub.SpeakAsync(new AiSpeechRequest { Text = "<speak>Hello <break time=\"1s\"/> there.</speak>", IsSsml = true,
                                                           Voice = "en-GB-Neural2-B", Format = AiAudioFormat.Wav });
        using var body = JsonDocument.Parse(h.LastBody!);
        Assert.Equal("LINEAR16", body.RootElement.GetProperty("audioConfig").GetProperty("audioEncoding").GetString());
        Assert.Equal("<speak>Hello <break time=\"1s\"/> there.</speak>", body.RootElement.GetProperty("input").GetProperty("ssml").GetString());
        Assert.Equal("en-GB", body.RootElement.GetProperty("voice").GetProperty("languageCode").GetString());
        Assert.Equal(AiAudioFormat.Wav, r.Format);
        Assert.Equal(new byte[] { 9, 9 }, AudioJoin.ReadWav(r.Audio).Data.ToArray());

        var ex = await Assert.ThrowsAsync<AiException>(() => hub.SpeakAsync(new AiSpeechRequest { Text = "<speak>Hi</speak>", IsSsml = true, Voice = "en-US-Chirp3-HD-Charon" }));
        Assert.Contains("plain text only", ex.Message);
    }

    [Fact]
    public async Task Cancel_part_way_records_the_characters_already_sent()
    {
        using var t = new TempRoot();
        using var cts = new CancellationTokenSource();
        var h = new FakeHandler(_ => { cts.Cancel(); return (HttpStatusCode.OK, AudioBody([1])); });
        var hub = HubWithTts(t, h);
        var text = string.Join(" ", Enumerable.Range(1, 300).Select(i => $"Sentence {i} of the chapter is read aloud."));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            hub.SpeakAsync(new AiSpeechRequest { Text = text, Voice = "en-US-Standard-A" }, null, cts.Token));
        var line = Assert.Single(hub.Usage.Read(DateTime.Now));
        Assert.True(line.Cancelled);
        Assert.Equal(SpeechText.Split(text)[0].Length, line.Chars);
    }

    // ── Errors ────────────────────────────────────────────────────────────────

    [Fact]
    public void Errors_are_mapped_to_plain_words()
    {
        var disabled = GoogleTextToSpeech.MapError(403,
            "{ \"error\": { \"code\": 403, \"message\": \"Cloud Text-to-Speech API has not been used in project 123 before or it is disabled.\", \"status\": \"PERMISSION_DENIED\", \"details\": [ { \"reason\": \"SERVICE_DISABLED\" } ] } }", null);
        Assert.Equal(AiErrorKind.Auth, disabled.Kind);
        Assert.StartsWith("Turn on the Cloud Text-to-Speech API for the key's Google Cloud project", disabled.Message);
        Assert.Contains(GoogleTextToSpeech.EnableApiUrl, disabled.Message);
        Assert.Equal(AiProviderType.GoogleTts, disabled.Provider);
        Assert.Contains("If this is a Gemini key", disabled.Message);
        var unrelated = GoogleTextToSpeech.MapError(400, "{ \"error\": { \"message\": \"Bad input. See https://cloud.google.com/billing/docs for limits.\" } }", null);
        Assert.DoesNotContain("Billing is not turned on", unrelated.Message);

        var badKey = GoogleTextToSpeech.MapError(400, "{ \"error\": { \"code\": 400, \"message\": \"API key not valid. Please pass a valid API key.\", \"details\": [ { \"reason\": \"API_KEY_INVALID\" } ] } }", "en-US-Neural2-A");
        Assert.Equal(AiErrorKind.Auth, badKey.Kind);
        Assert.Contains("did not accept the API key", badKey.Message);

        var voice = GoogleTextToSpeech.MapError(400, "{ \"error\": { \"code\": 400, \"message\": \"Voice 'en-US-Nope-Z' does not exist.\" } }", "en-US-Nope-Z");
        Assert.Equal(AiErrorKind.ProviderError, voice.Kind);
        Assert.Contains("could not use the voice \"en-US-Nope-Z\"", voice.Message);
        Assert.Equal(400, voice.StatusCode);

        var ssml = GoogleTextToSpeech.MapError(400, "{ \"error\": { \"code\": 400, \"message\": \"Invalid SSML: unsupported tag sub.\" } }", "en-US-Studio-O");
        Assert.Contains("speech markup (SSML)", ssml.Message);

        var quota = GoogleTextToSpeech.MapError(429, "{ \"error\": { \"code\": 429, \"message\": \"Quota exceeded\" } }", "v");
        Assert.Equal(AiErrorKind.RateLimit, quota.Kind);
        Assert.Contains("limit", quota.Message);

        var billing = GoogleTextToSpeech.MapError(403, "{ \"error\": { \"code\": 403, \"message\": \"This API method requires billing to be enabled.\", \"details\": [ { \"reason\": \"BILLING_DISABLED\" } ] } }", null);
        Assert.Contains("Billing is not turned on", billing.Message);
    }

    [Fact]
    public async Task A_refused_request_is_an_AiException_and_a_failed_ledger_line()
    {
        using var t = new TempRoot();
        var h = new FakeHandler(HttpStatusCode.Forbidden,
            "{ \"error\": { \"code\": 403, \"message\": \"Cloud Text-to-Speech API has not been used in project 1 before or it is disabled.\", \"details\": [ { \"reason\": \"SERVICE_DISABLED\" } ] } }");
        var hub = HubWithTts(t, h);
        var ex = await Assert.ThrowsAsync<AiException>(() => hub.SpeakAsync(new AiSpeechRequest { Text = "Hello.", Voice = "en-US-Neural2-A" }));
        Assert.Equal(AiErrorKind.Auth, ex.Kind);
        var line = Assert.Single(hub.Usage.Read(DateTime.Now));
        Assert.False(line.Ok);
        Assert.Equal("Auth", line.Error);
        Assert.Equal(0, line.Chars);
    }

    // ── Service account ───────────────────────────────────────────────────────

    [Fact]
    public async Task Service_account_signs_a_JWT_gets_a_token_once_and_sends_it_as_Bearer()
    {
        GoogleTextToSpeech.ClearTokens();
        using var rsa = RSA.Create(2048);
        var json = ServiceAccountJson(rsa, $"tts-{Guid.NewGuid():N}@proj.iam.gserviceaccount.com");
        var cred = new GoogleTtsCredential(GoogleTtsCredentialKind.ServiceAccount, json, "proj", "x", DateTime.UtcNow, "test");
        var h = new FakeHandler(r => r.RequestUri!.Host == "oauth2.googleapis.com"
            ? (HttpStatusCode.OK, "{ \"access_token\": \"ya29.token\", \"expires_in\": 3599, \"token_type\": \"Bearer\" }")
            : (HttpStatusCode.OK, AudioBody([7])));
        var tts = new GoogleTextToSpeech(cred, h.Client());

        Assert.Equal(new byte[] { 7 }, await tts.SynthesizeAsync("Hi.", false, "en-US-Neural2-A", "en-US", 1, AiAudioFormat.Mp3, CancellationToken.None));
        Assert.Equal(new byte[] { 7 }, await tts.SynthesizeAsync("Again.", false, "en-US-Neural2-A", "en-US", 1, AiAudioFormat.Mp3, CancellationToken.None));

        Assert.Equal(3, h.Requests.Count);                                             // one token, two syntheses
        Assert.Equal("https://oauth2.googleapis.com/token", h.Requests[0].RequestUri!.ToString());
        var form = h.Bodies[0]!;
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", form);
        var jwt = Uri.UnescapeDataString(form.Split("assertion=")[1]);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        using var claims = JsonDocument.Parse(FromBase64Url(parts[1]));
        Assert.Equal("https://www.googleapis.com/auth/cloud-platform", claims.RootElement.GetProperty("scope").GetString());
        Assert.Equal("https://oauth2.googleapis.com/token", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal(3600, claims.RootElement.GetProperty("exp").GetInt64() - claims.RootElement.GetProperty("iat").GetInt64());
        Assert.EndsWith("@proj.iam.gserviceaccount.com", claims.RootElement.GetProperty("iss").GetString());
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]),
                                   HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        Assert.Equal("Bearer ya29.token", h.Requests[1].Headers.Authorization!.ToString());
        Assert.False(h.Requests[1].Headers.Contains("X-Goog-Api-Key"));
    }

    [Fact]
    public async Task A_refused_service_account_says_so()
    {
        GoogleTextToSpeech.ClearTokens();
        using var rsa = RSA.Create(2048);
        var cred = new GoogleTtsCredential(GoogleTtsCredentialKind.ServiceAccount, ServiceAccountJson(rsa, $"x-{Guid.NewGuid():N}@p.iam.gserviceaccount.com"), "p", "x", null, null);
        var h = new FakeHandler(HttpStatusCode.BadRequest, "{ \"error\": \"invalid_grant\", \"error_description\": \"Invalid JWT Signature.\" }");
        var ex = await Assert.ThrowsAsync<AiException>(() => new GoogleTextToSpeech(cred, h.Client()).ListVoicesAsync(null, CancellationToken.None));
        Assert.Equal(AiErrorKind.Auth, ex.Kind);
        Assert.Contains("Invalid JWT Signature", ex.Message);
        Assert.Contains("service-account file", ex.Message);
    }

    // ── 1.9.0: sample rate and custom pronunciations ─────────────────────────

    private static readonly AiPronunciation[] Names =
    [
        new("Olemelukwe", "oʊlɛmɛˈluːkweɪ"),
        new("Dalgo", "ˈdælɡoʊ"),
        new("Olemelukwe", "different"),                  // a second entry for a phrase: the first wins
        new("Nobody", "  "),                             // no IPA: skipped
        new("  ", "ə")                                   // no phrase: skipped
    ];

    private static List<(string Phrase, string Ipa, string Encoding)> SentPronunciations(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var list = new List<(string, string, string)>();
        if (!doc.RootElement.GetProperty("input").TryGetProperty("customPronunciations", out var cp)) return list;
        foreach (var p in cp.GetProperty("pronunciations").EnumerateArray())
            list.Add((p.GetProperty("phrase").GetString()!, p.GetProperty("pronunciation").GetString()!, p.GetProperty("phoneticEncoding").GetString()!));
        return list;
    }

    [Theory]
    [InlineData("en-US-Chirp3-HD-Charon", true)]
    [InlineData("en-GB-Chirp3-HD-Charon", false)]
    [InlineData("en-US-Chirp-HD-F", false)]
    [InlineData("en-US-Neural2-A", false)]
    [InlineData("en-US-Studio-O", false)]
    [InlineData("Kore", false)]
    public void Pronunciations_are_taken_by_Chirp_3_HD_voices_in_en_US(string voice, bool accepts)
    {
        Assert.Equal(accepts, GoogleVoiceNames.AcceptsPronunciations(voice));
        Assert.Equal(accepts, new AiVoice { Name = voice, LanguageCode = "en-US", Family = GoogleVoiceNames.Family(voice) }.AcceptsPronunciations);
    }

    [Fact]
    public async Task Sample_rate_is_sent_and_the_WAV_comes_back_at_that_rate()
    {
        using var t = new TempRoot();
        var h = new FakeHandler(_ => (HttpStatusCode.OK, AudioBody(Wav([1, 2, 3, 4], 44100))));
        var hub = HubWithTts(t, h);
        var text = string.Join(" ", Enumerable.Range(1, 300).Select(i => $"Sentence {i} of the chapter is read aloud."));
        var r = await hub.SpeakAsync(new AiSpeechRequest { Text = text, Voice = "en-US-Chirp3-HD-Charon", Format = AiAudioFormat.Wav, SampleRateHertz = 44100 });

        Assert.True(h.Requests.Count > 1);
        foreach (var b in h.Bodies)
        {
            using var body = JsonDocument.Parse(b!);
            Assert.Equal(44100, body.RootElement.GetProperty("audioConfig").GetProperty("sampleRateHertz").GetInt32());
            Assert.Equal("LINEAR16", body.RootElement.GetProperty("audioConfig").GetProperty("audioEncoding").GetString());
        }
        var (fmt, data) = AudioJoin.ReadWav(r.Audio);
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(fmt!.AsSpan(0)));       // PCM
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(2)));        // mono
        Assert.Equal(44100, BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4)));
        Assert.Equal(88200, BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(8)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(14)));
        Assert.Equal(h.Requests.Count * 4, data.Length);

        // MP3 asks for the rate too
        var h2 = new FakeHandler(_ => (HttpStatusCode.OK, AudioBody([0xFF, 0xFB, 1])));
        hub.TtsFactory = c => new GoogleTextToSpeech(c, h2.Client());
        await hub.SpeakAsync(new AiSpeechRequest { Text = "Hello.", Voice = "en-US-Neural2-A", SampleRateHertz = 44100 });
        using var mp3 = JsonDocument.Parse(h2.LastBody!);
        Assert.Equal(44100, mp3.RootElement.GetProperty("audioConfig").GetProperty("sampleRateHertz").GetInt32());
    }

    [Fact]
    public void Samples_without_a_header_get_one_at_the_rate_asked_for()
    {
        var joined = AudioJoin.Join([[1, 2], [3, 4]], AiAudioFormat.Wav, 44100);
        var (fmt, data) = AudioJoin.ReadWav(joined);
        Assert.Equal(44100, BinaryPrimitives.ReadInt32LittleEndian(fmt!.AsSpan(4)));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, data.ToArray());
        Assert.Equal(24000, BinaryPrimitives.ReadInt32LittleEndian(AudioJoin.ReadWav(AudioJoin.Wav([[1, 2]])).Fmt!.AsSpan(4)));
    }

    [Fact]
    public async Task Pronunciations_go_only_with_the_pieces_that_contain_the_phrase()
    {
        using var t = new TempRoot();
        var h = new FakeHandler(_ => (HttpStatusCode.OK, AudioBody([0xFF, 0xFB, 1])));
        var hub = HubWithTts(t, h);
        var filler = string.Join(" ", Enumerable.Range(1, 300).Select(i => $"Sentence {i} of the chapter is read aloud."));
        var text = "Dalgo met Olemelukwe. " + filler + " Olemelukwe left.";
        var r = await hub.SpeakAsync(new AiSpeechRequest { Text = text, Voice = "en-US-Chirp3-HD-Charon", Pronunciations = Names });

        var pieces = SpeechText.Split(text);
        Assert.True(pieces.Count >= 3);
        Assert.Equal(pieces.Count, h.Requests.Count);
        Assert.False(r.PronunciationsIgnored);
        Assert.Equal([("Olemelukwe", "oʊlɛmɛˈluːkweɪ", "PHONETIC_ENCODING_IPA"), ("Dalgo", "ˈdælɡoʊ", "PHONETIC_ENCODING_IPA")],
                     SentPronunciations(h.Bodies[0]!));
        Assert.Empty(SentPronunciations(h.Bodies[1]!));                              // neither name in the middle
        Assert.Equal([("Olemelukwe", "oʊlɛmɛˈluːkweɪ", "PHONETIC_ENCODING_IPA")], SentPronunciations(h.Bodies[^1]!));
    }

    [Theory]
    [InlineData("en-US-Neural2-A", null)]
    [InlineData("en-GB-Chirp3-HD-Charon", null)]
    [InlineData("en-US-Chirp3-HD-Charon", "en-GB")]
    public async Task Pronunciations_are_ignored_for_voices_or_languages_that_do_not_take_them(string voice, string? language)
    {
        using var t = new TempRoot();
        var h = new FakeHandler(_ => (HttpStatusCode.OK, AudioBody([0xFF, 0xFB, 1])));
        var hub = HubWithTts(t, h);
        var r = await hub.SpeakAsync(new AiSpeechRequest { Text = "Dalgo met Olemelukwe.", Voice = voice, LanguageCode = language, Pronunciations = Names });
        Assert.Empty(SentPronunciations(h.LastBody!));
        Assert.False(r.PronunciationsIgnored);
    }

    [Fact]
    public async Task A_refused_pronunciation_sends_the_piece_again_without_them_and_says_so()
    {
        using var t = new TempRoot();
        var h = new FakeHandler(r =>
        {
            var body = r.Content!.ReadAsStringAsync().Result;
            return body.Contains("customPronunciations", StringComparison.Ordinal)
                ? (HttpStatusCode.BadRequest, "{ \"error\": { \"code\": 400, \"message\": \"Invalid pronunciation for phrase 'Olemelukwe': unrecognised IPA symbol.\", \"status\": \"INVALID_ARGUMENT\" } }")
                : (HttpStatusCode.OK, AudioBody([0xFF, 0xFB, 9]));
        });
        var hub = HubWithTts(t, h);
        var r = await hub.SpeakAsync(new AiSpeechRequest { Text = "Dalgo met Olemelukwe.", Voice = "en-US-Chirp3-HD-Charon", Pronunciations = Names });

        Assert.True(r.PronunciationsIgnored);
        Assert.Equal(2, h.Requests.Count);
        Assert.NotEmpty(SentPronunciations(h.Bodies[0]!));
        Assert.Empty(SentPronunciations(h.Bodies[1]!));
        Assert.Equal(new byte[] { 0xFF, 0xFB, 9 }, r.Audio);
        Assert.Equal("Dalgo met Olemelukwe.".Length, r.Characters);
        Assert.True(Assert.Single(hub.Usage.Read(DateTime.Now)).Ok);

        // Any other refusal is not retried
        var h2 = new FakeHandler(HttpStatusCode.BadRequest, "{ \"error\": { \"code\": 400, \"message\": \"Input is too long.\" } }");
        hub.TtsFactory = c => new GoogleTextToSpeech(c, h2.Client());
        await Assert.ThrowsAsync<AiException>(() => hub.SpeakAsync(new AiSpeechRequest { Text = "Dalgo met Olemelukwe.", Voice = "en-US-Chirp3-HD-Charon", Pronunciations = Names }));
        Assert.Single(h2.Requests);

        var mapped = GoogleTextToSpeech.MapError(400, "{ \"error\": { \"message\": \"Invalid pronunciation for phrase 'X'.\" } }", "en-US-Chirp3-HD-Charon");
        Assert.True(GoogleTextToSpeech.IsPronunciationError(mapped));
        Assert.Contains("pronunciation list", mapped.Message);
    }

    [Fact]
    public async Task A_1_8_0_request_sends_no_rate_and_no_pronunciations()
    {
        using var t = new TempRoot();
        var h = new FakeHandler(_ => (HttpStatusCode.OK, AudioBody(Wav([5, 6], 24000))));
        var hub = HubWithTts(t, h);
        var r = await hub.SpeakAsync(new AiSpeechRequest { Text = "Dalgo met Olemelukwe.", Voice = "en-US-Chirp3-HD-Charon", Format = AiAudioFormat.Wav });
        using var body = JsonDocument.Parse(h.LastBody!);
        Assert.False(body.RootElement.GetProperty("audioConfig").TryGetProperty("sampleRateHertz", out _));
        Assert.Equal(["text"], body.RootElement.GetProperty("input").EnumerateObject().Select(p => p.Name));
        Assert.False(r.PronunciationsIgnored);
        Assert.Equal(24000, BinaryPrimitives.ReadInt32LittleEndian(AudioJoin.ReadWav(r.Audio).Fmt!.AsSpan(4)));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private static string Normalize(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static int Count(string s, string part) => s.Split(part).Length - 1;

    /// <summary>Progress&lt;T&gt; posts to a sync context; this one reports at once, in order.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
