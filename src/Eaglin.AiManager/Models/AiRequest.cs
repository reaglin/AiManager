namespace Eaglin.AiManager;

/// <summary>One prompt to send. Everything an app needs to say; nothing about keys or endpoints.</summary>
public sealed class AiRequest
{
    /// <summary>Which vendor answers. Null = the app's default (settings.json, app then global).</summary>
    public AiProviderType? Provider { get; init; }

    /// <summary>Model id for that vendor. Null = the app's configured model, else the built-in default.</summary>
    public string? Model { get; init; }

    public string System { get; init; } = string.Empty;
    public string User   { get; init; } = string.Empty;

    /// <summary>Output-token ceiling. 4000 suits report-length answers; chapter-length work passes more.</summary>
    public int MaxTokens { get; init; } = 4000;

    /// <summary>Free text naming what the call is for ("chapter-summary"). Lands in the log and the usage ledger.</summary>
    public string Purpose { get; init; } = string.Empty;

    public AiRequest With(AiProviderType provider, string? model = null) => new()
    {
        Provider = provider, Model = model ?? Model, System = System, User = User,
        MaxTokens = MaxTokens, Purpose = Purpose
    };
}

/// <summary>What came back, with the accounting an app may want to show.</summary>
public sealed class AiResponse
{
    public required string         Text         { get; init; }
    public required AiProviderType Provider     { get; init; }
    public required string         Model        { get; init; }
    public required string         App          { get; init; }
    public          string         Purpose      { get; init; } = string.Empty;
    public          int            InputTokens  { get; init; }
    public          int            OutputTokens { get; init; }
    /// <summary>Null when the model's price is unknown (tokens are still recorded).</summary>
    public          double?        EstimatedCostUsd { get; init; }
    public          TimeSpan       Elapsed      { get; init; }
    public          string?        StopReason   { get; init; }
    public          DateTime       CompletedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Result of one provider in a fan-out call: a response or the reason it failed.</summary>
public sealed class AiOutcome
{
    public required AiProviderType Provider { get; init; }
    public AiResponse?  Response { get; init; }
    public AiException? Error    { get; init; }
    public bool Succeeded => Response is not null;
}
