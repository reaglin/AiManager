using System.Text;
using System.Text.Json;
using Eaglin.AiManager.Catalog;

namespace Eaglin.AiManager.Speech;

/// <summary>
/// Google's voice list on disk (1.8.0): <c>{root}\speech\voices.json</c>. Only what Google said is
/// kept (name, languages, gender, sample rate); the kind and price are worked out again on reading,
/// so a corrected price table applies to a cached list. A missing or unreadable file is "no cache".
/// </summary>
internal static class VoiceCache
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal sealed record Cached(DateTime FetchedUtc, IReadOnlyList<AiVoice> Voices);

    public static Cached? Read(string root)
    {
        var path = AiPaths.VoicesFile(root);
        try
        {
            if (!File.Exists(path)) return null;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path, Encoding.UTF8), Json);
            if (dto?.Voices is null) return null;
            var voices = dto.Voices.Where(v => !string.IsNullOrWhiteSpace(v.Name)).Select(v => new AiVoice
            {
                Name = v.Name!,
                LanguageCodes = v.LanguageCodes ?? [],
                LanguageCode = v.LanguageCodes?.FirstOrDefault() ?? GoogleVoiceNames.Language(v.Name),
                Gender = v.Gender ?? string.Empty,
                Family = GoogleVoiceNames.Family(v.Name),
                PricePerMillionChars = Pricing.SpeechPricePerMillion(v.Name!),
                NaturalSampleRateHertz = v.NaturalSampleRateHertz
            }).ToList();
            return new Cached(dto.FetchedUtc, voices);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("VoiceCache", $"Could not read {path}; fetching again", ex);
            return null;
        }
    }

    public static void Write(string root, IReadOnlyList<AiVoice> voices)
    {
        var path = AiPaths.VoicesFile(root);
        try
        {
            Directory.CreateDirectory(AiPaths.SpeechDir(root));
            var dto = new Dto
            {
                FetchedUtc = DateTime.UtcNow,
                Voices = voices.Select(v => new VoiceDto { Name = v.Name, LanguageCodes = v.LanguageCodes.ToList(), Gender = v.Gender, NaturalSampleRateHertz = v.NaturalSampleRateHertz }).ToList()
            };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Json), Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("VoiceCache", $"Could not write {path}", ex);
        }
    }

    private sealed class Dto
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTime FetchedUtc { get; set; }
        public List<VoiceDto>? Voices { get; set; }
    }

    private sealed class VoiceDto
    {
        public string? Name { get; set; }
        public List<string>? LanguageCodes { get; set; }
        public string? Gender { get; set; }
        public int NaturalSampleRateHertz { get; set; }
    }
}
