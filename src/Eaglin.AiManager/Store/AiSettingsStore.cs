using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eaglin.AiManager.Store;

/// <summary>
/// <c>{root}\settings.json</c>: the global default provider and model per provider, per-app
/// overrides of both, price overrides and an optional monthly budget. Resolution is
/// app → global → built-in default (<see cref="AiProviderRouter.DefaultModels"/>). Nothing
/// secret lives here; the keys are in <see cref="CredentialStore"/>.
/// </summary>
public sealed class AiSettings
{
    public int SchemaVersion { get; set; } = 1;

    public AiProviderType DefaultProvider { get; set; } = AiProviderType.Claude;

    // Populate (not replace) on load so the case-insensitive comparers survive deserialisation.

    /// <summary>Global model per provider, keyed by provider name ("Claude", "Gemini", …).</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, string> Models { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-app overrides, keyed by the app name passed to <see cref="AiHub.Open"/>.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, AppAiSettings> Apps { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>USD per million tokens, keyed by model id; overrides the built-in price table.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, ModelPrice> PriceOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public double? MonthlyBudgetUsd { get; set; }

    public string? ModelFor(AiProviderType p) =>
        Models.TryGetValue(p.ToString(), out var m) && !string.IsNullOrWhiteSpace(m) ? m : null;

    public void SetModel(AiProviderType p, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) Models.Remove(p.ToString());
        else Models[p.ToString()] = model.Trim();
    }

    public AppAiSettings ForApp(string app)
    {
        if (!Apps.TryGetValue(app, out var a)) Apps[app] = a = new AppAiSettings();
        return a;
    }
}

public sealed class AppAiSettings
{
    public AiProviderType? DefaultProvider { get; set; }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, string> Models { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? ModelFor(AiProviderType p) =>
        Models.TryGetValue(p.ToString(), out var m) && !string.IsNullOrWhiteSpace(m) ? m : null;

    public void SetModel(AiProviderType p, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) Models.Remove(p.ToString());
        else Models[p.ToString()] = model.Trim();
    }
}

public sealed record ModelPrice(double InputPerMTok, double OutputPerMTok);

public sealed class AiSettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Path { get; }

    public AiSettingsStore(string root) => Path = AiPaths.SettingsFile(root);

    public AiSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AiSettings();
            var s = JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(Path, Encoding.UTF8), Json);
            if (s is null) return new AiSettings();
            if (s.DefaultProvider == AiProviderType.None) s.DefaultProvider = AiProviderType.Claude;
            return s;
        }
        catch (Exception ex)
        {
            ActivityLog.Error("AiSettingsStore", $"Could not read {Path}; using defaults", ex);
            return new AiSettings();
        }
    }

    public void Save(AiSettings s)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json), Encoding.UTF8);
        File.Move(tmp, Path, overwrite: true);
    }

    /// <summary>The provider an app uses when a request names none: app override, else global.</summary>
    public static AiProviderType ResolveProvider(AiSettings s, string app)
    {
        if (s.Apps.TryGetValue(app, out var a) && a.DefaultProvider is { } p && p != AiProviderType.None) return p;
        return s.DefaultProvider == AiProviderType.None ? AiProviderType.Claude : s.DefaultProvider;
    }

    /// <summary>The model an app uses for a provider: app override, else global, else built-in default.</summary>
    public static string ResolveModel(AiSettings s, string app, AiProviderType p)
    {
        if (s.Apps.TryGetValue(app, out var a) && a.ModelFor(p) is { } am) return am;
        if (s.ModelFor(p) is { } gm) return gm;
        return AiProviderRouter.DefaultModels.TryGetValue(p, out var d) ? d : string.Empty;
    }
}
