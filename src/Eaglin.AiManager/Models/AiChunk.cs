namespace Eaglin.AiManager;

/// <summary>
/// One piece of a streamed answer. Text chunks carry <see cref="Text"/>; the last chunk has
/// <see cref="IsFinal"/> set and carries the usage the vendor reported (its Text is empty).
/// </summary>
public sealed record AiChunk(
    string  Text,
    bool    IsFinal      = false,
    int     InputTokens  = 0,
    int     OutputTokens = 0,
    string? StopReason   = null)
{
    public static AiChunk Final(int inputTokens, int outputTokens, string? stopReason) =>
        new(string.Empty, true, inputTokens, outputTokens, stopReason);
}
