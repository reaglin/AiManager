namespace Eaglin.AiManager;

public enum AiErrorKind
{
    /// <summary>No API key stored for the provider. UI: "add a key in AI Settings".</summary>
    NoKey,
    /// <summary>The provider rejected the key (401/403).</summary>
    Auth,
    /// <summary>429 — quota or rate limit.</summary>
    RateLimit,
    /// <summary>DNS, connection, TLS.</summary>
    Network,
    /// <summary>The HTTP call exceeded the client timeout.</summary>
    Timeout,
    /// <summary>Any other non-success status from the provider; the message carries its text.</summary>
    ProviderError,
    /// <summary>The model declined the request (safety refusal).</summary>
    Refused,
    /// <summary>A 2xx with no usable text.</summary>
    EmptyResponse
}

/// <summary>
/// The one exception type apps see from the AI layer. <see cref="Kind"/> tells a UI what to say;
/// <see cref="Message"/> is already plain English. Cancellation is *not* an AiException —
/// <see cref="OperationCanceledException"/> propagates as usual.
/// </summary>
public sealed class AiException : Exception
{
    public AiErrorKind    Kind       { get; }
    public AiProviderType Provider   { get; }
    public int?           StatusCode { get; }

    public AiException(AiErrorKind kind, AiProviderType provider, string message, Exception? inner = null, int? statusCode = null)
        : base(message, inner)
    {
        Kind       = kind;
        Provider   = provider;
        StatusCode = statusCode;
    }
}
