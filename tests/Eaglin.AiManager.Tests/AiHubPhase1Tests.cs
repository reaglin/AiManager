using Eaglin.AiManager.Prompts;
using Eaglin.AiManager.Store;

namespace Eaglin.AiManager.Tests;

public class AiHubPhase1Tests
{
    private static AiHub OpenWithKeys(TempRoot t, params AiProviderType[] providers)
    {
        var hub = AiHub.Open("TestApp", t.Path);
        foreach (var p in providers) hub.Credentials.SetKey(p, $"key-{p}", "test");
        hub.Credentials.Save();
        hub.ProviderFactory = (type, key, model) => new FakeProvider(type, model);
        return hub;
    }

    [Fact]
    public async Task Whole_call_is_priced_and_written_to_the_ledger()
    {
        using var t = new TempRoot();
        var hub = OpenWithKeys(t, AiProviderType.Claude);
        hub.ProviderFactory = (type, key, model) => new FakeProvider(type, model) { InputTokens = 1_000_000, OutputTokens = 100_000 };

        var r = await hub.CompleteAsync(new AiRequest { Model = "claude-opus-5", User = "u", Purpose = "unit" });

        Assert.Equal(5.0 + 2.5, r.EstimatedCostUsd!.Value, 6);
        var rec = Assert.Single(hub.Usage.Read(DateTime.Now));
        Assert.Equal("TestApp", rec.App);
        Assert.Equal("unit", rec.Purpose);
        Assert.True(rec.Ok);
        Assert.False(rec.Streamed);
        Assert.Equal(7.5, rec.CostUsd!.Value, 6);
        Assert.Equal(1_000_000, rec.InTok);
    }

    [Fact]
    public async Task Price_overrides_from_settings_apply()
    {
        using var t = new TempRoot();
        var hub = OpenWithKeys(t, AiProviderType.XAi);
        hub.Settings.PriceOverrides["mystery-model"] = new ModelPrice(100, 200);
        hub.ProviderFactory = (type, key, model) => new FakeProvider(type, model) { InputTokens = 10_000, OutputTokens = 10_000 };

        var r = await hub.CompleteAsync(new AiRequest { Provider = AiProviderType.XAi, Model = "mystery-model", User = "u" });
        Assert.Equal(1.0 + 2.0, r.EstimatedCostUsd!.Value, 6);
    }

    [Fact]
    public async Task Failures_are_recorded_with_their_error_kind()
    {
        using var t = new TempRoot();
        var hub = OpenWithKeys(t, AiProviderType.Claude);
        hub.ProviderFactory = (type, key, model) => new FakeProvider(type, model) { Throws = new AiException(AiErrorKind.RateLimit, type, "slow") };

        await Assert.ThrowsAsync<AiException>(() => hub.CompleteAsync(new AiRequest { User = "u" }));

        var rec = Assert.Single(hub.Usage.Read(DateTime.Now));
        Assert.False(rec.Ok);
        Assert.Equal("RateLimit", rec.Error);
        Assert.Null(rec.CostUsd);
    }

    [Fact]
    public async Task Streaming_yields_chunks_then_a_final_chunk_and_records_the_call()
    {
        using var t = new TempRoot();
        var hub = OpenWithKeys(t, AiProviderType.Claude);
        hub.ProviderFactory = (type, key, model) => new FakeProvider(type, model) { Answer = "streamed answer", InputTokens = 40, OutputTokens = 4 };
        AiResponse? completed = null;
        hub.Completed += r => completed = r;

        var chunks = new List<AiChunk>();
        await foreach (var c in hub.StreamAsync(new AiRequest { Model = "claude-opus-5", User = "u", Purpose = "stream-unit" }))
            chunks.Add(c);

        Assert.Equal("streamed answer", string.Concat(chunks.Where(c => !c.IsFinal).Select(c => c.Text)));
        Assert.True(chunks[^1].IsFinal);
        Assert.Equal(40, chunks[^1].InputTokens);

        Assert.NotNull(completed);
        Assert.Equal("streamed answer", completed!.Text);
        Assert.Equal(4, completed.OutputTokens);
        Assert.NotNull(completed.EstimatedCostUsd);

        var rec = Assert.Single(hub.Usage.Read(DateTime.Now));
        Assert.True(rec.Streamed);
        Assert.True(rec.Ok);
        Assert.Equal("stream-unit", rec.Purpose);
    }

    [Fact]
    public async Task Cancelled_stream_is_recorded_as_cancelled_and_rethrown()
    {
        using var t = new TempRoot();
        var hub = OpenWithKeys(t, AiProviderType.Claude);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var c in hub.StreamAsync(new AiRequest { User = "u" }, cts.Token))
                cts.Cancel();          // cancel after the first chunk; the fake checks the token before the second
        });

        var rec = Assert.Single(hub.Usage.Read(DateTime.Now));
        Assert.True(rec.Cancelled);
        Assert.True(rec.Streamed);
        Assert.False(rec.Ok);
    }

    [Fact]
    public async Task RunTemplate_binds_the_library_template_and_uses_its_name_as_purpose()
    {
        using var t = new TempRoot();
        var hub = OpenWithKeys(t, AiProviderType.Claude);
        FakeProvider? used = null;
        hub.ProviderFactory = (type, key, model) => used = new FakeProvider(type, model);
        hub.Prompts.RegisterDefaults(new[] { new PromptTemplate("greet", "Be brief.", "Say hello to {name}.") });

        var r = await hub.RunTemplateAsync("greet", new { name = "Ron" });

        Assert.Equal("Say hello to Ron.", used!.LastUser);
        Assert.Equal("greet", r.Purpose);
        Assert.Equal("greet", hub.Usage.Read(DateTime.Now).Single().Purpose);
    }

    [Fact]
    public async Task Budget_warning_fires_once_when_month_to_date_spend_passes_the_budget()
    {
        using var t = new TempRoot();
        var hub = OpenWithKeys(t, AiProviderType.Claude);
        hub.Settings.MonthlyBudgetUsd = 1.0;
        hub.ProviderFactory = (type, key, model) => new FakeProvider(type, model) { InputTokens = 100_000, OutputTokens = 0 };  // $0.50 each on opus-5
        var warnings = new List<BudgetStatus>();
        hub.BudgetExceeded += s => warnings.Add(s);

        await hub.CompleteAsync(new AiRequest { Model = "claude-opus-5", User = "u" });   // 0.50
        await hub.CompleteAsync(new AiRequest { Model = "claude-opus-5", User = "u" });   // 1.00 — not over yet
        Assert.Empty(warnings);
        await hub.CompleteAsync(new AiRequest { Model = "claude-opus-5", User = "u" });   // 1.50 — over
        await hub.CompleteAsync(new AiRequest { Model = "claude-opus-5", User = "u" });   // still over, no repeat

        var w = Assert.Single(warnings);
        Assert.Equal(1.0, w.BudgetUsd);
        Assert.Equal(1.5, w.SpentUsd, 6);
        Assert.Equal(DateTime.Now.ToString("yyyy-MM"), w.Month);
    }
}

public class EstimateTests
{
    [Fact]
    public void Token_estimate_is_about_a_quarter_of_the_characters_and_input_cost_uses_the_price_table()
    {
        Assert.Equal(0, AiHub.EstimateTokens(""));
        Assert.Equal(1, AiHub.EstimateTokens("abc"));
        Assert.Equal(250, AiHub.EstimateTokens(new string('x', 1000)));
        using var t = new TempRoot();
        var hub = AiHub.Open("TestApp", t.Path);
        var req = new AiRequest { Model = "claude-opus-5", System = new string('a', 4000), User = new string('b', 4000) };   // 2,000 tokens
        Assert.Equal(2000 / 1_000_000.0 * 5.0, hub.EstimateInputCost(req, AiProviderType.Claude)!.Value, 10);
        Assert.Null(hub.EstimateInputCost(req, AiProviderType.Claude, "unknown-model"));
    }
}
