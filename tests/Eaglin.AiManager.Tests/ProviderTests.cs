using System.Net;
using Eaglin.AiManager.Providers;

namespace Eaglin.AiManager.Tests;

public class ProviderTests
{
    [Fact]
    public async Task Claude_parses_text_usage_and_stop_reason_and_sends_the_right_headers()
    {
        var h = new FakeHandler(HttpStatusCode.OK,
            "{ \"content\": [ { \"type\": \"text\", \"text\": \"Hello \" }, { \"type\": \"text\", \"text\": \"there\" } ]," +
            "  \"usage\": { \"input_tokens\": 12, \"output_tokens\": 3 }, \"stop_reason\": \"end_turn\" }");
        var p = new ClaudeProvider("sk-test", "claude-opus-5", h.Client());

        var r = await p.GenerateAsync("sys", "user", 100, CancellationToken.None);

        Assert.Equal("Hello there", r.Text);
        Assert.Equal(12, r.InputTokens);
        Assert.Equal(3, r.OutputTokens);
        Assert.Equal("end_turn", r.StopReason);
        Assert.Equal("sk-test", h.LastRequest!.Headers.GetValues("x-api-key").Single());
        Assert.Contains("\"max_tokens\":100", h.LastBody);
        Assert.Contains("\"system\":\"sys\"", h.LastBody);
    }

    [Fact]
    public async Task Claude_refusal_is_a_Refused_error()
    {
        var h = new FakeHandler(HttpStatusCode.OK, "{ \"content\": [], \"stop_reason\": \"refusal\" }");
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            new ClaudeProvider("k", "m", h.Client()).GenerateAsync("s", "u", 10, CancellationToken.None));
        Assert.Equal(AiErrorKind.Refused, ex.Kind);
    }

    [Fact]
    public async Task Gemini_parses_parts_and_usage_metadata()
    {
        var h = new FakeHandler(HttpStatusCode.OK,
            "{ \"candidates\": [ { \"content\": { \"parts\": [ { \"text\": \"Hi\" } ] }, \"finishReason\": \"STOP\" } ]," +
            "  \"usageMetadata\": { \"promptTokenCount\": 7, \"candidatesTokenCount\": 1 } }");
        var p = new GeminiProvider("g", "gemini-2.5-flash", h.Client());

        var r = await p.GenerateAsync("s", "u", 10, CancellationToken.None);

        Assert.Equal("Hi", r.Text);
        Assert.Equal(7, r.InputTokens);
        Assert.Equal("STOP", r.StopReason);
        Assert.Contains("/v1beta/models/gemini-2.5-flash:generateContent", h.LastRequest!.RequestUri!.ToString());
    }

    [Theory]
    [InlineData(AiProviderType.OpenAi,  "api.openai.com")]
    [InlineData(AiProviderType.Mistral, "api.mistral.ai")]
    [InlineData(AiProviderType.XAi,     "api.x.ai")]
    public async Task OpenAi_shaped_providers_parse_choices_and_usage(AiProviderType type, string host)
    {
        var h = new FakeHandler(HttpStatusCode.OK,
            "{ \"choices\": [ { \"message\": { \"content\": \"OK\" }, \"finish_reason\": \"stop\" } ]," +
            "  \"usage\": { \"prompt_tokens\": 5, \"completion_tokens\": 1 } }");
        var p = AiProviderRouter.Build(type, "key", "some-model", h.Client());

        var r = await p.GenerateAsync("s", "u", 10, CancellationToken.None);

        Assert.Equal("OK", r.Text);
        Assert.Equal(5, r.InputTokens);
        Assert.Equal(1, r.OutputTokens);
        Assert.Equal("stop", r.StopReason);
        Assert.Equal(host, h.LastRequest!.RequestUri!.Host);
        Assert.Equal("Bearer key", h.LastRequest.Headers.Authorization!.ToString());
        Assert.Contains("\"model\":\"some-model\"", h.LastBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized,    AiErrorKind.Auth)]
    [InlineData(HttpStatusCode.Forbidden,       AiErrorKind.Auth)]
    [InlineData(HttpStatusCode.TooManyRequests, AiErrorKind.RateLimit)]
    [InlineData(HttpStatusCode.InternalServerError, AiErrorKind.ProviderError)]
    public async Task Http_failures_map_to_error_kinds_with_the_vendor_message(HttpStatusCode status, AiErrorKind kind)
    {
        var h = new FakeHandler(status, "{ \"error\": { \"message\": \"bad things\" } }");
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            new OpenAiProvider("k", "m", h.Client()).GenerateAsync("s", "u", 10, CancellationToken.None));
        Assert.Equal(kind, ex.Kind);
        Assert.Equal((int)status, ex.StatusCode);
        Assert.Contains("bad things", ex.Message);
        Assert.Equal(AiProviderType.OpenAi, ex.Provider);
    }

    [Fact]
    public async Task Empty_body_is_an_EmptyResponse_error()
    {
        var h = new FakeHandler(HttpStatusCode.OK, "{ \"choices\": [] }");
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            new XAiProvider("k", "m", h.Client()).GenerateAsync("s", "u", 10, CancellationToken.None));
        Assert.Equal(AiErrorKind.EmptyResponse, ex.Kind);
    }

    [Fact]
    public async Task Blank_key_is_a_NoKey_error_before_any_network_call()
    {
        var h = new FakeHandler(HttpStatusCode.OK, "{}");
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            new MistralProvider("", "m", h.Client()).GenerateAsync("s", "u", 10, CancellationToken.None));
        Assert.Equal(AiErrorKind.NoKey, ex.Kind);
        Assert.Null(h.LastRequest);
    }

    [Fact]
    public void Router_builds_every_provider_with_its_model_or_the_default()
    {
        foreach (var p in AiProviderRouter.AllProviders)
        {
            var built = AiProviderRouter.Build(p, "key", "");
            Assert.Equal(p, built.Type);
            Assert.True(built.HasApiKey);
            Assert.Equal(AiProviderRouter.DefaultModels[p], built.Model);
            Assert.Equal("custom", AiProviderRouter.Build(p, "key", "custom").Model);
            Assert.Contains(AiProviderRouter.DefaultModels[p], AiProviderRouter.FallbackModels[p]);
        }
        Assert.Equal(5, AiProviderRouter.AllProviders.Count);
        Assert.Equal("claude-opus-5", AiProviderRouter.DefaultModels[AiProviderType.Claude]);
    }
}
