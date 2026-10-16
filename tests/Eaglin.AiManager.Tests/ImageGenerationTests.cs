using System.Net;
using System.Text.Json;
using Eaglin.AiManager.Catalog;
using Eaglin.AiManager.Providers;

namespace Eaglin.AiManager.Tests;

/// <summary>
/// Making pictures (1.7.0): one request shape turned into each vendor's body, every answer shape read
/// back to bytes, the choice of provider with its reason, prices, the ledger, and the errors in plain
/// words. Fake HTTP handlers only — never a real call.
/// </summary>
public class ImageGenerationTests
{
    /// <summary>A real 2×3 PNG header (IHDR), enough for the type and size to be read.</summary>
    private static readonly byte[] Png =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        0, 0, 0, 2, 0, 0, 0, 3, 8, 6, 0, 0, 0, 0, 0, 0
    ];
    private static readonly string PngB64 = Convert.ToBase64String(Png);

    private static AiImageRequest Wide(int count = 1, AiImageQuality q = AiImageQuality.Standard) => new()
    {
        Prompt = "The water cycle over a mountain lake", Style = "A flat illustration.", Shape = AiImageShape.Wide16x9,
        Quality = q, Count = count, Purpose = "slide-picture"
    };

    private static AiHub HubWithKeys(TempRoot t, params AiProviderType[] providers)
    {
        var hub = AiHub.Open("TestApp", t.Path);
        foreach (var p in providers) hub.Credentials.SetKey(p, $"key-{p}", "test");
        hub.Credentials.Save();
        return hub;
    }

    private static JsonElement Json(string? body) => JsonDocument.Parse(body!).RootElement;

    // ── Request bodies ────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenAi_sends_a_16x9_size_in_multiples_of_16_and_reads_base64_and_the_revised_prompt()
    {
        var h = new FakeHandler(HttpStatusCode.OK, $"{{\"data\":[{{\"b64_json\":\"{PngB64}\",\"revised_prompt\":\"A lake…\"}}]}}");
        var pics = await new OpenAiImageGenerator("sk", "gpt-image-2", h.Client()).GenerateAsync(Wide(2), CancellationToken.None);

        Assert.Equal("https://api.openai.com/v1/images/generations", h.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer sk", h.LastRequest.Headers.Authorization!.ToString());
        var b = Json(h.LastBody);
        Assert.Equal("gpt-image-2", b.GetProperty("model").GetString());
        Assert.Equal("1536x864", b.GetProperty("size").GetString());   // 96×16 by 54×16
        Assert.Equal("medium", b.GetProperty("quality").GetString());
        Assert.Equal(2, b.GetProperty("n").GetInt32());
        Assert.Contains("Style: A flat illustration.", b.GetProperty("prompt").GetString());

        var pic = Assert.Single(pics);
        Assert.Equal("image/png", pic.MediaType);
        Assert.Equal((2, 3), (pic.Width, pic.Height));
        Assert.Equal("A lake…", pic.RevisedPrompt);
        Assert.Equal("png", pic.Extension);
    }

    [Theory]
    [InlineData(AiImageQuality.Draft, "low")]
    [InlineData(AiImageQuality.Standard, "medium")]
    [InlineData(AiImageQuality.Best, "high")]
    public void OpenAi_quality_words(AiImageQuality q, string word) => Assert.Equal(word, OpenAiImageGenerator.QualityWord(q));

    [Fact]
    public async Task XAi_asks_for_base64_with_a_ratio_and_downloads_a_url_answer_at_once()
    {
        var h = new FakeHandler(r => r.RequestUri!.Host == "imgen.x.ai"
            ? (HttpStatusCode.OK, new ByteArrayContent(Png))
            : (HttpStatusCode.OK, new StringContent("{\"data\":[{\"url\":\"https://imgen.x.ai/p.png\"}]}")));
        var pics = await new XAiImageGenerator("xk", "grok-imagine-image-2.0", h.Client())
            .GenerateAsync(Wide(q: AiImageQuality.Best), CancellationToken.None);

        var b = Json(h.Bodies[0]);
        Assert.Equal("https://api.x.ai/v1/images/generations", h.Requests[0].RequestUri!.ToString());
        Assert.Equal("16:9", b.GetProperty("aspect_ratio").GetString());
        Assert.Equal("2k", b.GetProperty("resolution").GetString());
        Assert.Equal("b64_json", b.GetProperty("response_format").GetString());
        Assert.Equal("medium", b.GetProperty("quality").GetString());
        Assert.Equal(Png, Assert.Single(pics).Data);   // the url was fetched straight away
    }

    [Fact]
    public void XAi_sends_quality_only_to_the_model_that_takes_it()
    {
        var body = new XAiImageGenerator("xk", "grok-imagine-image").Body(Wide());
        Assert.False(body.ContainsKey("quality"));
    }

    [Fact]
    public async Task Gemini_uses_interactions_with_the_key_header_and_finds_the_picture_block()
    {
        var h = new FakeHandler(HttpStatusCode.OK,
            $"{{\"steps\":[{{\"type\":\"model_output\",\"content\":[{{\"type\":\"text\",\"text\":\"Here it is\"}},{{\"type\":\"image\",\"mime_type\":\"image/png\",\"data\":\"{PngB64}\"}}]}}]}}");
        var g = new GeminiImageGenerator("gk", "gemini-nano-banana-2.1", h.Client());
        var pics = await g.GenerateAsync(Wide(2), CancellationToken.None);

        Assert.Equal(2, h.Requests.Count);   // one picture per call
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", h.LastRequest!.RequestUri!.ToString());
        Assert.Equal("gk", h.LastRequest.Headers.GetValues("x-goog-api-key").Single());
        var f = Json(h.LastBody).GetProperty("response_format");
        Assert.Equal("image", f.GetProperty("type").GetString());
        Assert.Equal("16:9", f.GetProperty("aspect_ratio").GetString());
        Assert.Equal("1K", f.GetProperty("image_size").GetString());
        Assert.Equal(2, pics.Count);
        Assert.Equal("SynthID", g.Watermark);
    }

    [Fact]
    public async Task Gemini_falls_back_to_generateContent_when_interactions_is_not_found_and_reads_inlineData()
    {
        var h = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("interactions")
            ? (HttpStatusCode.NotFound, "{\"error\":{\"message\":\"not found\"}}")
            : (HttpStatusCode.OK, $"{{\"candidates\":[{{\"content\":{{\"parts\":[{{\"inlineData\":{{\"mimeType\":\"image/png\",\"data\":\"{PngB64}\"}}}}]}}}}]}}"));
        var pics = await new GeminiImageGenerator("gk", "gemini-2.5-flash-image", h.Client()).GenerateAsync(Wide(), CancellationToken.None);

        Assert.EndsWith("/models/gemini-2.5-flash-image:generateContent", h.LastRequest!.RequestUri!.AbsolutePath);
        var cfg = Json(h.LastBody).GetProperty("generationConfig");
        Assert.Equal("IMAGE", cfg.GetProperty("responseModalities")[0].GetString());
        Assert.Equal("16:9", cfg.GetProperty("imageConfig").GetProperty("aspectRatio").GetString());
        Assert.Single(pics);
    }

    // ── Errors in plain words ─────────────────────────────────────────────────

    [Fact]
    public async Task A_refused_picture_is_a_Refused_error_that_says_what_to_change()
    {
        var h = new FakeHandler(HttpStatusCode.BadRequest,
            "{\"error\":{\"code\":\"moderation_blocked\",\"message\":\"Your request was rejected by the safety system.\"}}");
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            new OpenAiImageGenerator("sk", "gpt-image-2", h.Client()).GenerateAsync(Wide(), CancellationToken.None));
        Assert.Equal(AiErrorKind.Refused, ex.Kind);
        Assert.Contains("would not make this picture", ex.Message);
        Assert.Contains("people's names", ex.Message);
    }

    [Fact]
    public async Task An_unverified_OpenAI_organisation_is_told_how_to_verify()
    {
        var h = new FakeHandler(HttpStatusCode.Forbidden,
            "{\"error\":{\"message\":\"Your organization must be verified to use the model gpt-image-2.\"}}");
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            new OpenAiImageGenerator("sk", "gpt-image-2", h.Client()).GenerateAsync(Wide(), CancellationToken.None));
        Assert.Contains("cannot make pictures yet", ex.Message);
        Assert.Contains("Verify Organization", ex.Message);
    }

    [Fact]
    public async Task A_free_Gemini_key_is_told_to_turn_on_billing()
    {
        var h = new FakeHandler(HttpStatusCode.TooManyRequests,
            "{\"error\":{\"message\":\"Quota exceeded for metric: generate_content_free_tier_requests, limit: 0\"}}");
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            new GeminiImageGenerator("gk", "gemini-nano-banana-2.1", h.Client()).GenerateAsync(Wide(), CancellationToken.None));
        Assert.Contains("no free tier", ex.Message);
        Assert.Contains("billing", ex.Message);
    }

    [Fact]
    public async Task A_blocked_Gemini_answer_is_a_refusal_and_an_empty_one_says_so()
    {
        var blocked = new FakeHandler(HttpStatusCode.OK, "{\"candidates\":[{\"finishReason\":\"IMAGE_SAFETY\"}]}");
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            new GeminiImageGenerator("gk", "m", blocked.Client()).GenerateAsync(Wide(), CancellationToken.None));
        Assert.Equal(AiErrorKind.Refused, ex.Kind);

        var empty = new FakeHandler(HttpStatusCode.OK, "{\"steps\":[{\"type\":\"model_output\",\"content\":[{\"type\":\"text\",\"text\":\"I can describe it instead.\"}]}]}");
        var ex2 = await Assert.ThrowsAsync<AiException>(() =>
            new GeminiImageGenerator("gk", "m", empty.Client()).GenerateAsync(Wide(), CancellationToken.None));
        Assert.Equal(AiErrorKind.EmptyResponse, ex2.Kind);
        Assert.Contains("I can describe it instead.", ex2.Message);
    }

    [Fact]
    public async Task An_xAI_picture_filtered_by_moderation_is_a_refusal()
    {
        var h = new FakeHandler(HttpStatusCode.OK, "{\"data\":[{\"url\":\"\",\"respect_moderation\":false}]}");
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            new XAiImageGenerator("xk", "grok-imagine-image-2.0", h.Client()).GenerateAsync(Wide(), CancellationToken.None));
        Assert.Equal(AiErrorKind.Refused, ex.Kind);
    }

    [Fact]
    public void Picture_types_and_sizes_come_from_the_bytes()
    {
        Assert.Equal(("image/png", 2, 3), ImageApi.Sniff(Png));
        // A tiny JPEG: SOI, then SOF0 with height 3, width 4.
        byte[] jpg = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x03, 0x00, 0x04, 0x03, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(("image/jpeg", 4, 3), ImageApi.Sniff(jpg));
    }

    // ── Which provider draws ──────────────────────────────────────────────────

    [Fact]
    public void With_only_a_Claude_key_the_error_says_which_keys_can_draw()
    {
        using var t = new TempRoot();
        var hub = HubWithKeys(t, AiProviderType.Claude);
        var ex = Assert.Throws<AiException>(() => hub.ResolveImageProvider());
        Assert.Equal(AiErrorKind.NoKey, ex.Kind);
        Assert.Contains("needs an OpenAI, Google Gemini or xAI key", ex.Message);
        Assert.Contains("Claude and Mistral AI cannot make pictures", ex.Message);
    }

    [Fact]
    public void A_Claude_user_with_a_Gemini_key_gets_Gemini_and_is_told_why()
    {
        using var t = new TempRoot();
        var hub = HubWithKeys(t, AiProviderType.Claude, AiProviderType.Gemini);
        var c = hub.ResolveImageProvider();
        Assert.Equal(AiProviderType.Gemini, c.Provider);
        Assert.Contains("cannot make pictures", c.Reason);   // "(Claude cannot make pictures)"
    }

    [Fact]
    public void The_order_is_request_then_program_then_all_programs_then_the_programs_provider()
    {
        using var t = new TempRoot();
        var hub = HubWithKeys(t, AiProviderType.OpenAi, AiProviderType.Gemini, AiProviderType.XAi);
        hub.Settings.DefaultProvider = AiProviderType.OpenAi;
        Assert.Equal(AiProviderType.OpenAi, hub.ResolveImageProvider().Provider);     // the program's provider can draw

        hub.Settings.ImageProvider = AiProviderType.Gemini;
        Assert.Equal(AiProviderType.Gemini, hub.ResolveImageProvider().Provider);     // all programs' picture provider
        Assert.Contains("chosen for pictures in AI Settings", hub.ResolveImageProvider().Reason);

        hub.Settings.ForApp("TestApp").ImageProvider = AiProviderType.XAi;
        Assert.Equal(AiProviderType.XAi, hub.ResolveImageProvider().Provider);        // this program's own

        Assert.Equal(AiProviderType.OpenAi, hub.ResolveImageProvider(AiProviderType.OpenAi).Provider);   // the request's
    }

    [Fact]
    public void A_picture_provider_chosen_without_a_key_is_an_error_not_a_quiet_switch()
    {
        using var t = new TempRoot();
        var hub = HubWithKeys(t, AiProviderType.OpenAi);
        hub.Settings.ImageProvider = AiProviderType.Gemini;
        var ex = Assert.Throws<AiException>(() => hub.ResolveImageProvider());
        Assert.Contains("set to Gemini (Google)", ex.Message);
        Assert.Contains("no Gemini (Google) key", ex.Message);
    }

    [Fact]
    public void Claude_cannot_be_asked_for_pictures()
    {
        using var t = new TempRoot();
        var hub = HubWithKeys(t, AiProviderType.Claude);
        var ex = Assert.Throws<AiException>(() => hub.ResolveImageProvider(AiProviderType.Claude));
        Assert.Contains("cannot make pictures", ex.Message);
    }

    [Fact]
    public void The_picture_provider_is_saved_and_an_app_entry_with_only_it_is_kept()
    {
        using var t = new TempRoot();
        var hub = AiHub.Open("TestApp", t.Path);
        hub.Settings.ImageProvider = AiProviderType.XAi;
        hub.Settings.ForApp("TestApp").ImageProvider = AiProviderType.OpenAi;
        Assert.False(hub.Settings.ForApp("TestApp").IsEmpty);
        hub.SaveSettings();

        var again = AiHub.Open("TestApp", t.Path);
        Assert.Equal(AiProviderType.XAi, again.Settings.ImageProvider);
        Assert.Equal(AiProviderType.OpenAi, again.Settings.AppImageProvider("TestApp"));
    }

    // ── Prices ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("gpt-image-2", AiImageQuality.Standard, 0.041)]
    [InlineData("gpt-image-2", AiImageQuality.Draft, 0.005)]
    [InlineData("gemini-nano-banana-2.1", AiImageQuality.Best, 0.0504)]
    [InlineData("grok-imagine-image-2.0", AiImageQuality.Standard, 0.04)]
    [InlineData("grok-imagine-image", AiImageQuality.Standard, 0.02)]
    public void Each_providers_standard_price(string model, AiImageQuality q, double usd) =>
        Assert.Equal(usd, Pricing.EstimateImage(model, q)!.Value, 6);

    [Fact]
    public void An_unknown_price_is_null_never_zero_and_a_newer_model_does_not_take_an_older_ones_price()
    {
        Assert.Null(Pricing.EstimateImage("gpt-image-2.5-flare", AiImageQuality.Standard));
        Assert.Null(Pricing.EstimateImage("some-new-model", AiImageQuality.Standard));
        Assert.Equal(0.07, Pricing.EstimateImage("gpt-image-2.5-flare", AiImageQuality.Standard,
            new Dictionary<string, ImagePrice> { ["gpt-image-2.5"] = new(0.01, 0.07, 0.2) })!.Value, 6);
    }

    // ── Through the hub ───────────────────────────────────────────────────────

    private sealed class FakeImages(int n) : IImageGenerator
    {
        public AiProviderType Type => AiProviderType.OpenAi;
        public string Model => "gpt-image-2";
        public string? Watermark => null;
        public AiImageRequest? Last;
        public TaskCompletionSource? Hold;
        public async Task<IReadOnlyList<AiGeneratedImage>> GenerateAsync(AiImageRequest request, CancellationToken ct)
        {
            Last = request;
            if (Hold is not null) await Hold.Task.WaitAsync(ct);
            return Enumerable.Range(0, n).Select(_ => ImageApi.Picture(Png, null)).ToList();
        }
    }

    [Fact]
    public async Task The_hub_returns_pictures_records_them_in_the_ledger_and_says_why_that_provider()
    {
        using var t = new TempRoot();
        var hub = HubWithKeys(t, AiProviderType.Claude, AiProviderType.OpenAi);
        var fake = new FakeImages(2);
        hub.ImageFactory = (_, k, m) => { Assert.Equal("key-OpenAi", k); Assert.Equal("gpt-image-2", m); return fake; };
        AiImageResult? raised = null;
        hub.ImagesGenerated += r => raised = r;

        var r = await hub.GenerateImagesAsync(Wide(2));

        Assert.Equal(2, r.Images.Count);
        Assert.Equal(AiProviderType.OpenAi, r.Provider);
        Assert.Contains("Claude (Anthropic) cannot make pictures", r.ProviderReason);
        Assert.Equal(0.082, r.EstimatedCostUsd!.Value, 6);
        Assert.Same(r, raised);
        Assert.Equal(AiImageShape.Wide16x9, fake.Last!.Shape);

        var rec = Assert.Single(hub.Usage.Read(DateTime.Now));
        Assert.Equal(2, rec.Images);
        Assert.Equal(0.082, rec.CostUsd!.Value, 6);
        Assert.Equal("slide-picture", rec.Purpose);
        Assert.Equal(2, hub.Usage.Summarize(DateTime.Now).Totals.Pictures);
    }

    [Fact]
    public async Task Cancelling_mid_call_throws_and_is_recorded_as_cancelled()
    {
        using var t = new TempRoot();
        var hub = HubWithKeys(t, AiProviderType.OpenAi);
        var fake = new FakeImages(1) { Hold = new TaskCompletionSource() };
        hub.ImageFactory = (_, _, _) => fake;
        using var cts = new CancellationTokenSource();

        var call = hub.GenerateImagesAsync(Wide(), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.True(Assert.Single(hub.Usage.Read(DateTime.Now)).Cancelled);
    }

    [Fact]
    public async Task The_count_is_kept_between_one_and_four_and_an_empty_prompt_is_refused()
    {
        using var t = new TempRoot();
        var hub = HubWithKeys(t, AiProviderType.OpenAi);
        Assert.Equal(0.041 * 3, hub.EstimateImages(AiProviderType.OpenAi, null, AiImageQuality.Standard, 3)!.Value, 6);
        Assert.Equal(4, new OpenAiImageGenerator("k", "gpt-image-2").Body(Wide(9))["n"]!.GetValue<int>());
        await Assert.ThrowsAsync<ArgumentException>(() => hub.GenerateImagesAsync(new AiImageRequest { Prompt = "  " }));
    }
}

/// <summary>The How? key help says, for every provider, whether its key can make pictures (1.7.0).</summary>
public class ImageKeyHelpTests
{
    [Theory]
    [InlineData(AiProviderType.OpenAi, "Verify Organization")]
    [InlineData(AiProviderType.Gemini, "no free tier")]
    [InlineData(AiProviderType.XAi, "Pictures: yes")]
    [InlineData(AiProviderType.Claude, "cannot make them")]
    [InlineData(AiProviderType.Mistral, "Pictures: not through AI Manager")]
    public void Each_providers_help_says_whether_its_key_can_make_pictures(AiProviderType p, string words)
    {
        Assert.Contains(words, Eaglin.AiManager.Help.KeyHelp.ShortText(p));
        Assert.Contains("<h3>Pictures</h3>", Eaglin.AiManager.Help.KeyHelp.Html());
    }
}
