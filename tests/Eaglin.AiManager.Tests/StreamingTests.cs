using System.Net;
using System.Text;
using Eaglin.AiManager.Providers;

namespace Eaglin.AiManager.Tests;

public class SseReaderTests
{
    private static async Task<List<SseEvent>> Parse(string body)
    {
        var list = new List<SseEvent>();
        await foreach (var e in SseReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(body)), CancellationToken.None))
            list.Add(e);
        return list;
    }

    [Fact]
    public async Task Parses_events_data_comments_and_multiline_data()
    {
        var events = await Parse(
            ": keep-alive\n" +
            "event: message_start\ndata: {\"a\":1}\n\n" +
            "data: line one\ndata: line two\n\n" +
            "data:[DONE]\n");

        Assert.Equal(3, events.Count);
        Assert.Equal(("message_start", "{\"a\":1}"), (events[0].Event, events[0].Data));
        Assert.Equal((null, "line one\nline two"), (events[1].Event, events[1].Data));
        Assert.Equal("[DONE]", events[2].Data);
    }

    [Fact]
    public async Task Windows_line_endings_are_fine()
    {
        var events = await Parse("data: a\r\n\r\ndata: b\r\n\r\n");
        Assert.Equal(new[] { "a", "b" }, events.Select(e => e.Data));
    }
}

public class StreamingProviderTests
{
    private static async Task<(string Text, AiChunk Final)> Collect(IAsyncEnumerable<AiChunk> stream)
    {
        var sb = new StringBuilder();
        AiChunk? final = null;
        await foreach (var c in stream)
        {
            if (c.IsFinal) final = c; else sb.Append(c.Text);
        }
        return (sb.ToString(), final ?? throw new Xunit.Sdk.XunitException("no final chunk"));
    }

    [Fact]
    public async Task Claude_streams_text_deltas_and_reports_usage_and_stop_reason()
    {
        var h = FakeHandler.Sse(
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":21,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Once \"}}\n\n" +
            "event: ping\ndata: {\"type\":\"ping\"}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"upon\"}}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":9}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        var p = new ClaudeProvider("k", "claude-opus-5", h.Client());

        var (text, final) = await Collect(p.StreamAsync("s", "u", 50, CancellationToken.None));

        Assert.Equal("Once upon", text);
        Assert.Equal(21, final.InputTokens);
        Assert.Equal(9, final.OutputTokens);
        Assert.Equal("end_turn", final.StopReason);
        Assert.Contains("\"stream\":true", h.LastBody);
    }

    [Fact]
    public async Task Claude_error_event_mid_stream_becomes_a_ProviderError()
    {
        var h = FakeHandler.Sse(
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\n" +
            "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n");
        var p = new ClaudeProvider("k", "m", h.Client());

        var ex = await Assert.ThrowsAsync<AiException>(() => Collect(p.StreamAsync("s", "u", 50, CancellationToken.None)));
        Assert.Equal(AiErrorKind.ProviderError, ex.Kind);
        Assert.Contains("Overloaded", ex.Message);
    }

    [Theory]
    [InlineData(AiProviderType.OpenAi,  true)]
    [InlineData(AiProviderType.XAi,     true)]
    [InlineData(AiProviderType.Mistral, false)]
    public async Task OpenAi_shaped_streams_deltas_usage_and_DONE(AiProviderType type, bool expectsUsageOption)
    {
        var h = FakeHandler.Sse(
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":8,\"completion_tokens\":2}}\n\n" +
            "data: [DONE]\n\n");
        var p = AiProviderRouter.Build(type, "k", "m", h.Client());

        var (text, final) = await Collect(p.StreamAsync("s", "u", 50, CancellationToken.None));

        Assert.Equal("Hello", text);
        Assert.Equal(8, final.InputTokens);
        Assert.Equal(2, final.OutputTokens);
        Assert.Equal("stop", final.StopReason);
        Assert.Equal(expectsUsageOption, h.LastBody!.Contains("include_usage"));
    }

    [Fact]
    public async Task Gemini_streams_parts_and_usage_metadata()
    {
        var h = FakeHandler.Sse(
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Sea \"}]}}],\"usageMetadata\":{\"promptTokenCount\":5}}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"breeze\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":5,\"candidatesTokenCount\":3}}\n\n");
        var p = new GeminiProvider("k", "gemini-2.5-flash", h.Client());

        var (text, final) = await Collect(p.StreamAsync("s", "u", 50, CancellationToken.None));

        Assert.Equal("Sea breeze", text);
        Assert.Equal(5, final.InputTokens);
        Assert.Equal(3, final.OutputTokens);
        Assert.Equal("STOP", final.StopReason);
        Assert.Contains(":streamGenerateContent?alt=sse", h.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Http_error_before_the_stream_maps_like_a_whole_call()
    {
        var h = new FakeHandler(HttpStatusCode.Unauthorized, "{ \"error\": { \"message\": \"bad key\" } }");
        var p = new XAiProvider("k", "m", h.Client());
        var ex = await Assert.ThrowsAsync<AiException>(() => Collect(p.StreamAsync("s", "u", 50, CancellationToken.None)));
        Assert.Equal(AiErrorKind.Auth, ex.Kind);
    }

    [Fact]
    public async Task Cancelling_mid_stream_stops_the_read_and_surfaces_as_cancellation()
    {
        var content = new ChannelContent();
        content.Push("data: {\"choices\":[{\"delta\":{\"content\":\"one \"}}]}\n\n");
        content.Push("data: {\"choices\":[{\"delta\":{\"content\":\"two \"}}]}\n\n");
        // …and then nothing: the vendor is "still thinking".

        var h = new FakeHandler(_ => (HttpStatusCode.OK, (HttpContent)content));
        var p = new OpenAiProvider("k", "m", h.Client());
        using var cts = new CancellationTokenSource();

        var received = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var c in p.StreamAsync("s", "u", 50, cts.Token))
            {
                received.Add(c.Text);
                if (received.Count == 2) cts.Cancel();
            }
        });

        Assert.Equal(new[] { "one ", "two " }, received);
    }
}
