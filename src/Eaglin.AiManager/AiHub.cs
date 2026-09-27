using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Eaglin.AiManager.Catalog;
using Eaglin.AiManager.Prompts;
using Eaglin.AiManager.Providers;
using Eaglin.AiManager.Store;
using Eaglin.AiManager.Usage;

namespace Eaglin.AiManager;

/// <summary>Raised when month-to-date estimated spend (all apps) passes the budget in settings.</summary>
public sealed record BudgetStatus(double SpentUsd, double BudgetUsd, string Month);

/// <summary>
/// The entry point an app uses: <c>var ai = AiHub.Open("AuthorPlus");</c>. Opens the shared
/// store, initialises the activity log for this app, and turns <see cref="AiRequest"/>s into
/// <see cref="AiResponse"/>s using the keys and defaults the user has configured. Every call,
/// whole or streamed, is written to the usage ledger with its estimated cost.
/// One instance per app process is the intended use.
/// </summary>
public sealed class AiHub
{
    /// <summary>
    /// The name the standalone AI Manager program opens with. A hub opened under this name is not
    /// "opened for an app", so the settings windows show no per-program choices.
    /// </summary>
    public const string ManagerAppName = "AiManager";

    public string App  { get; }
    public string Root { get; }

    public CredentialStore Credentials   { get; }
    public AiSettingsStore SettingsStore { get; }
    public AiSettings      Settings      { get; private set; }
    public UsageLedger     Usage         { get; }
    public PromptLibrary   Prompts       { get; }
    public ModelCatalog    Models        { get; }

    /// <summary>Raised after every successful call (whole or streamed).</summary>
    public event Action<AiResponse>? Completed;

    /// <summary>Raised once per month (per process) when spend passes <see cref="AiSettings.MonthlyBudgetUsd"/>. A warning only; calls still go through.</summary>
    public event Action<BudgetStatus>? BudgetExceeded;

    /// <summary>Test seam: builds the provider for (type, key, model). Defaults to the real vendors.</summary>
    internal Func<AiProviderType, string, string, IAiProvider> ProviderFactory { get; set; }
        = (t, k, m) => AiProviderRouter.Build(t, k, m);

    private string? _budgetWarnedMonth;

    private AiHub(string app, string root, HttpClient? http)
    {
        App  = app;
        Root = root;
        ActivityLog.Initialize(root, app);
        Credentials   = new CredentialStore(root);
        SettingsStore = new AiSettingsStore(root);
        Settings      = SettingsStore.Load();
        Usage         = new UsageLedger(root);
        Prompts       = new PromptLibrary(root, app);
        Models        = new ModelCatalog(root, Credentials, http);
    }

    /// <param name="app">The calling app's name ("AuthorPlus"). Tags the log, ledger, defaults and templates.</param>
    /// <param name="root">Store folder; null = <see cref="AiPaths.DefaultRoot"/> (Documents\AiManager).</param>
    public static AiHub Open(string app, string? root = null) => Open(app, root, null);

    internal static AiHub Open(string app, string? root, HttpClient? http)
    {
        if (string.IsNullOrWhiteSpace(app)) throw new ArgumentException("An app name is required.", nameof(app));
        var hub = new AiHub(app.Trim(), root ?? AiPaths.DefaultRoot, http);
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

    /// <summary>
    /// The model a request to <paramref name="provider"/> uses when it names none: this program's
    /// own choice ("Model for &lt;app&gt;"), else the model for all programs, else the built-in default.
    /// </summary>
    public string DefaultModel(AiProviderType provider) => AiSettingsStore.ResolveModel(Settings, App, provider);

    /// <summary>Where <see cref="DefaultModel"/>'s answer comes from: this program, all programs, or the built-in default.</summary>
    public ModelSource DefaultModelSource(AiProviderType provider) => AiSettingsStore.ResolveModelSource(Settings, App, provider);

    /// <summary>This program's own model for a provider, or null when it uses the model for all programs.</summary>
    public string? AppModel(AiProviderType provider) => Settings.AppModelFor(App, provider);

    /// <summary>True unless this hub belongs to the standalone AI Manager program (see <see cref="ManagerAppName"/>).</summary>
    public bool IsOpenedForAnApp => !string.Equals(App, ManagerAppName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sets (or, with null, clears) this program's own model for a provider and saves it at once:
    /// the way a program changes its choice in code (for example a one-time migration). It
    /// re-reads settings.json first so a change another program just saved is kept, and changes
    /// nothing else. When the file exists but cannot be read, or cannot be written, nothing is
    /// changed and it returns false (the reason is in the activity log). An explicit model sticks
    /// even when it equals the model for all programs or the built-in default.
    /// </summary>
    public bool SetAppModel(AiProviderType provider, string? model)
    {
        if (provider == AiProviderType.None) throw new ArgumentOutOfRangeException(nameof(provider), "Choose a provider.");
        if (!SettingsStore.TryLoadExisting(out var fresh)) return false;
        fresh.SetAppModel(App, provider, model);
        try
        {
            SettingsStore.Save(fresh);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("AiHub", $"Could not save {SettingsStore.Path}; {App}'s model for {provider} not changed", ex);
            return false;
        }
        Settings = fresh;
        ActivityLog.Info("AiHub", $"{App}'s own model for {provider}: {(string.IsNullOrWhiteSpace(model) ? "(same as all programs)" : model.Trim())}");
        return true;
    }

    /// <summary>True when the given provider (null = the app default) has a stored key.</summary>
    public bool IsAvailable(AiProviderType? provider = null) => Credentials.HasKey(provider ?? DefaultProvider);

    public IReadOnlyList<AiProviderType> ProvidersWithKeys => Credentials.ProvidersWithKeys;

    public static string DisplayName(AiProviderType provider) => AiProviderRouter.DisplayName(provider);

    /// <summary>
    /// A rough token count for English prose (about four characters per token) — enough to
    /// show "≈ 12,000 tokens" before sending and to keep a payload under a model's limit.
    /// Vendors count differently; the ledger records the real figures afterwards.
    /// </summary>
    public static int EstimateTokens(string? text) => string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 4.0);

    /// <summary>Estimated cost of sending <paramref name="request"/> to a provider/model, from the price table (input side only; the answer adds to it).</summary>
    public double? EstimateInputCost(AiRequest request, AiProviderType? provider = null, string? model = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var p = provider ?? request.Provider ?? DefaultProvider;
        var m = model ?? request.Model ?? DefaultModel(p);
        return Pricing.Estimate(m, EstimateTokens(request.System) + EstimateTokens(request.User), 0, Settings.PriceOverrides);
    }

    // ── Calls ─────────────────────────────────────────────────────────────────

    private sealed record Prepared(AiProviderType Provider, string Model, IAiProvider Impl, string Purpose);

    private Prepared Prepare(AiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = request.Provider ?? DefaultProvider;
        if (provider == AiProviderType.None)
            throw new AiException(AiErrorKind.NoKey, provider, "No AI provider is selected. Choose one in AI Settings.");

        var key = Credentials.KeyFor(provider);
        if (key.Length == 0)
            throw new AiException(AiErrorKind.NoKey, provider,
                $"No {DisplayName(provider)} API key is stored. Add one in AI Settings.");

        var model   = string.IsNullOrWhiteSpace(request.Model) ? DefaultModel(provider) : request.Model!;
        var purpose = string.IsNullOrWhiteSpace(request.Purpose) ? "(unnamed)" : request.Purpose;
        return new Prepared(provider, model, ProviderFactory(provider, key, model), purpose);
    }

    /// <summary>
    /// Sends one request and returns the whole answer. Throws <see cref="AiException"/> (see its
    /// <c>Kind</c>) on any failure the user can act on, <see cref="OperationCanceledException"/> on cancel.
    /// </summary>
    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        var p = Prepare(request);
        ActivityLog.Info("AiHub", $"→ {p.Provider}/{p.Model}  purpose={p.Purpose}  max_tokens={request.MaxTokens}");
        var sw = Stopwatch.StartNew();
        ProviderResult result;
        try
        {
            result = await p.Impl.GenerateAsync(request.System, request.User, request.MaxTokens, request.Images, ct);
        }
        catch (OperationCanceledException)
        {
            Record(p, request, sw, 0, 0, ok: false, error: null, streamed: false, cancelled: true);
            throw;
        }
        catch (AiException ex)
        {
            Record(p, request, sw, 0, 0, ok: false, error: ex.Kind.ToString(), streamed: false, cancelled: false);
            throw;
        }
        sw.Stop();

        return Finish(p, request, sw, result.Text, result.InputTokens, result.OutputTokens, result.StopReason, streamed: false);
    }

    /// <summary>
    /// Sends one request and yields the answer as it arrives. The final chunk carries usage.
    /// Cancelling the token stops the stream; the partial call is still recorded in the ledger.
    /// </summary>
    public IAsyncEnumerable<AiChunk> StreamAsync(AiRequest request, CancellationToken ct = default) =>
        StreamCoreAsync(request, null, ct);

    /// <summary>
    /// Streams the answer through <paramref name="onChunk"/> (text chunks only) and returns the
    /// finished <see cref="AiResponse"/> — the convenient form for a UI panel.
    /// </summary>
    public async Task<AiResponse> StreamAsync(AiRequest request, Action<AiChunk> onChunk, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onChunk);
        AiResponse? result = null;
        await foreach (var chunk in StreamCoreAsync(request, r => result = r, ct))
            if (!chunk.IsFinal) onChunk(chunk);
        return result ?? throw new AiException(AiErrorKind.EmptyResponse, request.Provider ?? DefaultProvider, "The stream ended without a result.");
    }

    private async IAsyncEnumerable<AiChunk> StreamCoreAsync(AiRequest request, Action<AiResponse>? onDone, [EnumeratorCancellation] CancellationToken ct)
    {
        var p = Prepare(request);
        ActivityLog.Info("AiHub", $"→ {p.Provider}/{p.Model}  purpose={p.Purpose}  max_tokens={request.MaxTokens}  (stream)");
        var sw   = Stopwatch.StartNew();
        var text = new StringBuilder();
        int inTok = 0, outTok = 0;
        string? stop = null;

        var enumerator = p.Impl.StreamAsync(request.System, request.User, request.MaxTokens, request.Images, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                AiChunk chunk;
                try
                {
                    if (!await enumerator.MoveNextAsync()) break;
                    chunk = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    Record(p, request, sw, inTok, outTok, ok: false, error: null, streamed: true, cancelled: true);
                    throw;
                }
                catch (AiException ex)
                {
                    Record(p, request, sw, inTok, outTok, ok: false, error: ex.Kind.ToString(), streamed: true, cancelled: false);
                    throw;
                }

                if (chunk.IsFinal)
                {
                    inTok  = chunk.InputTokens;
                    outTok = chunk.OutputTokens;
                    stop   = chunk.StopReason;
                }
                else text.Append(chunk.Text);

                yield return chunk;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
        sw.Stop();

        var response = Finish(p, request, sw, text.ToString(), inTok, outTok, stop, streamed: true);
        onDone?.Invoke(response);
    }

    /// <summary>Binds a template from the app's prompt library and sends it whole.</summary>
    public Task<AiResponse> RunTemplateAsync(string templateName, object? values, CancellationToken ct = default,
                                             AiProviderType? provider = null, string? model = null) =>
        CompleteAsync(Prompts.Get(templateName).Bind(values, provider, model), ct);

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

    // ── Accounting ────────────────────────────────────────────────────────────

    private AiResponse Finish(Prepared p, AiRequest request, Stopwatch sw, string text, int inTok, int outTok, string? stop, bool streamed)
    {
        var cost = Pricing.Estimate(p.Model, inTok, outTok, Settings.PriceOverrides);
        var response = new AiResponse
        {
            Text             = text,
            Provider         = p.Provider,
            Model            = p.Model,
            App              = App,
            Purpose          = request.Purpose,
            InputTokens      = inTok,
            OutputTokens     = outTok,
            EstimatedCostUsd = cost,
            Elapsed          = sw.Elapsed,
            StopReason       = stop
        };
        ActivityLog.Info("AiHub", $"✓ {p.Provider}/{p.Model}  purpose={p.Purpose}  in={inTok}  out={outTok}  cost={(cost is null ? "?" : $"${cost:F4}")}  {sw.Elapsed.TotalSeconds:0.0}s");
        Record(p, request, sw, inTok, outTok, ok: true, error: null, streamed, cancelled: false, cost);
        Completed?.Invoke(response);
        return response;
    }

    private void Record(Prepared p, AiRequest request, Stopwatch sw, int inTok, int outTok,
                        bool ok, string? error, bool streamed, bool cancelled, double? cost = null)
    {
        if (!ok)
            ActivityLog.Warn("AiHub", cancelled
                ? $"✕ {p.Provider}/{p.Model}  purpose={p.Purpose}  cancelled after {sw.Elapsed.TotalSeconds:0.0}s"
                : $"✕ {p.Provider}/{p.Model}  purpose={p.Purpose}  {error}");

        Usage.Append(new UsageRecord
        {
            App = App, Provider = p.Provider, Model = p.Model, Purpose = request.Purpose,
            InTok = inTok, OutTok = outTok,
            CostUsd = cost ?? (inTok + outTok > 0 ? Pricing.Estimate(p.Model, inTok, outTok, Settings.PriceOverrides) : null),
            Ms = sw.ElapsedMilliseconds, Ok = ok, Error = error, Streamed = streamed, Cancelled = cancelled
        });

        CheckBudget();
    }

    private void CheckBudget()
    {
        if (Settings.MonthlyBudgetUsd is not { } budget || budget <= 0) return;
        var month = DateTime.Now.ToString("yyyy-MM");
        if (_budgetWarnedMonth == month) return;
        var spent = Usage.SpentThisMonth();
        if (spent <= budget) return;
        _budgetWarnedMonth = month;
        ActivityLog.Warn("AiHub", $"Monthly budget exceeded: ${spent:F2} of ${budget:F2} ({month})");
        BudgetExceeded?.Invoke(new BudgetStatus(spent, budget, month));
    }
}
