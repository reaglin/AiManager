using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eaglin.AiManager.Catalog;

namespace Eaglin.AiManager.Store;

/// <summary>
/// <c>{root}\settings.json</c>: the global default provider and model per provider, per-app
/// overrides of both, price overrides and an optional monthly budget. Resolution is
/// app → global → built-in default (<see cref="AiProviderRouter.DefaultModels"/>). Nothing
/// secret lives here; the keys are in <see cref="CredentialStore"/>.
/// Keys this version does not know (written by a newer one) are kept on save, never dropped.
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

    /// <summary>
    /// The provider that makes pictures for every program that has not chosen its own (1.7.0); null =
    /// automatic (the program's provider when it can draw, else the first that can draw and has a key).
    /// Only OpenAI, Gemini and xAI can draw.
    /// </summary>
    public AiProviderType? ImageProvider { get; set; }

    /// <summary>Picture model per provider (1.7.0), keyed by provider name; absent = the built-in default (<see cref="AiHub.DefaultImageModel"/>).</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, string> ImageModels { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>USD per picture, keyed by model id; overrides the built-in table (<see cref="Pricing.PerImage"/>).</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, ImagePrice> ImagePriceOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? ImageModelFor(AiProviderType p) =>
        ImageModels.TryGetValue(p.ToString(), out var m) && !string.IsNullOrWhiteSpace(m) ? m : null;

    /// <summary>One program's own provider for pictures, or null when it follows the one for all programs.</summary>
    public AiProviderType? AppImageProvider(string app) =>
        Apps.TryGetValue(app, out var a) && a.ImageProvider is { } p && p != AiProviderType.None ? p : null;

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

    /// <summary>
    /// The model one program has chosen for itself for a provider, or null when it follows the
    /// model for all programs ("Same as all programs").
    /// </summary>
    public string? AppModelFor(string app, AiProviderType p) =>
        Apps.TryGetValue(app, out var a) ? a.ModelFor(p) : null;

    /// <summary>
    /// Sets (or, with null or blank, clears) one program's own model for a provider. The value is
    /// kept as given even when it equals the model for all programs or the built-in default: an
    /// explicit choice sticks. Clearing the last thing an app entry holds removes the empty entry.
    /// Changes memory only; <see cref="AiSettingsStore.Save"/> (or <see cref="AiHub.SetAppModel"/>) writes it.
    /// </summary>
    public void SetAppModel(string app, AiProviderType p, string? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        if (string.IsNullOrWhiteSpace(model))
        {
            if (!Apps.TryGetValue(app, out var a)) return;
            a.SetModel(p, null);
            if (a.IsEmpty) Apps.Remove(app);
            return;
        }
        ForApp(app).SetModel(p, model);
    }

    /// <summary>Settings a newer version wrote that this one does not know; written back unchanged.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AppAiSettings
{
    public AiProviderType? DefaultProvider { get; set; }

    /// <summary>This program's provider for pictures (1.7.0); null = the one for all programs.</summary>
    public AiProviderType? ImageProvider { get; set; }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, string> Models { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? ModelFor(AiProviderType p) =>
        Models.TryGetValue(p.ToString(), out var m) && !string.IsNullOrWhiteSpace(m) ? m : null;

    public void SetModel(AiProviderType p, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) Models.Remove(p.ToString());
        else Models[p.ToString()] = model.Trim();
    }

    /// <summary>True when the entry holds nothing (no provider, no models, nothing from a newer version).</summary>
    [JsonIgnore]
    public bool IsEmpty => DefaultProvider is null && ImageProvider is null && Models.Count == 0 && (Extra is null || Extra.Count == 0);

    /// <summary>Settings a newer version wrote that this one does not know; written back unchanged.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Where the model a program uses for a provider comes from.</summary>
public enum ModelSource
{
    /// <summary>The program chose its own model ("Model for &lt;app&gt;").</summary>
    App,
    /// <summary>The model chosen for all programs.</summary>
    AllPrograms,
    /// <summary>Nothing was chosen; the built-in default for the provider.</summary>
    BuiltIn
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
        if (s.AppModelFor(app, p) is { } am) return am;
        if (s.ModelFor(p) is { } gm) return gm;
        return AiProviderRouter.DefaultModels.TryGetValue(p, out var d) ? d : string.Empty;
    }

    /// <summary>Where <see cref="ResolveModel"/>'s answer comes from, for words such as "its own choice".</summary>
    public static ModelSource ResolveModelSource(AiSettings s, string app, AiProviderType p) =>
        s.AppModelFor(app, p) is not null ? ModelSource.App
        : s.ModelFor(p) is not null ? ModelSource.AllPrograms
        : ModelSource.BuiltIn;

    /// <summary>
    /// Like <see cref="Load"/>, but refuses to stand in defaults for a file that exists and cannot
    /// be read: a change made in code must never write defaults over someone's settings.
    /// </summary>
    internal bool TryLoadExisting(out AiSettings settings)
    {
        settings = new AiSettings();
        try
        {
            if (!File.Exists(Path)) return true;
            var s = JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(Path, Encoding.UTF8), Json);
            if (s is null) return false;
            if (s.DefaultProvider == AiProviderType.None) s.DefaultProvider = AiProviderType.Claude;
            settings = s;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            ActivityLog.Error("AiSettingsStore", $"Could not read {Path}; nothing changed", ex);
            return false;
        }
    }
}
