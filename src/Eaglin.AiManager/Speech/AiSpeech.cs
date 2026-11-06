using Eaglin.AiManager.Catalog;

namespace Eaglin.AiManager;

/// <summary>The kind of sound file text-to-speech returns (1.8.0).</summary>
public enum AiAudioFormat
{
    /// <summary>MP3 — small, plays everywhere. The default.</summary>
    Mp3,
    /// <summary>WAV (16-bit PCM, Google's LINEAR16) — larger, for editing.</summary>
    Wav
}

/// <summary>One Google text-to-speech voice (1.8.0), as listed by <see cref="AiHub.ListVoicesAsync"/>.</summary>
public sealed class AiVoice
{
    /// <summary>Google's name for the voice, the one a request sends: "en-US-Chirp3-HD-Charon".</summary>
    public required string Name { get; init; }

    /// <summary>The first of <see cref="LanguageCodes"/>: "en-US".</summary>
    public required string LanguageCode { get; init; }

    /// <summary>Every language the voice speaks (a few voices speak several).</summary>
    public IReadOnlyList<string> LanguageCodes { get; init; } = [];

    /// <summary>"Male", "Female" or "Neutral" (Google's ssmlGender); empty when not given.</summary>
    public string Gender { get; init; } = "";

    /// <summary>
    /// The kind of voice, parsed from the name: "Chirp 3: HD", "Chirp HD", "Studio", "Neural2",
    /// "WaveNet", "Standard", "Polyglot", "News", "Casual", "Journey" or "Other". The kind sets the price.
    /// </summary>
    public required string Family { get; init; }

    /// <summary>USD per million characters for this kind of voice (from <see cref="Pricing.PerMillionCharacters"/>); null when unknown.</summary>
    public double? PricePerMillionChars { get; init; }

    /// <summary>Google's natural sample rate for the voice (24000 for most); 0 when not given.</summary>
    public int NaturalSampleRateHertz { get; init; }

    /// <summary>The voice's own name within its kind: "Charon" for en-US-Chirp3-HD-Charon, "A" for en-US-Neural2-A.</summary>
    public string Speaker => GoogleVoiceNames.Speaker(Name);

    /// <summary>
    /// False for the Chirp and Journey voices, which take plain text only (SSML is refused). Studio
    /// voices take SSML but not &lt;sub&gt; or &lt;phoneme&gt; (see <see cref="AcceptsSub"/>).
    /// </summary>
    public bool AcceptsSsml => GoogleVoiceNames.AcceptsSsml(Name);

    /// <summary>True when the voice accepts &lt;sub alias&gt; (pronunciations); Studio, Chirp and Journey do not.</summary>
    public bool AcceptsSub => GoogleVoiceNames.AcceptsSub(Name);

    /// <summary>
    /// True when Google takes custom pronunciations (IPA for a phrase) with this voice (1.9.0): the Chirp 3: HD
    /// voices, in en-US for now (Google's API reference: "initial support is for en-us").
    /// </summary>
    public bool AcceptsPronunciations => GoogleVoiceNames.AcceptsPronunciations(Name);

    /// <summary>For a list: "Charon — Chirp 3: HD, male (en-US)".</summary>
    public string Label => Gender.Length == 0
        ? $"{Speaker} — {Family} ({LanguageCode})"
        : $"{Speaker} — {Family}, {Gender.ToLowerInvariant()} ({LanguageCode})";

    public override string ToString() => Label;
}

/// <summary>
/// How a voice should say one phrase (1.9.0): <paramref name="Phrase"/> exactly as it appears in the text (a name,
/// "Olemelukwe"), and its pronunciation in the International Phonetic Alphabet ("oʊlɛmɛˈluːkweɪ").
/// </summary>
public sealed record AiPronunciation(string Phrase, string Ipa);

/// <summary>Text to speak, and how (1.8.0). Everything an app needs to say; nothing about keys or endpoints.</summary>
public sealed class AiSpeechRequest
{
    /// <summary>Plain text, or SSML when <see cref="IsSsml"/>. Any length: long text is sent in pieces and joined.</summary>
    public required string Text { get; init; }

    /// <summary>True when <see cref="Text"/> is SSML (with or without the &lt;speak&gt; wrapper). Chirp and Journey voices refuse SSML.</summary>
    public bool IsSsml { get; init; }

    /// <summary>The voice's name, as <see cref="AiVoice.Name"/>: "en-US-Chirp3-HD-Charon".</summary>
    public required string Voice { get; init; }

    /// <summary>Null = taken from the voice name ("en-US").</summary>
    public string? LanguageCode { get; init; }

    /// <summary>1.0 is normal; 0.25 (slow) to 2.0 (fast). Google accepts up to 4.0.</summary>
    public double SpeakingRate { get; init; } = 1.0;

    public AiAudioFormat Format { get; init; } = AiAudioFormat.Mp3;

    /// <summary>
    /// The sample rate to ask Google for (1.9.0), for example 44100; null = the voice's natural rate (24000 for most).
    /// Sent as <c>audioConfig.sampleRateHertz</c> for MP3 and WAV; a WAV comes back as one 16-bit mono PCM file at this rate.
    /// </summary>
    public int? SampleRateHertz { get; init; }

    /// <summary>
    /// How to say particular names (1.9.0): a phrase exactly as it appears in the text, and its IPA. Sent to Google as
    /// custom pronunciations only when the voice takes them (<see cref="GoogleVoiceNames.AcceptsPronunciations"/>) and the
    /// language is en-US; otherwise ignored (a program that wants a name said right with any voice also respells it in the
    /// text). Each piece of a long text carries only the phrases that occur in it. If Google refuses one, the piece is sent
    /// again without them and <see cref="AiSpeech.PronunciationsIgnored"/> says so.
    /// </summary>
    public IReadOnlyList<AiPronunciation> Pronunciations { get; init; } = [];

    /// <summary>Free text naming what the call is for ("chapter-audio"). Lands in the log and the usage ledger.</summary>
    public string Purpose { get; init; } = "";
}

/// <summary>What text-to-speech returned (1.8.0): one playable file, with the accounting an app may want to show.</summary>
public sealed class AiSpeech
{
    /// <summary>One playable file: MP3 pieces joined end to end, or one WAV with the pieces' sound merged under one header.</summary>
    public required byte[] Audio { get; init; }
    public AiAudioFormat Format { get; init; }
    /// <summary>Characters billed (every piece sent, SSML tags included).</summary>
    public int Characters { get; init; }
    /// <summary>Requests sent (pieces of the text).</summary>
    public int Requests { get; init; }
    /// <summary>Price × characters, ignoring Google's free monthly characters; null when the voice's price is unknown.</summary>
    public double? EstimatedCostUsd { get; init; }
    public required string Voice { get; init; }
    public required string App { get; init; }
    public string Purpose { get; init; } = "";
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// True when Google refused the custom pronunciations for at least one piece (1.9.0) and that piece was spoken without
    /// them — so a program can tell the author that a pronunciation in its list needs looking at.
    /// </summary>
    public bool PronunciationsIgnored { get; init; }

    public DateTime CompletedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>".mp3" or ".wav", for saving <see cref="Audio"/> to a file.</summary>
    public string FileExtension => Format == AiAudioFormat.Wav ? ".wav" : ".mp3";
}

/// <summary>What Google's voice names say about a voice (1.8.0): its kind, its own name, what markup it takes.</summary>
public static class GoogleVoiceNames
{
    /// <summary>"en-US-Chirp3-HD-Charon" → "Chirp 3: HD"; see <see cref="AiVoice.Family"/>.</summary>
    public static string Family(string? name)
    {
        var n = name ?? string.Empty;
        bool Has(string part) => n.Contains(part, StringComparison.OrdinalIgnoreCase);
        if (Has("-Chirp3-HD-"))  return "Chirp 3: HD";
        if (Has("-Chirp-HD-"))   return "Chirp HD";
        if (Has("-Studio-"))     return "Studio";
        if (Has("-Neural2-"))    return "Neural2";
        if (Has("-Wavenet-"))    return "WaveNet";
        if (Has("-Standard-"))   return "Standard";
        if (Has("-Polyglot-"))   return "Polyglot";
        if (Has("-News-"))       return "News";
        if (Has("-Casual-"))     return "Casual";
        if (Has("-Journey-"))    return "Journey";
        return "Other";
    }

    /// <summary>The language a voice name starts with: "en-US-Neural2-A" → "en-US"; "" when the name has no language.</summary>
    public static string Language(string? name)
    {
        var parts = (name ?? string.Empty).Split('-');
        return parts.Length >= 3 && parts[0].Length is 2 or 3 && parts[1].Length >= 2 ? $"{parts[0]}-{parts[1]}" : string.Empty;
    }

    /// <summary>The part after the kind: "Charon" for en-US-Chirp3-HD-Charon, "A" for en-US-Neural2-A.</summary>
    public static string Speaker(string? name)
    {
        var n = name ?? string.Empty;
        foreach (var marker in new[] { "-Chirp3-HD-", "-Chirp-HD-", "-Studio-", "-Neural2-", "-Wavenet-", "-Standard-", "-Polyglot-", "-News-", "-Casual-", "-Journey-" })
        {
            var i = n.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return n[(i + marker.Length)..];
        }
        var lang = Language(n);
        return lang.Length > 0 && n.Length > lang.Length + 1 ? n[(lang.Length + 1)..] : n;
    }

    /// <summary>False for the Chirp and Journey voices (plain text only).</summary>
    public static bool AcceptsSsml(string? name) => Family(name) is not ("Chirp 3: HD" or "Chirp HD" or "Journey");

    /// <summary>&lt;sub&gt; is refused by Studio voices and by every voice that takes no SSML.</summary>
    public static bool AcceptsSub(string? name) => AcceptsSsml(name) && Family(name) != "Studio";

    /// <summary>
    /// True for the voices Google takes custom pronunciations with (1.9.0): Chirp 3: HD voices in en-US. Google's API
    /// reference: "The initial support is for en-us, with plans to expand to other locales in the future."
    /// </summary>
    public static bool AcceptsPronunciations(string voiceName) =>
        Family(voiceName) == "Chirp 3: HD" && PronunciationLanguage(Language(voiceName));

    /// <summary>The languages Google takes custom pronunciations in: en-US only, for now.</summary>
    internal static bool PronunciationLanguage(string? languageCode) =>
        string.Equals(languageCode?.Trim(), "en-US", StringComparison.OrdinalIgnoreCase);
}
