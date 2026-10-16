namespace Eaglin.AiManager;

/// <summary>
/// The shape of a picture (1.7.0). Each provider turns it into its own size or ratio: OpenAI a
/// pixel size, Gemini and xAI an aspect ratio. Wide 16:9 is the slide shape.
/// </summary>
public enum AiImageShape
{
    Square,
    /// <summary>16:9 — a PowerPoint slide.</summary>
    Wide16x9,
    /// <summary>3:2 — a picture in a page of text.</summary>
    Landscape3x2,
    /// <summary>2:3 — upright.</summary>
    Portrait
}

/// <summary>
/// How good (and how dear) a picture is (1.7.0). Each provider maps it: OpenAI low / medium / high,
/// Gemini 1K / 1K / 2K, xAI low / medium / medium at 2k.
/// </summary>
public enum AiImageQuality { Draft, Standard, Best }

/// <summary>
/// Pictures to make from a description (1.7.0). Everything an app needs to say; nothing about keys,
/// endpoints or a vendor's words. Apps send it to <see cref="AiHub.GenerateImagesAsync"/>.
/// </summary>
public sealed class AiImageRequest
{
    /// <summary>
    /// Which vendor draws. Null = the program's "provider for pictures" in AI Settings, else the one
    /// for all programs, else the program's default provider when it can draw and has a key, else the
    /// first provider that can draw and has a key (<see cref="AiHub.ResolveImageProvider"/>).
    /// </summary>
    public AiProviderType? Provider { get; init; }

    /// <summary>Model id. Null = <see cref="AiHub.ImageModel"/> for the provider.</summary>
    public string? Model { get; init; }

    /// <summary>What the picture should show. Required.</summary>
    public required string Prompt { get; init; }

    /// <summary>A style sentence the app adds ("A flat illustration with soft colours."). Sent after the prompt; the hub does not interpret it.</summary>
    public string? Style { get; init; }

    public AiImageShape Shape { get; init; } = AiImageShape.Square;

    public AiImageQuality Quality { get; init; } = AiImageQuality.Standard;

    /// <summary>How many pictures, 1 to <see cref="AiHub.MaxImagesPerRequest"/>. Each one is charged.</summary>
    public int Count { get; init; } = 1;

    /// <summary>Free text naming what the call is for ("slide-picture"). Lands in the log and the usage ledger.</summary>
    public string Purpose { get; init; } = string.Empty;

    /// <summary>The text sent to the vendor: the prompt, then the style.</summary>
    public string FullPrompt =>
        string.IsNullOrWhiteSpace(Style) ? Prompt.Trim() : $"{Prompt.Trim()}\n\nStyle: {Style.Trim()}";
}

/// <summary>One picture that came back: its bytes, type and size, and the vendor's rewrite of the request when it gives one.</summary>
public sealed record AiGeneratedImage(byte[] Data, string MediaType, int Width, int Height, string? RevisedPrompt)
{
    /// <summary>"png", "jpg" or "webp" — for a file name.</summary>
    public string Extension => MediaType switch
    {
        "image/jpeg" => "jpg",
        "image/webp" => "webp",
        "image/gif"  => "gif",
        _            => "png"
    };
}

/// <summary>What came back from <see cref="AiHub.GenerateImagesAsync"/>, with what an app should tell its user.</summary>
public sealed class AiImageResult
{
    public required IReadOnlyList<AiGeneratedImage> Images { get; init; }
    public required AiProviderType Provider { get; init; }
    public required string Model { get; init; }
    public required string App { get; init; }
    public string Purpose { get; init; } = string.Empty;

    /// <summary>How many pictures were asked for (Images may hold fewer if the vendor sent fewer).</summary>
    public int Count { get; init; }

    /// <summary>Null when the model's price per picture is unknown — show "price unknown", never $0.</summary>
    public double? EstimatedCostUsd { get; init; }
    public TimeSpan Elapsed { get; init; }
    public DateTime CompletedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>A mark the vendor puts in every picture ("SynthID" for Gemini), so an app can say so; null when none is known.</summary>
    public string? Watermark { get; init; }

    /// <summary>Why this provider drew the pictures, in words for the user ("chosen for pictures in AI Settings").</summary>
    public string ProviderReason { get; init; } = string.Empty;
}

/// <summary>Which provider will draw, and why — so a program can tell its user before anything is sent.</summary>
public sealed record AiImageProviderChoice(AiProviderType Provider, string Reason);
