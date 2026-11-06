using Eaglin.AiManager.Store;

namespace Eaglin.AiManager.Catalog;

/// <summary>
/// Estimated cost per call. Prices are USD per million tokens (input, output) and are matched
/// by the longest prefix of the model id, so dated or suffixed ids still price. The user can
/// correct or add prices in <c>settings.json</c> (<see cref="AiSettings.PriceOverrides"/>);
/// an override matches exactly first, then by prefix. Every figure the library produces from
/// this table is an <em>estimate</em> and is labelled so in the UIs.
/// </summary>
public static class Pricing
{
    /// <summary>Built-in table, as of 2026-09-12. Anthropic figures are from the current price list; the others are the vendors' published list prices at the time and should be checked when a bill matters.</summary>
    public static readonly IReadOnlyList<(string Prefix, ModelPrice Price)> BuiltIn = new (string, ModelPrice)[]
    {
        ("claude-fable-5-1",   new(10.00, 50.00)),
        ("claude-fable-5",     new(10.00, 50.00)),
        ("claude-opus-5",      new( 5.00, 25.00)),
        ("claude-opus-4",      new( 5.00, 25.00)),
        ("claude-sonnet-5",    new( 2.00, 10.00)),
        ("claude-sonnet-4-6",  new( 3.00, 15.00)),
        ("claude-haiku-4-5",   new( 1.00,  5.00)),
        ("gpt-4o-mini",        new( 0.15,  0.60)),
        ("gpt-4o",             new( 2.50, 10.00)),
        ("gemini-2.5-flash",   new( 0.30,  2.50)),
        ("gemini-2.5-pro",     new( 1.25, 10.00)),
        ("mistral-large",      new( 2.00,  6.00)),
        ("mistral-medium",     new( 0.40,  2.00)),
        ("mistral-small",      new( 0.10,  0.30)),
        ("grok-4",             new( 3.00, 15.00)),
        ("grok-3-mini",        new( 0.30,  0.50)),
        ("grok-3",             new( 3.00, 15.00))
    };

    /// <summary>
    /// Speech-to-text prices, USD per minute of audio (1.5.0), matched by the longest prefix like
    /// the token table. Vendors' list prices as of 2026-10-10.
    /// </summary>
    public static readonly IReadOnlyList<(string Prefix, double UsdPerMinute)> PerMinute = new (string, double)[]
    {
        ("whisper-1",              0.006),
        ("gpt-4o-transcribe",      0.006),
        ("gpt-4o-mini-transcribe", 0.003),
        ("voxtral-mini",           0.003)
    };

    /// <summary>Estimated cost of transcribing <paramref name="seconds"/> of audio; null when the model has no per-minute price.</summary>
    public static double? EstimateAudio(string model, double seconds)
    {
        var id = Normalize(model);
        double? found = null; int foundLen = -1;
        foreach (var (prefix, perMinute) in PerMinute)
            if (id.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > foundLen) { found = perMinute; foundLen = prefix.Length; }
        return found is { } p ? Math.Max(0, seconds) / 60.0 * p : null;
    }

    /// <summary>
    /// Picture prices, USD per picture at each quality (1.7.0), matched by the longest prefix of the
    /// model id. A null price means "not known" — it is shown as "price unknown", never as $0; a
    /// prefix with all prices null stops a shorter prefix from pricing a different model
    /// ("gpt-image-2.5" must not take "gpt-image-2"'s price). The vendors' list prices read on
    /// 2026-10-16 (OpenAI at 1536×1024, Gemini 1K / 2K, xAI flat) — check them when a bill matters.
    /// </summary>
    public static readonly IReadOnlyList<(string Prefix, ImagePrice Price)> PerImage = new (string, ImagePrice)[]
    {
        ("gpt-image-2",                   new(0.005, 0.041, 0.165)),
        ("gpt-image-2.5",                 new(null,  null,  null)),    // not in OpenAI's per-picture table yet
        ("gpt-image-1.5",                 new(null,  0.05,  null)),
        ("gpt-image-1-mini",              new(null,  0.015, null)),
        ("gpt-image-1",                   new(null,  null,  null)),
        ("gemini-nano-banana-2.1",        new(0.0336, 0.0336, 0.0504)),
        ("gemini-3.1-flash-image",        new(0.067, 0.067, 0.101)),
        ("gemini-3.1-flash-lite-image",   new(null,  null,  null)),
        ("gemini-3-pro-image",            new(0.134, 0.134, 0.134)),
        ("gemini-2.5-flash-image",        new(0.039, 0.039, 0.039)),
        ("grok-imagine-image-2.0",        new(0.04,  0.04,  0.04)),
        ("grok-imagine-image-quality",    new(0.05,  0.05,  0.05)),
        ("grok-imagine-image-pro",        new(null,  null,  null)),    // retired by xAI (May 2026)
        ("grok-imagine-image",            new(0.02,  0.02,  0.02))
    };

    /// <summary>Estimated cost of one picture; null when the price is not known. An override (longest prefix) wins over the table.</summary>
    public static double? EstimateImage(string model, AiImageQuality quality, IReadOnlyDictionary<string, ImagePrice>? overrides = null)
    {
        var id = Normalize(model);
        if (id.Length == 0) return null;
        ImagePrice? found = null; int foundLen = -1;
        if (overrides is { Count: > 0 })
            foreach (var (key, price) in overrides)
            {
                var k = Normalize(key);
                if (id.StartsWith(k, StringComparison.Ordinal) && k.Length > foundLen) { found = price; foundLen = k.Length; }
            }
        if (found is null)
            foreach (var (prefix, price) in PerImage)
                if (id.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > foundLen) { found = price; foundLen = prefix.Length; }
        return found?.For(quality);
    }

    /// <summary>
    /// Text-to-speech prices (1.8.0), USD per million characters, by kind of Google voice
    /// (<see cref="AiVoice.Family"/>). Google's list prices as known on 2026-11-06 (the pricing page
    /// could not be read automatically that day — check https://cloud.google.com/text-to-speech/pricing
    /// when a bill matters). Google also gives free characters every month, which these estimates
    /// ignore: Standard and WaveNet 4 million; Neural2, Polyglot, Studio and Chirp 3: HD 1 million.
    /// </summary>
    public static readonly IReadOnlyList<(string Family, double UsdPerMillionChars)> PerMillionCharacters = new (string, double)[]
    {
        ("Chirp 3: HD",  30.00),
        ("Chirp HD",     30.00),
        ("Journey",      30.00),
        ("Studio",      160.00),
        ("Neural2",      16.00),
        ("Polyglot",     16.00),
        ("News",         16.00),
        ("Casual",       16.00),
        ("WaveNet",       4.00),
        ("Standard",      4.00)
    };

    /// <summary>USD per million characters for a Google voice (by its kind); null when the kind has no price here.</summary>
    public static double? SpeechPricePerMillion(string voice)
    {
        var family = GoogleVoiceNames.Family(voice);
        foreach (var (f, price) in PerMillionCharacters)
            if (f == family) return price;
        return null;
    }

    /// <summary>Estimated cost of speaking <paramref name="characters"/> characters with a voice, ignoring the free monthly characters; null when the price is unknown.</summary>
    public static double? EstimateSpeech(string voice, int characters) =>
        SpeechPricePerMillion(voice) is { } p ? Math.Max(0, characters) / 1_000_000.0 * p : null;

    /// <summary>"models/gemini-2.5-flash" → "gemini-2.5-flash"; case-insensitive matching.</summary>
    public static string Normalize(string model)
    {
        var m = (model ?? string.Empty).Trim().ToLowerInvariant();
        if (m.StartsWith("models/", StringComparison.Ordinal)) m = m["models/".Length..];
        return m;
    }

    public static ModelPrice? PriceFor(string model, IReadOnlyDictionary<string, ModelPrice>? overrides = null)
    {
        var id = Normalize(model);
        if (id.Length == 0) return null;

        if (overrides is { Count: > 0 })
        {
            ModelPrice? best = null; int bestLen = -1;
            foreach (var (key, price) in overrides)
            {
                var k = Normalize(key);
                if (id.StartsWith(k, StringComparison.Ordinal) && k.Length > bestLen) { best = price; bestLen = k.Length; }
            }
            if (best is not null) return best;
        }

        ModelPrice? found = null; int foundLen = -1;
        foreach (var (prefix, price) in BuiltIn)
            if (id.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > foundLen) { found = price; foundLen = prefix.Length; }
        return found;
    }

    /// <summary>Null when the model has no price; tokens are still worth recording.</summary>
    public static double? Estimate(string model, int inputTokens, int outputTokens, IReadOnlyDictionary<string, ModelPrice>? overrides = null)
    {
        var p = PriceFor(model, overrides);
        if (p is null) return null;
        return inputTokens / 1_000_000.0 * p.InputPerMTok + outputTokens / 1_000_000.0 * p.OutputPerMTok;
    }
}

/// <summary>USD per picture at each quality (1.7.0); null = not known.</summary>
public sealed record ImagePrice(double? Draft, double? Standard, double? Best)
{
    public double? For(AiImageQuality q) => q switch
    {
        AiImageQuality.Draft => Draft,
        AiImageQuality.Best  => Best,
        _                    => Standard
    };
}
