namespace Eaglin.AiManager.Providers;

/// <summary>
/// Contract every vendor wrapper satisfies: one system + user prompt in, text and token counts
/// out — as a whole or as a stream. Each wraps its own REST API directly so the library never
/// depends on a vendor SDK. Apps do not use this directly — they go through <see cref="AiHub"/>.
/// </summary>
public interface IAiProvider
{
    AiProviderType Type { get; }

    /// <summary>Human-readable name, e.g. "Claude (Anthropic)".</summary>
    string ProviderName { get; }

    /// <summary>Model identifier this instance was built with.</summary>
    string Model { get; }

    /// <summary>True if a non-empty API key was supplied (presence only, not validity).</summary>
    bool HasApiKey { get; }

    /// <summary>
    /// Sends the prompt and returns the text with usage. Throws <see cref="AiException"/> for
    /// every failure the caller can act on, and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    Task<ProviderResult> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct);

    /// <summary>
    /// Sends the prompt and yields the answer as it is generated. The last chunk is
    /// <see cref="AiChunk.IsFinal"/> and carries usage. Cancelling the token closes the
    /// connection and throws <see cref="OperationCanceledException"/> from the enumerator.
    /// </summary>
    IAsyncEnumerable<AiChunk> StreamAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct);

    /// <summary>
    /// As <see cref="GenerateAsync(string, string, int, CancellationToken)"/>, with pictures sent with
    /// the user prompt (1.3.0). The five built-in providers send them; a provider that does not
    /// override this sends the text alone, so providers written before 1.3.0 keep working.
    /// </summary>
    Task<ProviderResult> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, IReadOnlyList<AiImage> images, CancellationToken ct) =>
        GenerateAsync(systemPrompt, userPrompt, maxTokens, ct);

    /// <summary>As <see cref="StreamAsync(string, string, int, CancellationToken)"/>, with pictures (1.3.0); the same fallback.</summary>
    IAsyncEnumerable<AiChunk> StreamAsync(string systemPrompt, string userPrompt, int maxTokens, IReadOnlyList<AiImage> images, CancellationToken ct) =>
        StreamAsync(systemPrompt, userPrompt, maxTokens, ct);
}

public sealed record ProviderResult(string Text, int InputTokens, int OutputTokens, string? StopReason);
