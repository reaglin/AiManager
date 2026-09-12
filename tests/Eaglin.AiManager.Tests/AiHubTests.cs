using Eaglin.AiManager.Providers;

namespace Eaglin.AiManager.Tests;

public class AiHubTests
{
    private static AiHub OpenWithKeys(TempRoot t, params AiProviderType[] providers)
    {
        var hub = AiHub.Open("TestApp", t.Path);
        foreach (var p in providers) hub.Credentials.SetKey(p, $"key-{p}", "test");
        hub.Credentials.Save();
        var built = new List<(AiProviderType, string, string)>();
        hub.ProviderFactory = (type, key, model) => { built.Add((type, key, model)); return new FakeProvider(type, model); };
        return hub;
    }

    [Fact]
    public void Open_requires_an_app_name_and_creates_nothing_until_a_key_is_saved()
    {
        using var t = new TempRoot();
        Assert.Throws<ArgumentException>(() => AiHub.Open(" ", t.Path));
        var hub = AiHub.Open("TestApp", t.Path);
        Assert.Equal("TestApp", hub.App);
        Assert.False(File.Exists(AiPaths.CredentialsFile(t.Path)));
        Assert.False(hub.IsAvailable());
        Assert.Empty(hub.ProvidersWithKeys);
    }

    [Fact]
    public async Task Default_provider_and_model_are_resolved_from_settings()
    {
        using var t = new TempRoot();
        var hub = OpenWithKeys(t, AiProviderType.Gemini);
        hub.Settings.DefaultProvider = AiProviderType.Gemini;
        hub.Settings.ForApp("TestApp").SetModel(AiProviderType.Gemini, "models/gemini-2.5-pro");
        hub.SaveSettings();
        hub.Reload();

        Assert.True(hub.IsAvailable());
        var r = await hub.CompleteAsync(new AiRequest { System = "s", User = "u", Purpose = "unit" });
        Assert.Equal(AiProviderType.Gemini, r.Provider);
        Assert.Equal("models/gemini-2.5-pro", r.Model);
        Assert.Equal("answer from Gemini/models/gemini-2.5-pro", r.Text);
        Assert.Equal(10, r.InputTokens);
        Assert.Equal("TestApp", r.App);
        Assert.Equal("unit", r.Purpose);
        Assert.True(r.EstimatedCostUsd > 0);   // gemini-2.5-pro is in the built-in price table
    }

    [Fact]
    public async Task Explicit_provider_and_model_win_and_Completed_fires()
    {
        using var t = new TempRoot();
        var hub = OpenWithKeys(t, AiProviderType.Claude, AiProviderType.XAi);
        AiResponse? seen = null;
        hub.Completed += r => seen = r;

        var r = await hub.CompleteAsync(new AiRequest { Provider = AiProviderType.XAi, Model = "grok-3", User = "u" });
        Assert.Equal(AiProviderType.XAi, r.Provider);
        Assert.Equal("grok-3", r.Model);
        Assert.Same(r, seen);
    }

    [Fact]
    public async Task Missing_key_is_a_NoKey_AiException_naming_the_provider()
    {
        using var t = new TempRoot();
        var hub = OpenWithKeys(t, AiProviderType.Claude);
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            hub.CompleteAsync(new AiRequest { Provider = AiProviderType.OpenAi, User = "u" }));
        Assert.Equal(AiErrorKind.NoKey, ex.Kind);
        Assert.Equal(AiProviderType.OpenAi, ex.Provider);
        Assert.Contains("OpenAI", ex.Message);
    }

    [Fact]
    public async Task CompleteOnAll_returns_one_outcome_per_provider_and_isolates_failures()
    {
        using var t = new TempRoot();
        var hub = OpenWithKeys(t, AiProviderType.Claude, AiProviderType.Mistral, AiProviderType.OpenAi);
        hub.ProviderFactory = (type, key, model) => type == AiProviderType.Mistral
            ? new FakeProvider(type, model) { Throws = new AiException(AiErrorKind.RateLimit, type, "slow down") }
            : new FakeProvider(type, model);

        var outcomes = await hub.CompleteOnAllAsync(new AiRequest { User = "compare" });

        Assert.Equal(new[] { AiProviderType.Claude, AiProviderType.OpenAi, AiProviderType.Mistral }, outcomes.Select(o => o.Provider));
        Assert.True(outcomes[0].Succeeded);
        Assert.True(outcomes[1].Succeeded);
        Assert.False(outcomes[2].Succeeded);
        Assert.Equal(AiErrorKind.RateLimit, outcomes[2].Error!.Kind);
    }

    [Fact]
    public async Task Cancellation_propagates_as_OperationCanceledException()
    {
        using var t = new TempRoot();
        var hub = OpenWithKeys(t, AiProviderType.Claude);
        hub.ProviderFactory = (_, _, m) => new CancellingProvider(m);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            hub.CompleteAsync(new AiRequest { User = "u" }, cts.Token));
    }

    private sealed class CancellingProvider(string model) : IAiProvider
    {
        public AiProviderType Type => AiProviderType.Claude;
        public string ProviderName => "cancel";
        public string Model => model;
        public bool HasApiKey => true;
        public Task<ProviderResult> GenerateAsync(string s, string u, int m, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProviderResult("x", 0, 0, null));
        }
        public IAsyncEnumerable<AiChunk> StreamAsync(string s, string u, int m, CancellationToken ct) => throw new NotSupportedException();
    }
}
