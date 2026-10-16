using System.Globalization;
using Eaglin.AiManager.Legacy;

namespace Eaglin.AiManager.WinForms;

/// <summary>What one press of Test (or Models) came to, in words a first-time user can act on.</summary>
internal sealed record ProviderCheck(bool Ok, string Message);

/// <summary>What the AI Settings form holds when Save is pressed.</summary>
internal sealed class SettingsInput
{
    public Dictionary<AiProviderType, string> Keys   { get; } = new();
    public Dictionary<AiProviderType, string> Models { get; } = new();
    /// <summary>
    /// Providers whose model box the user picked from or typed in. Their model is saved even when
    /// it is the built-in default: choosing the model already shown must stick. A box nobody
    /// touched still leaves the built-in default unpinned, so it follows AI Manager's updates.
    /// </summary>
    public HashSet<AiProviderType> ModelsChosen { get; } = new();
    /// <summary>
    /// "Model for &lt;app&gt;" per provider: a model id, or null for "Same as all programs".
    /// A provider left out is not changed (the form leaves it empty when it shows no such column).
    /// </summary>
    public Dictionary<AiProviderType, string?> AppModels { get; } = new();
    public AiProviderType DefaultProvider { get; set; } = AiProviderType.Claude;
    /// <summary>null = this app follows the default for all programs.</summary>
    public AiProviderType? AppDefaultProvider { get; set; }
    /// <summary>Who makes pictures for all programs (1.7.0); null = automatic.</summary>
    public AiProviderType? ImageProvider { get; set; }
    /// <summary>Who makes pictures for this app; null = the same as all programs.</summary>
    public AiProviderType? AppImageProvider { get; set; }
    public double? MonthlyBudgetUsd { get; set; }
}

/// <summary>
/// Everything the AI Settings form does that is not drawing: reading the store, pushing typed
/// keys into the hub in memory (so an unsaved key can be tested), Save, Cancel, Test, the model
/// list and the legacy import. Kept apart from the form so it can be tested without a window.
/// Edits live in memory until <see cref="Save"/>; <see cref="Discard"/> re-reads the store so a
/// cancelled edit never lingers in the calling app's hub.
/// </summary>
internal sealed class AiSettingsSession
{
    public AiHub Hub { get; }

    public AiSettingsSession(AiHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        Hub = hub;
    }

    public string App => Hub.App;

    /// <summary>Re-reads keys and settings (another program may have changed them).</summary>
    public void Load() => Hub.Reload();

    /// <summary>Throws away anything typed but not saved.</summary>
    public void Discard() => Hub.Reload();

    public string KeyFor(AiProviderType p) => Hub.Credentials.KeyFor(p);

    public string ModelFor(AiProviderType p) =>
        Hub.Settings.ModelFor(p) ?? AiProviderRouter.DefaultModels[p];

    /// <summary>True when the window was opened for a program, so it offers "Model for &lt;app&gt;".</summary>
    public bool ShowsAppModels => Hub.IsOpenedForAnApp;

    /// <summary>This program's own model for a provider, or null for "Same as all programs".</summary>
    public string? AppModelFor(AiProviderType p) => Hub.AppModel(p);

    /// <summary>The model drop-down: the built-in names first, then whatever the vendor listed last time.</summary>
    public IReadOnlyList<string> ModelChoices(AiProviderType p, IEnumerable<string>? fetched = null)
    {
        var cached = fetched ?? Hub.Models.Cached(p)?.Models.Select(m => m.Id) ?? Enumerable.Empty<string>();
        return AiProviderRouter.FallbackModels[p].Concat(cached)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One line under a provider's key: whether a key is saved, when and by which program.</summary>
    public string SavedNote(AiProviderType p) =>
        Hub.Credentials.EntryFor(p) is { } e
            ? $"Key saved{(e.UpdatedUtc is { } u ? " " + u.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "")}{(string.IsNullOrWhiteSpace(e.UpdatedBy) ? "" : " by " + Who(e.UpdatedBy))}. Press Test to check it works."
            : "No key yet. Get one from the link on the left and paste it into the box.";

    private static string Who(string updatedBy) =>
        updatedBy.StartsWith("import:", StringComparison.OrdinalIgnoreCase) ? $"import from {updatedBy["import:".Length..]}" : updatedBy;

    public AiProviderType DefaultProvider => Hub.Settings.DefaultProvider;

    public AiProviderType? AppDefaultProvider =>
        Hub.Settings.Apps.TryGetValue(App, out var a) && a.DefaultProvider is { } p && p != AiProviderType.None ? p : null;

    public double? MonthlyBudgetUsd => Hub.Settings.MonthlyBudgetUsd;

    /// <summary>Who makes pictures for all programs (null = automatic) — 1.7.0.</summary>
    public AiProviderType? ImageProvider =>
        Hub.Settings.ImageProvider is { } p && AiHub.CanGenerateImages(p) ? p : null;

    public AiProviderType? AppImageProvider =>
        Hub.Settings.AppImageProvider(App) is { } p && AiHub.CanGenerateImages(p) ? p : null;

    /// <summary>
    /// What "automatic" would choose now, in words for the settings window ("Now: OpenAI, the provider
    /// CourseBuilder+ uses"), or why nothing can draw.
    /// </summary>
    public string ImageProviderNow()
    {
        try { return "Now: " + Hub.ResolveImageProvider().Reason + "."; }
        catch (AiException ex) { return ex.Message; }
    }

    public double SpentThisMonth => Hub.Usage.SpentThisMonth();

    /// <summary>
    /// Reads the budget box: blank, "0" or "$0" mean no warning; otherwise a non-negative number
    /// of dollars in either the invariant or the user's own number format.
    /// </summary>
    public static bool TryParseBudget(string? text, out double? budget)
    {
        budget = null;
        var t = (text ?? string.Empty).Trim().TrimStart('$').Trim();
        if (t.Length == 0) return true;
        if ((double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ||
             double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out v)) && v >= 0 && !double.IsInfinity(v))
        {
            budget = v == 0 ? null : v;
            return true;
        }
        return false;
    }

    /// <summary>Pushes typed keys into the hub's store in memory (nothing is written).</summary>
    public void ApplyKeys(IReadOnlyDictionary<AiProviderType, string> typed)
    {
        foreach (var (p, key) in typed)
        {
            var k = key?.Trim() ?? string.Empty;
            if (k != Hub.Credentials.KeyFor(p)) Hub.Credentials.SetKey(p, k, App);
        }
    }

    /// <summary>Writes keys and settings to the shared store. Throws IOException/UnauthorizedAccessException if the folder cannot be written.</summary>
    public void Save(SettingsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ApplyKeys(input.Keys);
        foreach (var (p, model) in input.Models)
        {
            var unpinnedDefault = !input.ModelsChosen.Contains(p)
                && string.Equals(model?.Trim(), AiProviderRouter.DefaultModels[p], StringComparison.Ordinal)
                && Hub.Settings.ModelFor(p) is null;
            Hub.Settings.SetModel(p, unpinnedDefault ? null : model);
        }
        foreach (var (p, model) in input.AppModels)
            Hub.Settings.SetAppModel(App, p, model);

        Hub.Settings.DefaultProvider = input.DefaultProvider == AiProviderType.None ? AiProviderType.Claude : input.DefaultProvider;
        var app = Hub.Settings.ForApp(App);
        app.DefaultProvider = input.AppDefaultProvider;
        if (ShowsAppModels) app.ImageProvider = input.AppImageProvider;
        if (app.IsEmpty) Hub.Settings.Apps.Remove(App);
        Hub.Settings.ImageProvider = input.ImageProvider;
        Hub.Settings.MonthlyBudgetUsd = input.MonthlyBudgetUsd;

        Hub.Credentials.Save();
        Hub.SaveSettings();
        ActivityLog.Info("AiSettingsForm", $"Saved (keys: {string.Join(", ", Hub.ProvidersWithKeys.DefaultIfEmpty())})");
        Hub.Reload();
    }

    /// <summary>
    /// Sends a one-word prompt with this key and model — a real call, which costs a fraction of a
    /// cent and is written to the usage ledger as "connection-test". The typed key is used even
    /// if it has not been saved.
    /// </summary>
    public async Task<ProviderCheck> TestAsync(AiProviderType p, string typedKey, string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(typedKey))
            return new ProviderCheck(false, "Paste a key into the box first, then press Test.");
        ApplyKeys(new Dictionary<AiProviderType, string> { [p] = typedKey });
        try
        {
            var r = await Hub.CompleteAsync(new AiRequest
            {
                Provider  = p,
                Model     = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
                System    = "You are a connection test. Reply with exactly the word OK and nothing else.",
                User      = "Connection test.",
                MaxTokens = 16,
                Purpose   = "connection-test"
            }, ct).ConfigureAwait(true);
            return new ProviderCheck(true, $"Works. {AiHub.DisplayName(p)} answered in {r.Elapsed.TotalSeconds:0.0} s using {r.Model}. Press Save to keep this key.");
        }
        catch (AiException ex)
        {
            return new ProviderCheck(false, Explain(ex));
        }
    }

    /// <summary>Fetches the vendor's current model list (uses the typed key).</summary>
    public async Task<(ProviderCheck Check, IReadOnlyList<string> Models)> RefreshModelsAsync(AiProviderType p, string typedKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(typedKey))
            return (new ProviderCheck(false, "The model list comes from the provider, so it needs a key. Paste one first."), ModelChoices(p));
        ApplyKeys(new Dictionary<AiProviderType, string> { [p] = typedKey });
        try
        {
            var list = await Hub.Models.ListAsync(p, refresh: true, ct).ConfigureAwait(true);
            return (new ProviderCheck(true, $"{list.Models.Count} models listed by {AiHub.DisplayName(p)}. Pick one from the Model list."),
                    ModelChoices(p, list.Models.Select(m => m.Id)));
        }
        catch (AiException ex)
        {
            return (new ProviderCheck(false, Explain(ex)), ModelChoices(p));
        }
    }

    /// <summary>An AiException in words that say what to do next.</summary>
    public static string Explain(AiException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var name = AiHub.DisplayName(ex.Provider);
        return ex.Kind switch
        {
            AiErrorKind.NoKey         => "There is no key for this provider. Paste one into the box first.",
            AiErrorKind.Auth          => $"{name} did not accept this key. Check it was copied whole (no spaces). A key that worked before may have expired or been deleted: create a new one (How? beside Get a key shows the steps).",
            _ when ex.Provider == AiProviderType.Claude && ex.StatusCode == 400 && ex.Message.Contains("workspace", StringComparison.OrdinalIgnoreCase)
                                      => $"{name} needs a key made for one workspace. Create a new key and choose Default Workspace (How? beside Get a key shows the steps).",
            AiErrorKind.RateLimit     => $"{name} says the account is over its limit or has no credit left. Check billing on the provider's website, then try again.",
            AiErrorKind.Network       => "Could not reach the provider. Check the internet connection, then try again.",
            AiErrorKind.Timeout       => "The provider took too long to answer. Try again in a minute.",
            AiErrorKind.Refused       => $"{name} declined the test prompt. The key works; try Test again.",
            AiErrorKind.EmptyResponse => $"{name} answered with nothing. The key probably works; try Test again.",
            _ when ex.StatusCode is 400 or 404 && ex.Message.Contains("model", StringComparison.OrdinalIgnoreCase)
                                      => $"{name} does not recognise the name in the Model box. Press Models and pick one from the list, then Test again.",
            _                         => $"{name} reported a problem ({ex.Message}). Check the name in the Model box, then try again later."
        };
    }

    /// <summary>Keys other programs saved before the shared store existed (read only; nothing is changed).</summary>
    public static IReadOnlyList<LegacySource> Discover(LegacyPaths? paths = null) => LegacyCredentials.Discover(paths);

    /// <summary>
    /// Copies the found keys into the hub's store in memory — providers that already have a key
    /// (typed or saved) keep it. Returns the providers that received a key. The caller saves.
    /// </summary>
    public IReadOnlyList<AiProviderType> Import(IReadOnlyDictionary<AiProviderType, string> typed, IEnumerable<LegacySource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ApplyKeys(typed);
        var before = AiProviderRouter.AllProviders.Where(Hub.Credentials.HasKey).ToHashSet();
        foreach (var s in sources) LegacyCredentials.Import(Hub.Credentials, s);
        return AiProviderRouter.AllProviders.Where(p => Hub.Credentials.HasKey(p) && !before.Contains(p)).ToList();
    }

    /// <summary>
    /// The words shown before an import: for each program, its file, and each provider marked
    /// "will be copied" or "already has a key here, kept". A provider found in two programs is
    /// copied from the first only, as <see cref="Import"/> does.
    /// </summary>
    public static (string Text, bool AnyToCopy) DescribeImport(IEnumerable<LegacySource> sources, Func<AiProviderType, bool> hasKeyHere)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(hasKeyHere);
        var taken = new HashSet<AiProviderType>();
        var any = false;
        var parts = new List<string>();
        foreach (var s in sources)
        {
            var lines = new List<string> { $"{s.App}  ({s.Path})" };
            foreach (var p in s.Providers)
            {
                string what;
                if (hasKeyHere(p)) what = "already has a key here, kept";
                else if (!taken.Add(p)) what = "copied from the program above instead";
                else { what = "will be copied"; any = true; }
                lines.Add($"    {AiHub.DisplayName(p)}: {what}");
            }
            parts.Add(string.Join(Environment.NewLine, lines));
        }
        return (string.Join(Environment.NewLine + Environment.NewLine, parts), any);
    }
}
