using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Eaglin.AiManager.Providers;

namespace Eaglin.AiManager.Tests;

/// <summary>An isolated store root per test, deleted on dispose.</summary>
public sealed class TempRoot : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AiManagerTests", Guid.NewGuid().ToString("N"));

    public TempRoot() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Answers requests from a responder (or one canned status + body) and records what was sent.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, (HttpStatusCode Status, HttpContent Content)> _respond;

    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string?> Bodies { get; } = new();
    public HttpRequestMessage? LastRequest => Requests.LastOrDefault();
    public string? LastBody => Bodies.LastOrDefault();

    public FakeHandler(HttpStatusCode status, string body)
        : this(_ => (status, new StringContent(body, Encoding.UTF8, "application/json"))) { }

    public FakeHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond)
        : this(r => { var (s, b) = respond(r); return (s, new StringContent(b, Encoding.UTF8, "application/json")); }) { }

    public FakeHandler(Func<HttpRequestMessage, (HttpStatusCode Status, HttpContent Content)> respond) => _respond = respond;

    /// <summary>Serves an SSE body (text/event-stream) with 200.</summary>
    public static FakeHandler Sse(string sseBody) =>
        new(_ => (HttpStatusCode.OK, new StringContent(sseBody, Encoding.UTF8, "text/event-stream")));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
        var (status, content) = _respond(request);
        return new HttpResponseMessage(status) { Content = content };
    }

    public HttpClient Client() => new(this) { Timeout = TimeSpan.FromSeconds(30) };
}

/// <summary>
/// HTTP content whose body arrives piece by piece from a channel, so a test can feed a few SSE
/// events, then hold the stream open until the reader cancels.
/// </summary>
public sealed class ChannelContent : HttpContent
{
    public Channel<byte[]> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

    public ChannelContent() => Headers.ContentType = new("text/event-stream");

    public void Push(string text) => Channel.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
    public void Complete() => Channel.Writer.TryComplete();

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) =>
        Task.FromResult<Stream>(new ChannelStream(Channel.Reader));
    protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        await foreach (var chunk in Channel.Reader.ReadAllAsync())
            await stream.WriteAsync(chunk);
    }

    protected override bool TryComputeLength(out long length) { length = 0; return false; }

    private sealed class ChannelStream(ChannelReader<byte[]> reader) : Stream
    {
        private byte[]? _pending;
        private int _offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pending is null || _offset >= _pending.Length)
            {
                if (!await reader.WaitToReadAsync(ct)) return 0;
                if (!reader.TryRead(out _pending)) return 0;
                _offset = 0;
            }
            var n = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>A provider that returns a fixed answer (or throws), for AiHub tests.</summary>
public sealed class FakeProvider : IAiProvider
{
    public AiProviderType Type { get; }
    public string ProviderName => $"Fake {Type}";
    public string Model { get; }
    public bool HasApiKey => true;
    public string? Answer { get; init; }
    public int InputTokens { get; init; } = 10;
    public int OutputTokens { get; init; } = 5;
    public AiException? Throws { get; init; }
    public string? LastSystem { get; private set; }
    public string? LastUser   { get; private set; }

    public FakeProvider(AiProviderType type, string model) { Type = type; Model = model; }

    public Task<ProviderResult> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        LastSystem = systemPrompt;
        LastUser   = userPrompt;
        if (Throws is not null) throw Throws;
        return Task.FromResult(new ProviderResult(Answer ?? $"answer from {Type}/{Model}", InputTokens, OutputTokens, "end_turn"));
    }

    public async IAsyncEnumerable<AiChunk> StreamAsync(string systemPrompt, string userPrompt, int maxTokens, [EnumeratorCancellation] CancellationToken ct)
    {
        LastSystem = systemPrompt;
        LastUser   = userPrompt;
        if (Throws is not null) throw Throws;
        var text = Answer ?? $"answer from {Type}/{Model}";
        var half = text.Length / 2;
        yield return new AiChunk(text[..half]);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new AiChunk(text[half..]);
        yield return AiChunk.Final(InputTokens, OutputTokens, "end_turn");
    }
}
