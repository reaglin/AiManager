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

    public string DefaultModel(AiProviderType provider) => AiSettingsStore.ResolveModel(Settings, App, provider);

    /// <summary>True when the given provider (null = the app default) has a stored key.</summary>
    public bool IsAvailable(AiProviderType? provider = null) => Credentials.HasKey(provider ?? DefaultProvider);

    public IReadOnlyList<AiProviderType> ProvidersWithKeys => Credentials.ProvidersWithKeys;

    public static string DisplayName(AiProviderType provider) => AiProviderRouter.DisplayName(provider);

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
            result = await p.Impl.GenerateAsync(request.System, request.User, request.MaxTokens, ct);
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
    public async IAsyncEnumerable<AiChunk> StreamAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var p = Prepare(request);
        ActivityLog.Info("AiHub", $"→ {p.Provider}/{p.Model}  purpose={p.Purpose}  max_tokens={request.MaxTokens}  (stream)");
        var sw   = Stopwatch.StartNew();
        var text = new StringBuilder();
        int inTok = 0, outTok = 0;
        string? stop = null;

        var enumerator = p.Impl.StreamAsync(request.System, request.User, request.MaxTokens, ct).GetAsyncEnumerator(ct);
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

        Finish(p, request, sw, text.ToString(), inTok, outTok, stop, streamed: true);
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
