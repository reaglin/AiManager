namespace Eaglin.AiManager.Providers;

/// <summary>
/// Contract every vendor wrapper satisfies: one system + user prompt in, text and token counts
/// out. Each wraps its own REST API directly so the library never depends on a vendor SDK.
/// Apps do not use this directly — they go through <see cref="AiHub"/>.
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
}

public sealed record ProviderResult(string Text, int InputTokens, int OutputTokens, string? StopReason);
