using System.Diagnostics;
using Eaglin.AiManager.Providers;
using Eaglin.AiManager.Store;

namespace Eaglin.AiManager;

/// <summary>
/// The entry point an app uses: <c>var ai = AiHub.Open("AuthorPlus");</c>. Opens the shared
/// store, initialises the activity log for this app, and turns <see cref="AiRequest"/>s into
/// <see cref="AiResponse"/>s using the keys and defaults the user has configured.
/// One instance per app process is the intended use.
/// </summary>
public sealed class AiHub
{
    public string App  { get; }
    public string Root { get; }

    public CredentialStore Credentials   { get; }
    public AiSettingsStore SettingsStore { get; }
    public AiSettings      Settings      { get; private set; }

    /// <summary>Raised after every successful call (the usage ledger and dashboards hook here).</summary>
    public event Action<AiResponse>? Completed;

    /// <summary>Test seam: builds the provider for (type, key, model). Defaults to the real vendors.</summary>
    internal Func<AiProviderType, string, string, IAiProvider> ProviderFactory { get; set; }
        = (t, k, m) => AiProviderRouter.Build(t, k, m);

    private AiHub(string app, string root)
    {
        App  = app;
        Root = root;
        ActivityLog.Initialize(root, app);
        Credentials   = new CredentialStore(root);
        SettingsStore = new AiSettingsStore(root);
        Settings      = SettingsStore.Load();
    }

    /// <param name="app">The calling app's name ("AuthorPlus"). Tags the log, ledger, defaults and templates.</param>
    /// <param name="root">Store folder; null = <see cref="AiPaths.DefaultRoot"/> (Documents\AiManager).</param>
    public static AiHub Open(string app, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(app)) throw new ArgumentException("An app name is required.", nameof(app));
        var hub = new AiHub(app.Trim(), root ?? AiPaths.DefaultRoot);
        ActivityLog.Info("AiHub", $"Opened (keys: {string.Join(", ", hub.ProvidersWithKeys.DefaultIfEmpty())})");
        return hub;
    }

    /// <summary>Re-reads keys and settings from disk — another app may have changed them.</summary>
    public void Reload()
    {
        Credentials.Reload();
        Settings = SettingsStore.Load();
    }

    /// <summary>Persists <see cref="Settings"/> (after the settings UI edited it).</summary>
    public void SaveSettings() => SettingsStore.Save(Settings);

    // ── Availability ──────────────────────────────────────────────────────────

    public AiProviderType DefaultProvider => AiSettingsStore.ResolveProvider(Settings, App);

    public string DefaultModel(AiProviderType provider) => AiSettingsStore.ResolveModel(Settings, App, provider);

    /// <summary>True when the given provider (null = the app default) has a stored key.</summary>
    public bool IsAvailable(AiProviderType? provider = null) => Credentials.HasKey(provider ?? DefaultProvider);

    public IReadOnlyList<AiProviderType> ProvidersWithKeys => Credentials.ProvidersWithKeys;

    public static string DisplayName(AiProviderType provider) => AiProviderRouter.DisplayName(provider);

    // ── Calls ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends one request and returns the answer. Throws <see cref="AiException"/> (see its
    /// <c>Kind</c>) on any failure the user can act on, <see cref="OperationCanceledException"/> on cancel.
    /// </summary>
    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = request.Provider ?? DefaultProvider;
        if (provider == AiProviderType.None)
            throw new AiException(AiErrorKind.NoKey, provider, "No AI provider is selected. Choose one in AI Settings.");

        var key = Credentials.KeyFor(provider);
        if (key.Length == 0)
            throw new AiException(AiErrorKind.NoKey, provider,
                $"No {DisplayName(provider)} API key is stored. Add one in AI Settings.");

        var model = string.IsNullOrWhiteSpace(request.Model) ? DefaultModel(provider) : request.Model!;
        var impl  = ProviderFactory(provider, key, model);
        var purpose = string.IsNullOrWhiteSpace(request.Purpose) ? "(unnamed)" : request.Purpose;

        ActivityLog.Info("AiHub", $"→ {provider}/{model}  purpose={purpose}  max_tokens={request.MaxTokens}");
        var sw = Stopwatch.StartNew();
        ProviderResult result;
        try
        {
            result = await impl.GenerateAsync(request.System, request.User, request.MaxTokens, ct);
        }
        catch (OperationCanceledException)
        {
            ActivityLog.Warn("AiHub", $"✕ {provider}/{model}  purpose={purpose}  cancelled after {sw.Elapsed.TotalSeconds:0.0}s");
            throw;
        }
        catch (AiException ex)
        {
            ActivityLog.Error("AiHub", $"✕ {provider}/{model}  purpose={purpose}  {ex.Kind}: {ex.Message}");
            throw;
        }
        sw.Stop();

        var response = new AiResponse
        {
            Text         = result.Text,
            Provider     = provider,
            Model        = model,
            App          = App,
            Purpose      = request.Purpose,
            InputTokens  = result.InputTokens,
            OutputTokens = result.OutputTokens,
            EstimatedCostUsd = null,          // Pricing arrives with task 1.3
            Elapsed      = sw.Elapsed,
            StopReason   = result.StopReason
        };
        ActivityLog.Info("AiHub", $"✓ {provider}/{model}  purpose={purpose}  in={response.InputTokens}  out={response.OutputTokens}  {sw.Elapsed.TotalSeconds:0.0}s");
        Completed?.Invoke(response);
        return response;
    }

    /// <summary>
    /// Sends the same request to several providers at once (default: every provider with a key)
    /// and returns one outcome per provider, in the order given. A failure on one provider does
    /// not affect the others; cancellation cancels all.
    /// </summary>
    public async Task<IReadOnlyList<AiOutcome>> CompleteOnAllAsync(
        AiRequest request, IEnumerable<AiProviderType>? providers = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var list = (providers ?? ProvidersWithKeys).Distinct().ToList();

        var tasks = list.Select(async p =>
        {
            try
            {
                var r = await CompleteAsync(request.With(p), ct);
                return new AiOutcome { Provider = p, Response = r };
            }
            catch (AiException ex)
            {
                return new AiOutcome { Provider = p, Error = ex };
            }
        });

        return await Task.WhenAll(tasks);
    }
}
