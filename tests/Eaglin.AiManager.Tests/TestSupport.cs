using System.Net;
using System.Text;
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

/// <summary>Answers every request with a canned status + body and records what was sent.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    public FakeHandler(HttpStatusCode status, string body) { _status = status; _body = body; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        LastBody    = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
    }

    public HttpClient Client() => new(this) { Timeout = TimeSpan.FromSeconds(30) };
}

/// <summary>A provider that returns a fixed answer (or throws), for AiHub tests.</summary>
public sealed class FakeProvider : IAiProvider
{
    public AiProviderType Type { get; }
    public string ProviderName => $"Fake {Type}";
    public string Model { get; }
    public bool HasApiKey => true;
    public string? Answer { get; init; }
    public AiException? Throws { get; init; }
    public string? LastSystem { get; private set; }
    public string? LastUser   { get; private set; }

    public FakeProvider(AiProviderType type, string model) { Type = type; Model = model; }

    public Task<ProviderResult> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        LastSystem = systemPrompt;
        LastUser   = userPrompt;
        if (Throws is not null) throw Throws;
        return Task.FromResult(new ProviderResult(Answer ?? $"answer from {Type}/{Model}", 10, 5, "end_turn"));
    }
}
