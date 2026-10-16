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

    /// <summary>Raised after every successful transcription (1.5.0).</summary>
    public event Action<AiTranscript>? Transcribed;

    /// <summary>Test seam: builds the speech-to-text wrapper for (type, key, model). Defaults to the real vendors.</summary>
    internal Func<AiProviderType, string, string, ISpeechToText> SpeechFactory { get; set; }
        = (t, k, m) => t switch
        {
            AiProviderType.OpenAi  => new OpenAiSpeechToText(k, m),
            AiProviderType.Mistral => new MistralSpeechToText(k, m),
            _ => throw new ArgumentException($"{t} cannot transcribe audio.", nameof(t))
        };

    /// <summary>Raised after every successful picture call (1.7.0).</summary>
    public event Action<AiImageResult>? ImagesGenerated;

    /// <summary>Test seam: builds the picture maker for (type, key, model). Defaults to the real vendors.</summary>
    internal Func<AiProviderType, string, string, IImageGenerator> ImageFactory { get; set; }
        = (t, k, m) => t switch
        {
            AiProviderType.OpenAi => new OpenAiImageGenerator(k, m),
            AiProviderType.Gemini => new GeminiImageGenerator(k, m),
            AiProviderType.XAi    => new XAiImageGenerator(k, m),
            _ => throw new ArgumentException($"{t} cannot make pictures.", nameof(t))
        };

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

    // ── Speech to text (1.5.0) ────────────────────────────────────────────────

    /// <summary>The providers that can turn audio into text, in the order they are tried.</summary>
    public static IReadOnlyList<AiProviderType> TranscriptionProviders { get; } = [AiProviderType.OpenAi, AiProviderType.Mistral];

    /// <summary>The largest audio file a transcription sends (OpenAI's limit; Mistral's is larger).</summary>
    public const long MaxTranscriptionBytes = 25L * 1024 * 1024;

    public static bool CanTranscribe(AiProviderType provider) => TranscriptionProviders.Contains(provider);

    /// <summary>The providers that can transcribe and have a stored key.</summary>
    public IReadOnlyList<AiProviderType> TranscriptionProvidersWithKeys =>
        TranscriptionProviders.Where(Credentials.HasKey).ToList();

    /// <summary>
    /// The model a transcription uses when the request names none: whisper-1 for OpenAI (the one
    /// that returns word and segment times), voxtral-mini-latest for Mistral.
    /// </summary>
    public static string DefaultTranscriptionModel(AiProviderType provider) => provider switch
    {
        AiProviderType.OpenAi  => "whisper-1",
        AiProviderType.Mistral => "voxtral-mini-latest",
        _ => string.Empty
    };

    /// <summary>
    /// Turns an audio file into text with times. The provider is the request's, else the app's
    /// default when it can transcribe and has a key, else the first transcription provider with a
    /// key. Throws <see cref="AiException"/> on any failure the user can act on (no key, file too
    /// large, rejected key, network), <see cref="FileNotFoundException"/> for a missing file and
    /// <see cref="OperationCanceledException"/> on cancel. Every call is written to the usage ledger
    /// with its seconds of audio and estimated cost.
    /// </summary>
    public async Task<AiTranscript> TranscribeAsync(AiTranscriptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = ResolveTranscriptionProvider(request.Provider);
        var key = Credentials.KeyFor(provider);
        if (key.Length == 0)
            throw new AiException(AiErrorKind.NoKey, provider,
                $"No {DisplayName(provider)} API key is stored. Add one in AI Settings.");

        var info = new FileInfo(request.AudioPath);
        if (!info.Exists) throw new FileNotFoundException("The audio file to transcribe was not found.", request.AudioPath);
        if (info.Length > MaxTranscriptionBytes)
            throw new AiException(AiErrorKind.ProviderError, provider,
                $"The audio file is too large to send ({info.Length / 1024.0 / 1024.0:0.0} MB; the limit is {MaxTranscriptionBytes / 1024 / 1024} MB). " +
                "Send a shorter recording, or the sound alone in a more compressed file such as MP3.");

        var model   = string.IsNullOrWhiteSpace(request.Model) ? DefaultTranscriptionModel(provider) : request.Model!;
        var purpose = string.IsNullOrWhiteSpace(request.Purpose) ? "(unnamed)" : request.Purpose;
        var impl    = SpeechFactory(provider, key, model);
        var p       = new Called(provider, model, purpose);

        ActivityLog.Info("AiHub", $"→ {provider}/{model}  purpose={purpose}  transcribe {info.Name} ({info.Length / 1024} KB)");
        var sw = Stopwatch.StartNew();
        SpeechResult result;
        try
        {
            result = await impl.TranscribeAsync(info.FullName, request.Language, request.Prompt, request.Words, ct);
        }
        catch (OperationCanceledException)
        {
            Record(p, request.Purpose, sw, 0, 0, ok: false, error: null, streamed: false, cancelled: true, null, null);
            throw;
        }
        catch (AiException ex)
        {
            Record(p, request.Purpose, sw, 0, 0, ok: false, error: ex.Kind.ToString(), streamed: false, cancelled: false, null, null);
            throw;
        }
        sw.Stop();

        var cost = Pricing.EstimateAudio(model, result.AudioSeconds);
        var transcript = new AiTranscript
        {
            Text = result.Text, Segments = result.Segments, Words = result.Words, Language = result.Language,
            AudioSeconds = result.AudioSeconds, Provider = provider, Model = model, App = App, Purpose = request.Purpose,
            EstimatedCostUsd = cost, Elapsed = sw.Elapsed
        };
        ActivityLog.Info("AiHub", $"✓ {provider}/{model}  purpose={purpose}  audio={result.AudioSeconds:0.0}s  segments={result.Segments.Count}  words={result.Words.Count}  cost={(cost is null ? "?" : $"${cost:F4}")}  {sw.Elapsed.TotalSeconds:0.0}s");
        Record(p, request.Purpose, sw, 0, 0, ok: true, error: null, streamed: false, cancelled: false, cost, result.AudioSeconds);
        Transcribed?.Invoke(transcript);
        return transcript;
    }

    private AiProviderType ResolveTranscriptionProvider(AiProviderType? asked)
    {
        if (asked is { } a && a != AiProviderType.None)
        {
            if (!CanTranscribe(a))
                throw new AiException(AiErrorKind.ProviderError, a,
                    $"{DisplayName(a)} cannot transcribe audio; use OpenAI or Mistral AI.");
            return a;
        }
        if (CanTranscribe(DefaultProvider) && Credentials.HasKey(DefaultProvider)) return DefaultProvider;
        return TranscriptionProvidersWithKeys.FirstOrDefault() is { } first && first != AiProviderType.None
            ? first
            : throw new AiException(AiErrorKind.NoKey, AiProviderType.None,
                "Transcribing in the cloud needs an OpenAI or Mistral AI key. Add one in AI Settings.");
    }

    // ── Pictures (1.7.0) ──────────────────────────────────────────────────────

    /// <summary>The providers that can make pictures, in the order they are tried. Claude and Mistral AI cannot.</summary>
    public static IReadOnlyList<AiProviderType> ImageProviders { get; } = [AiProviderType.OpenAi, AiProviderType.Gemini, AiProviderType.XAi];

    /// <summary>The most pictures one request asks for.</summary>
    public const int MaxImagesPerRequest = 4;

    public static bool CanGenerateImages(AiProviderType provider) => ImageProviders.Contains(provider);

    /// <summary>The providers that can make pictures and have a stored key.</summary>
    public IReadOnlyList<AiProviderType> ImageProvidersWithKeys => ImageProviders.Where(Credentials.HasKey).ToList();

    /// <summary>
    /// The built-in picture model for a provider (read from the vendors' docs on 2026-10-16):
    /// OpenAI gpt-image-2 (its price per picture is published), Gemini's "Nano Banana 2.1", xAI Grok Imagine 2.0.
    /// </summary>
    public static string DefaultImageModel(AiProviderType provider) => provider switch
    {
        AiProviderType.OpenAi => "gpt-image-2",
        AiProviderType.Gemini => "gemini-nano-banana-2.1",
        AiProviderType.XAi    => "grok-imagine-image-2.0",
        _ => string.Empty
    };

    /// <summary>The picture model a request to <paramref name="provider"/> uses when it names none: settings.json's ImageModels, else the built-in default.</summary>
    public string ImageModel(AiProviderType provider) => Settings.ImageModelFor(provider) ?? DefaultImageModel(provider);

    /// <summary>
    /// Which provider makes pictures, and why (to tell the user before anything is sent). In order:
    /// the request's provider; this program's "provider for pictures"; the one for all programs; this
    /// program's provider when it can draw and has a key; the first provider that can draw and has a
    /// key. A provider chosen in AI Settings without a key is an error, not a silent switch.
    /// Throws <see cref="AiException"/> (<see cref="AiErrorKind.NoKey"/>) when nothing can draw.
    /// </summary>
    public AiImageProviderChoice ResolveImageProvider(AiProviderType? asked = null)
    {
        if (asked is { } a && a != AiProviderType.None)
        {
            if (!CanGenerateImages(a))
                throw new AiException(AiErrorKind.ProviderError, a,
                    $"{DisplayName(a)} cannot make pictures. Use OpenAI, Google Gemini or xAI.");
            RequireImageKey(a, $"No {DisplayName(a)} API key is stored. Add one in AI Settings.");
            return new AiImageProviderChoice(a, $"{DisplayName(a)}, chosen here");
        }
        if (Settings.AppImageProvider(App) is { } ap && CanGenerateImages(ap))
        {
            RequireImageKey(ap, $"Pictures for {App} are set to {DisplayName(ap)} in AI Settings, but no {DisplayName(ap)} key is " +
                                "stored. Add the key, or choose another provider for pictures.");
            return new AiImageProviderChoice(ap, $"{DisplayName(ap)}, chosen for pictures in {App} (AI Settings)");
        }
        if (Settings.ImageProvider is { } gp && CanGenerateImages(gp))
        {
            RequireImageKey(gp, $"Pictures are set to {DisplayName(gp)} in AI Settings, but no {DisplayName(gp)} key is stored. " +
                                "Add the key, or choose another provider for pictures.");
            return new AiImageProviderChoice(gp, $"{DisplayName(gp)}, chosen for pictures in AI Settings");
        }
        var main = DefaultProvider;
        if (CanGenerateImages(main) && Credentials.HasKey(main))
            return new AiImageProviderChoice(main, $"{DisplayName(main)}, the provider {App} uses");
        if (ImageProvidersWithKeys.FirstOrDefault() is { } first && first != AiProviderType.None)
            return new AiImageProviderChoice(first, CanGenerateImages(main)
                ? $"{DisplayName(first)}, the first provider with a key that can make pictures (there is no {DisplayName(main)} key)"
                : $"{DisplayName(first)}, the first provider with a key that can make pictures ({DisplayName(main)} cannot make pictures)");
        throw new AiException(AiErrorKind.NoKey, AiProviderType.None,
            "Making pictures needs an OpenAI, Google Gemini or xAI key. Claude and Mistral AI cannot make pictures. " +
            "Add a key in AI Settings.");
    }

    private void RequireImageKey(AiProviderType p, string message)
    {
        if (!Credentials.HasKey(p)) throw new AiException(AiErrorKind.NoKey, p, message);
    }

    /// <summary>
    /// The estimated cost of <paramref name="count"/> pictures, from the price table (and the user's
    /// corrections); null when the price is not known. Show it before sending.
    /// </summary>
    public double? EstimateImages(AiProviderType provider, string? model, AiImageQuality quality, int count)
    {
        var m = string.IsNullOrWhiteSpace(model) ? ImageModel(provider) : model!;
        return Pricing.EstimateImage(m, quality, Settings.ImagePriceOverrides) is { } each ? each * Math.Max(0, count) : null;
    }

    /// <summary>
    /// Makes pictures from a description. The provider is chosen by <see cref="ResolveImageProvider"/>
    /// and its reason is in the result. Throws <see cref="AiException"/> on any failure the user can act
    /// on (no key; a key that cannot make pictures yet; a refusal, <see cref="AiErrorKind.Refused"/>;
    /// rate limit; network) and <see cref="OperationCanceledException"/> on cancel. Every call is
    /// written to the usage ledger with the pictures made and their estimated cost.
    /// </summary>
    public async Task<AiImageResult> GenerateImagesAsync(AiImageRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Say what the picture should show.", nameof(request));
        var choice  = ResolveImageProvider(request.Provider);
        var provider = choice.Provider;
        var model   = string.IsNullOrWhiteSpace(request.Model) ? ImageModel(provider) : request.Model!;
        var purpose = string.IsNullOrWhiteSpace(request.Purpose) ? "(unnamed)" : request.Purpose;
        var count   = Math.Clamp(request.Count, 1, MaxImagesPerRequest);
        var impl    = ImageFactory(provider, Credentials.KeyFor(provider), model);
        var p       = new Called(provider, model, purpose);

        ActivityLog.Info("AiHub", $"→ {provider}/{model}  purpose={purpose}  pictures={count}  {request.Shape}  {request.Quality}  ({choice.Reason})");
        var sw = Stopwatch.StartNew();
        IReadOnlyList<AiGeneratedImage> images;
        try
        {
            images = await impl.GenerateAsync(new AiImageRequest
            {
                Provider = provider, Model = model, Prompt = request.Prompt, Style = request.Style,
                Shape = request.Shape, Quality = request.Quality, Count = count, Purpose = request.Purpose
            }, ct);
        }
        catch (OperationCanceledException)
        {
            Record(p, request.Purpose, sw, 0, 0, ok: false, error: null, streamed: false, cancelled: true, null, null);
            throw;
        }
        catch (AiException ex)
        {
            Record(p, request.Purpose, sw, 0, 0, ok: false, error: ex.Kind.ToString(), streamed: false, cancelled: false, null, null);
            throw;
        }
        sw.Stop();

        var cost = EstimateImages(provider, model, request.Quality, images.Count);
        var result = new AiImageResult
        {
            Images = images, Provider = provider, Model = model, App = App, Purpose = request.Purpose, Count = count,
            EstimatedCostUsd = cost, Elapsed = sw.Elapsed, Watermark = impl.Watermark, ProviderReason = choice.Reason
        };
        ActivityLog.Info("AiHub", $"✓ {provider}/{model}  purpose={purpose}  pictures={images.Count}  cost={(cost is null ? "?" : $"${cost:F4}")}  {sw.Elapsed.TotalSeconds:0.0}s");
        Record(p, request.Purpose, sw, 0, 0, ok: true, error: null, streamed: false, cancelled: false, cost, null, images.Count);
        ImagesGenerated?.Invoke(result);
        return result;
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
                        bool ok, string? error, bool streamed, bool cancelled, double? cost = null) =>
        Record(new Called(p.Provider, p.Model, p.Purpose), request.Purpose, sw, inTok, outTok, ok, error, streamed, cancelled, cost, null);

    /// <summary>What the ledger needs to know about a call, prompt or transcription.</summary>
    private sealed record Called(AiProviderType Provider, string Model, string Purpose);

    private void Record(Called p, string requestPurpose, Stopwatch sw, int inTok, int outTok,
                        bool ok, string? error, bool streamed, bool cancelled, double? cost, double? audioSec, int? images = null)
    {
        if (!ok)
            ActivityLog.Warn("AiHub", cancelled
                ? $"✕ {p.Provider}/{p.Model}  purpose={p.Purpose}  cancelled after {sw.Elapsed.TotalSeconds:0.0}s"
                : $"✕ {p.Provider}/{p.Model}  purpose={p.Purpose}  {error}");

        Usage.Append(new UsageRecord
        {
            App = App, Provider = p.Provider, Model = p.Model, Purpose = requestPurpose,
            InTok = inTok, OutTok = outTok,
            CostUsd = cost ?? (inTok + outTok > 0 ? Pricing.Estimate(p.Model, inTok, outTok, Settings.PriceOverrides) : null),
            Ms = sw.ElapsedMilliseconds, Ok = ok, Error = error, Streamed = streamed, Cancelled = cancelled,
            AudioSec = audioSec, Images = images
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
