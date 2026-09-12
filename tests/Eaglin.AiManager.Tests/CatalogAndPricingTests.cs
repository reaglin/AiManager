using System.Net;
using Eaglin.AiManager.Catalog;
using Eaglin.AiManager.Store;

namespace Eaglin.AiManager.Tests;

public class PricingTests
{
    [Fact]
    public void Exact_and_prefix_matches_price_and_unknown_models_do_not()
    {
        Assert.Equal(new ModelPrice(5, 25), Pricing.PriceFor("claude-opus-5"));
        Assert.Equal(new ModelPrice(5, 25), Pricing.PriceFor("claude-opus-5-20260401"));       // dated id → prefix
        Assert.Equal(new ModelPrice(0.15, 0.60), Pricing.PriceFor("gpt-4o-mini-2024-07-18"));  // longest prefix wins over gpt-4o
        Assert.Equal(new ModelPrice(0.30, 2.50), Pricing.PriceFor("models/gemini-2.5-flash")); // "models/" stripped
        Assert.Null(Pricing.PriceFor("some-new-model"));
        Assert.Null(Pricing.Estimate("some-new-model", 100, 100));
    }

    [Fact]
    public void Overrides_win_and_cost_is_per_million_tokens()
    {
        var overrides = new Dictionary<string, ModelPrice> { ["claude-opus-5"] = new(1, 2) };
        Assert.Equal(new ModelPrice(1, 2), Pricing.PriceFor("claude-opus-5", overrides));
        Assert.Equal(1_000_000 / 1_000_000.0 * 1 + 500_000 / 1_000_000.0 * 2, Pricing.Estimate("claude-opus-5", 1_000_000, 500_000, overrides));
        Assert.Equal(0.0005 + 0.00025, Pricing.Estimate("claude-opus-5", 100, 10)!.Value, 10);
    }
}

public class ModelCatalogTests
{
    private static (ModelCatalog Catalog, FakeHandler Handler, CredentialStore Creds) Make(TempRoot t, string body, params AiProviderType[] keyed)
    {
        var creds = new CredentialStore(t.Path);
        foreach (var p in keyed) creds.SetKey(p, "k", "test");
        var h = new FakeHandler(HttpStatusCode.OK, body);
        return (new ModelCatalog(t.Path, creds, h.Client()), h, creds);
    }

    [Fact]
    public async Task Fallback_without_a_key_and_no_network_call()
    {
        using var t = new TempRoot();
        var (cat, h, _) = Make(t, "{}");
        var list = await cat.ListAsync(AiProviderType.Claude);
        Assert.True(list.FromFallback);
        Assert.Contains(list.Models, m => m.Id == "claude-opus-5");
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Claude_list_is_fetched_cached_and_reused_without_refresh()
    {
        using var t = new TempRoot();
        var (cat, h, creds) = Make(t,
            "{ \"data\": [ { \"id\": \"claude-opus-5\", \"display_name\": \"Claude Opus 5\", \"created_at\": \"2026-04-01T00:00:00Z\" }," +
            "              { \"id\": \"claude-haiku-4-5\", \"display_name\": \"Claude Haiku 4.5\", \"created_at\": \"2025-10-15T00:00:00Z\" } ], \"has_more\": false }",
            AiProviderType.Claude);

        var list = await cat.ListAsync(AiProviderType.Claude);
        Assert.False(list.FromFallback);
        Assert.NotNull(list.FetchedUtc);
        Assert.Equal(new[] { "claude-opus-5", "claude-haiku-4-5" }, list.Models.Select(m => m.Id));   // newest first
        Assert.Equal("Claude Opus 5", list.Models[0].DisplayName);
        Assert.Equal("k", h.LastRequest!.Headers.GetValues("x-api-key").Single());
        Assert.True(File.Exists(AiPaths.ModelsFile(t.Path)));

        var again = await new ModelCatalog(t.Path, creds, h.Client()).ListAsync(AiProviderType.Claude);
        Assert.Single(h.Requests);                        // served from the cache file
        Assert.Equal(list.Models.Select(m => m.Id), again.Models.Select(m => m.Id));

        await cat.ListAsync(AiProviderType.Claude, refresh: true);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task OpenAi_list_keeps_chat_models_only()
    {
        using var t = new TempRoot();
        var (cat, _, _) = Make(t,
            "{ \"data\": [ { \"id\": \"gpt-4o\", \"created\": 1715000000 }, { \"id\": \"text-embedding-3-small\", \"created\": 1 }," +
            "              { \"id\": \"gpt-4o-realtime-preview\", \"created\": 2 }, { \"id\": \"o3-mini\", \"created\": 1737000000 }, { \"id\": \"whisper-1\", \"created\": 3 } ] }",
            AiProviderType.OpenAi);
        var list = await cat.ListAsync(AiProviderType.OpenAi);
        Assert.Equal(new[] { "o3-mini", "gpt-4o" }, list.Models.Select(m => m.Id));
    }

    [Fact]
    public async Task Gemini_list_keeps_generateContent_models()
    {
        using var t = new TempRoot();
        var (cat, _, _) = Make(t,
            "{ \"models\": [ { \"name\": \"models/gemini-2.5-flash\", \"displayName\": \"Gemini 2.5 Flash\", \"supportedGenerationMethods\": [\"generateContent\"] }," +
            "                { \"name\": \"models/embedding-001\", \"displayName\": \"Embedding\", \"supportedGenerationMethods\": [\"embedContent\"] } ] }",
            AiProviderType.Gemini);
        var list = await cat.ListAsync(AiProviderType.Gemini);
        var only = Assert.Single(list.Models);
        Assert.Equal("models/gemini-2.5-flash", only.Id);
        Assert.Equal("Gemini 2.5 Flash", only.DisplayName);
    }

    [Fact]
    public async Task Mistral_and_xai_lists_parse_the_openai_shape()
    {
        using var t = new TempRoot();
        var (cat, _, _) = Make(t,
            "{ \"data\": [ { \"id\": \"mistral-large-latest\", \"capabilities\": { \"completion_chat\": true } }," +
            "              { \"id\": \"mistral-embed\", \"capabilities\": { \"completion_chat\": false } }, { \"id\": \"grok-4\" } ] }",
            AiProviderType.Mistral, AiProviderType.XAi);
        Assert.Equal(new[] { "grok-4", "mistral-large-latest" }, (await cat.ListAsync(AiProviderType.Mistral)).Models.Select(m => m.Id));
        Assert.Equal(3, (await cat.ListAsync(AiProviderType.XAi)).Models.Count);
    }

    [Fact]
    public async Task Failed_fetch_falls_back_quietly_but_throws_on_explicit_refresh()
    {
        using var t = new TempRoot();
        var creds = new CredentialStore(t.Path);
        creds.SetKey(AiProviderType.Claude, "bad", "test");
        var h = new FakeHandler(HttpStatusCode.Unauthorized, "{ \"error\": { \"message\": \"nope\" } }");
        var cat = new ModelCatalog(t.Path, creds, h.Client());

        var list = await cat.ListAsync(AiProviderType.Claude);
        Assert.True(list.FromFallback);

        var ex = await Assert.ThrowsAsync<AiException>(() => cat.ListAsync(AiProviderType.Claude, refresh: true));
        Assert.Equal(AiErrorKind.Auth, ex.Kind);
    }
}
