namespace Eaglin.AiManager;

/// <summary>One audio file to turn into text (1.5.0). Everything an app needs to say; nothing about keys or endpoints.</summary>
public sealed class AiTranscriptionRequest
{
    /// <summary>Which vendor transcribes. Null = the app's default provider if it can transcribe and has a key, else the first that does.</summary>
    public AiProviderType? Provider { get; init; }

    /// <summary>Model id. Null = <see cref="AiHub.DefaultTranscriptionModel"/> for the provider.</summary>
    public string? Model { get; init; }

    /// <summary>A local audio file (mp3, m4a, wav, ogg, webm, flac), at most <see cref="AiHub.MaxTranscriptionBytes"/>.</summary>
    public required string AudioPath { get; init; }

    /// <summary>ISO-639-1 code ("en"). Null = the vendor detects the language.</summary>
    public string? Language { get; init; }

    /// <summary>Spelling hints: the names and places the speaker uses. Sent where the vendor takes one.</summary>
    public string? Prompt { get; init; }

    /// <summary>Free text naming what the call is for ("story-transcript"). Lands in the log and the usage ledger.</summary>
    public string Purpose { get; init; } = string.Empty;

    /// <summary>Ask for word timings where the provider has them (OpenAI whisper-1 does).</summary>
    public bool Words { get; init; } = true;
}

/// <summary>A stretch of speech with its start and end in seconds from the start of the audio.</summary>
public sealed record AiTimedText(double Start, double End, string Text);

/// <summary>What came back from a transcription, with the accounting an app may want to show.</summary>
public sealed class AiTranscript
{
    public required string Text { get; init; }
    /// <summary>Sentences or phrases with times, as the vendor split them.</summary>
    public IReadOnlyList<AiTimedText> Segments { get; init; } = [];
    /// <summary>Single words with times; empty when the provider gives none.</summary>
    public IReadOnlyList<AiTimedText> Words { get; init; } = [];
    public string? Language { get; init; }
    public double AudioSeconds { get; init; }
    public required AiProviderType Provider { get; init; }
    public required string Model { get; init; }
    public required string App { get; init; }
    public string Purpose { get; init; } = string.Empty;
    /// <summary>Null when the model's per-minute price is unknown.</summary>
    public double? EstimatedCostUsd { get; init; }
    public TimeSpan Elapsed { get; init; }
    public DateTime CompletedUtc { get; init; } = DateTime.UtcNow;
}
