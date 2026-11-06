using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eaglin.AiManager.Catalog;
using Eaglin.AiManager.Providers;
using Eaglin.AiManager.Store;

namespace Eaglin.AiManager.Speech;

/// <summary>
/// The parts of a Google Cloud service-account key file that signing in needs (1.8.0).
/// <see cref="ToString"/> never shows the private key.
/// </summary>
public sealed record GoogleServiceAccount(string ClientEmail, string PrivateKeyPem, string? ProjectId, string TokenUri)
{
    public const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    /// <summary>Reads a service-account key file's JSON. Throws <see cref="ArgumentException"/> in plain words when it is not one.</summary>
    public static GoogleServiceAccount Parse(string json)
    {
        const string notOne = "This file is not a Google Cloud service-account key. In the Google Cloud console open IAM & Admin ▸ " +
                              "Service accounts, choose the account, then Keys ▸ Add key ▸ Create new key ▸ JSON, and choose the file it downloads.";
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json ?? string.Empty); }
        catch (JsonException ex) { throw new ArgumentException(notOne, nameof(json), ex); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException(notOne, nameof(json));
            var type  = ProviderHttp.Str(root, "type");
            var email = ProviderHttp.Str(root, "client_email");
            var key   = ProviderHttp.Str(root, "private_key");
            if (type != "service_account" || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(key))
                throw new ArgumentException(notOne, nameof(json));
            var uri = ProviderHttp.Str(root, "token_uri");
            return new GoogleServiceAccount(email, key, ProviderHttp.Str(root, "project_id"), string.IsNullOrWhiteSpace(uri) ? DefaultTokenUri : uri);
        }
    }

    public override string ToString() => $"service account {ClientEmail}";
}

/// <summary>
/// Google Cloud Text-to-Speech over REST (1.8.0): the voice list and synthesis, signed in with an
/// API key (the <c>X-Goog-Api-Key</c> header) or a service account (an OAuth2 token from a JWT
/// signed RS256 with .NET's own RSA — no Google packages). Apps go through <see cref="AiHub.SpeakAsync"/>
/// and <see cref="AiHub.ListVoicesAsync"/>; this class sends one request at a time.
/// </summary>
public sealed class GoogleTextToSpeech
{
    internal const string VoicesUrl     = "https://texttospeech.googleapis.com/v1/voices";
    internal const string SynthesizeUrl = "https://texttospeech.googleapis.com/v1/text:synthesize";
    internal const string Scope         = "https://www.googleapis.com/auth/cloud-platform";
    public   const string EnableApiUrl  = "https://console.cloud.google.com/apis/library/texttospeech.googleapis.com";
    private  const string Vendor        = "Google text-to-speech";
    private  const AiProviderType P     = AiProviderType.GoogleTts;

    private readonly GoogleTtsCredential _credential;
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Tokens made from service accounts, kept until shortly before they expire. Keyed by account and key.</summary>
    private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> Tokens = new();

    public GoogleTextToSpeech(GoogleTtsCredential credential, HttpClient? http = null, Func<DateTimeOffset>? now = null)
    {
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _http = http ?? ProviderHttp.DefaultClient;
        _now  = now ?? (() => DateTimeOffset.UtcNow);
    }

    // ── Voices ────────────────────────────────────────────────────────────────

    /// <summary>Every voice Google offers (or those for one language).</summary>
    public async Task<IReadOnlyList<AiVoice>> ListVoicesAsync(string? languageCode, CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(languageCode) ? VoicesUrl : $"{VoicesUrl}?languageCode={Uri.EscapeDataString(languageCode.Trim())}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await AuthorizeAsync(request, ct);
        ActivityLog.AiCall("GoogleTts voices", $"REQUEST  {url}");
        using var response = await ProviderHttp.SendAsync(_http, P, "GoogleTts voices", request, ct);
        await ThrowIfFailed(response, null, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var list = ParseVoices(doc.RootElement);
            ActivityLog.AiCall("GoogleTts voices", $"RESPONSE  voices={list.Count}");
            return list;
        }
        catch (JsonException ex)
        {
            throw new AiException(AiErrorKind.EmptyResponse, P, "Google sent back something that is not a list of voices.", ex);
        }
    }

    /// <summary>
    /// Reads <c>{ voices: [ { languageCodes, name, ssmlGender, naturalSampleRateHertz } ] }</c>. Voices whose
    /// name has no language ("Achernar", "Kore") are Gemini-TTS voices: they need a Gemini model name
    /// and are billed by the token, so they are left out until AI Manager supports them.
    /// </summary>
    public static IReadOnlyList<AiVoice> ParseVoices(JsonElement root)
    {
        var list = new List<AiVoice>();
        var voices = ProviderHttp.Obj(root, "voices");
        if (voices.ValueKind != JsonValueKind.Array) return list;
        foreach (var v in voices.EnumerateArray())
        {
            var name = ProviderHttp.Str(v, "name");
            if (string.IsNullOrWhiteSpace(name) || GoogleVoiceNames.Language(name).Length == 0) continue;
            var langs = new List<string>();
            if (ProviderHttp.Obj(v, "languageCodes") is { ValueKind: JsonValueKind.Array } codes)
                foreach (var c in codes.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } s) langs.Add(s);
            if (langs.Count == 0 && GoogleVoiceNames.Language(name) is { Length: > 0 } fromName) langs.Add(fromName);
            list.Add(new AiVoice
            {
                Name = name,
                LanguageCode = langs.FirstOrDefault() ?? string.Empty,
                LanguageCodes = langs,
                Gender = Gender(ProviderHttp.Str(v, "ssmlGender")),
                Family = GoogleVoiceNames.Family(name),
                PricePerMillionChars = Pricing.SpeechPricePerMillion(name),
                NaturalSampleRateHertz = ProviderHttp.Int(v, "naturalSampleRateHertz")
            });
        }
        return list;
    }

    private static string Gender(string? ssml) => ssml?.ToUpperInvariant() switch
    {
        "MALE"    => "Male",
        "FEMALE"  => "Female",
        "NEUTRAL" => "Neutral",
        _         => string.Empty
    };

    // ── Synthesis ─────────────────────────────────────────────────────────────

    /// <summary>One request: text (or SSML) of at most 5,000 bytes to sound. Returns the decoded audio bytes.</summary>
    public Task<byte[]> SynthesizeAsync(string input, bool isSsml, string voice, string languageCode, double speakingRate,
                                        AiAudioFormat format, CancellationToken ct) =>
        SynthesizeAsync(input, isSsml, voice, languageCode, speakingRate, format, null, null, ct);

    /// <summary>
    /// One request (1.9.0): as above, with a sample rate (<c>audioConfig.sampleRateHertz</c>; null = the voice's own) and
    /// custom pronunciations (<c>input.customPronunciations.pronunciations[]</c>, IPA), sent as given — the caller
    /// (<see cref="AiHub.SpeakAsync"/>) chooses which apply to this piece.
    /// </summary>
    public async Task<byte[]> SynthesizeAsync(string input, bool isSsml, string voice, string languageCode, double speakingRate,
                                              AiAudioFormat format, int? sampleRateHertz, IReadOnlyList<AiPronunciation>? pronunciations,
                                              CancellationToken ct)
    {
        var payload = Payload(input, isSsml, voice, languageCode, speakingRate, format, sampleRateHertz, pronunciations);
        using var request = new HttpRequestMessage(HttpMethod.Post, SynthesizeUrl) { Content = JsonContent.Create(payload) };
        await AuthorizeAsync(request, ct);
        ActivityLog.AiCall("GoogleTts synthesize", $"REQUEST  voice={voice}  chars={input.Length}  bytes={SpeechText.Bytes(input)}  ssml={isSsml}  {format}" +
                                                  (sampleRateHertz is int hz ? $"  rate={hz}" : "") +
                                                  (pronunciations is { Count: > 0 } pr ? $"  pronunciations={pr.Count}" : ""));
        using var response = await ProviderHttp.SendAsync(_http, P, "GoogleTts synthesize", request, ct);
        await ThrowIfFailed(response, voice, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var b64 = ProviderHttp.Str(doc.RootElement, "audioContent");
            if (string.IsNullOrEmpty(b64)) throw new AiException(AiErrorKind.EmptyResponse, P, "Google sent back no sound for this text.");
            var audio = Convert.FromBase64String(b64);
            ActivityLog.AiCall("GoogleTts synthesize", $"RESPONSE  audio_bytes={audio.Length}");
            return audio;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new AiException(AiErrorKind.EmptyResponse, P, "Google sent back something that is not sound.", ex);
        }
    }

    /// <summary>The JSON body of <c>v1/text:synthesize</c>.</summary>
    internal static Dictionary<string, object> Payload(string input, bool isSsml, string voice, string languageCode, double speakingRate,
                                                       AiAudioFormat format, int? sampleRateHertz, IReadOnlyList<AiPronunciation>? pronunciations)
    {
        var inputObj = new Dictionary<string, object> { [isSsml ? "ssml" : "text"] = input };
        if (pronunciations is { Count: > 0 })
            inputObj["customPronunciations"] = new
            {
                pronunciations = pronunciations.Select(p => new { phrase = p.Phrase, phoneticEncoding = "PHONETIC_ENCODING_IPA", pronunciation = p.Ipa }).ToArray()
            };
        var audioConfig = new Dictionary<string, object>
        {
            ["audioEncoding"] = format == AiAudioFormat.Wav ? "LINEAR16" : "MP3",
            ["speakingRate"]  = speakingRate
        };
        if (sampleRateHertz is int hz && hz > 0) audioConfig["sampleRateHertz"] = hz;
        return new Dictionary<string, object>
        {
            ["input"] = inputObj,
            ["voice"] = new { languageCode, name = voice },
            ["audioConfig"] = audioConfig
        };
    }

    /// <summary>
    /// True when Google's refusal is about a custom pronunciation (1.9.0) — a 400 that mentions a pronunciation, phoneme or
    /// phonetic encoding — so the piece can be sent again without them.
    /// </summary>
    public static bool IsPronunciationError(AiException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex.StatusCode != 400) return false;
        var m = ex.Message;
        return m.Contains("pronunciation", StringComparison.OrdinalIgnoreCase) || m.Contains("phonem", StringComparison.OrdinalIgnoreCase)
            || m.Contains("phonetic", StringComparison.OrdinalIgnoreCase);
    }

    // ── Signing in ────────────────────────────────────────────────────────────

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (_credential.Kind == GoogleTtsCredentialKind.ApiKey)
            request.Headers.Add("X-Goog-Api-Key", _credential.Secret);
        else
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct));
    }

    /// <summary>An OAuth2 access token for the stored service account; reused until five minutes before it expires.</summary>
    internal async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        GoogleServiceAccount sa;
        try { sa = GoogleServiceAccount.Parse(_credential.Secret); }
        catch (ArgumentException ex) { throw new AiException(AiErrorKind.Auth, P, ex.Message, ex); }

        var cacheKey = sa.ClientEmail + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sa.PrivateKeyPem)));
        var now = _now();
        if (Tokens.TryGetValue(cacheKey, out var cached) && cached.Expires - TimeSpan.FromMinutes(5) > now) return cached.Token;

        var jwt = MakeJwt(sa, now);
        using var request = new HttpRequestMessage(HttpMethod.Post, sa.TokenUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"]  = jwt
            })
        };
        ActivityLog.AiCall("GoogleTts token", $"REQUEST  {sa.ClientEmail}");
        using var response = await ProviderHttp.SendAsync(_http, P, "GoogleTts token", request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = TokenError(body);
            ActivityLog.Error("GoogleTts token", $"HTTP {(int)response.StatusCode}: {detail}");
            throw new AiException(AiErrorKind.Auth, P,
                $"Google did not accept the service-account file ({detail}). The key may have been deleted or the account disabled: " +
                "make a new key for the account in the Google Cloud console, or use an API key instead.", statusCode: (int)response.StatusCode);
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var token = ProviderHttp.Str(doc.RootElement, "access_token");
            if (string.IsNullOrEmpty(token)) throw new JsonException("no access_token");
            var seconds = ProviderHttp.Int(doc.RootElement, "expires_in");
            Tokens[cacheKey] = (token, now.AddSeconds(seconds > 0 ? seconds : 3600));
            return token;
        }
        catch (JsonException ex)
        {
            throw new AiException(AiErrorKind.Auth, P, "Google's sign-in answer for the service account could not be read.", ex);
        }
    }

    /// <summary>The signed assertion: header {alg RS256, typ JWT}, claims {iss, scope, aud, iat, exp = iat + 1 h}.</summary>
    internal static string MakeJwt(GoogleServiceAccount sa, DateTimeOffset now)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" });
        var iat = now.ToUnixTimeSeconds();
        var claims = JsonSerializer.SerializeToUtf8Bytes(new { iss = sa.ClientEmail, scope = Scope, aud = sa.TokenUri, iat, exp = iat + 3600 });
        var signingInput = $"{Base64Url(header)}.{Base64Url(claims)}";
        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(sa.PrivateKeyPem); }
        catch (ArgumentException ex)
        {
            throw new AiException(AiErrorKind.Auth, P, "The private key in the service-account file could not be read. Download a new key file.", ex);
        }
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Forgets the tokens made from service accounts (tests, and after the credential changes).</summary>
    internal static void ClearTokens() => Tokens.Clear();

    private static string TokenError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var e = ProviderHttp.Str(doc.RootElement, "error_description") ?? ProviderHttp.Str(doc.RootElement, "error");
            if (!string.IsNullOrEmpty(e)) return e;
        }
        catch (JsonException) { }
        return body.Length > 200 ? body[..200] + "…" : body;
    }

    // ── Errors in plain words ─────────────────────────────────────────────────

    private static async Task ThrowIfFailed(HttpResponseMessage response, string? voice, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var ex = MapError((int)response.StatusCode, body, voice);
        ActivityLog.Error("GoogleTts", $"HTTP {(int)response.StatusCode}: {ProviderHttp.TryExtractErrorMessage(body)}");
        throw ex;
    }

    /// <summary>Turns Google's error answer into an <see cref="AiException"/> a non-technical person can act on.</summary>
    public static AiException MapError(int status, string body, string? voice)
    {
        var detail = ProviderHttp.TryExtractErrorMessage(body);
        var all = body ?? string.Empty;
        bool Says(string s) => all.Contains(s, StringComparison.OrdinalIgnoreCase);
        const string maybeGemini = " If this is a Gemini key from aistudio.google.com, make a Google Cloud key instead (How? in AI Settings ▸ Voices).";

        if (Says("API_KEY_INVALID") || Says("API key not valid") || Says("API_KEY_SERVICE_BLOCKED") || Says("API_KEY_HTTP_REFERRER_BLOCKED"))
            return new(AiErrorKind.Auth, P,
                Says("API_KEY_SERVICE_BLOCKED")
                    ? "This Google key is not allowed to use text-to-speech. In the Google Cloud console, edit the key's API restrictions and tick \"Cloud Text-to-Speech API\"." + maybeGemini
                    : "Google did not accept the API key. Check that it was copied whole, or make a new one in the Google Cloud console (APIs & Services ▸ Credentials).",
                statusCode: status);
        if (Says("SERVICE_DISABLED") || Says("has not been used in project") || Says("it is disabled"))
            return new(AiErrorKind.Auth, P,
                $"Turn on the Cloud Text-to-Speech API for the key's Google Cloud project: open {EnableApiUrl}, choose the project and click Enable. " +
                "Then wait a minute and try again." + maybeGemini, statusCode: status);
        if (Says("BILLING_DISABLED") || Says("billing to be enabled") || Says("billing is disabled"))
            return new(AiErrorKind.Auth, P,
                "Billing is not turned on for the key's Google Cloud project. Text-to-speech needs a billing account, even for the free monthly characters: " +
                "in the Google Cloud console open Billing and link one to the project.", statusCode: status);

        return status switch
        {
            401 or 403 => new(AiErrorKind.Auth, P,
                $"Google refused the request ({status}): {detail} Check the key, or make a new one (How? in AI Settings ▸ Voices).", statusCode: status),
            429 => new(AiErrorKind.RateLimit, P,
                "Google's text-to-speech limit for the project has been reached for now. Wait a minute and try again; " +
                $"if it keeps happening, raise the quota in the Google Cloud console. ({detail})", statusCode: status),
            400 when Says("pronunciation") || Says("phonetic") => new(AiErrorKind.ProviderError, P,
                $"Google could not use a pronunciation (IPA) given for a name: {detail} Check the pronunciation list.", statusCode: status),
            400 when voice is not null && (Says("voice") || Says("name")) && !Says("ssml") => new(AiErrorKind.ProviderError, P,
                $"Google could not use the voice \"{voice}\": {detail} Choose another voice.", statusCode: status),
            400 when Says("ssml") => new(AiErrorKind.ProviderError, P,
                $"Google could not read the text's speech markup (SSML): {detail} Chirp and Journey voices take plain text only; Studio voices refuse <sub> and <phoneme>.",
                statusCode: status),
            400 => new(AiErrorKind.ProviderError, P, $"Google could not speak this text: {detail}", statusCode: status),
            _ => new(AiErrorKind.ProviderError, P, $"Google text-to-speech returned {status}: {detail}", statusCode: status)
        };
    }
}
