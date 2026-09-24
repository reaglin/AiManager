namespace Eaglin.AiManager;

/// <summary>
/// A picture sent with a prompt (1.3.0), for a model that can look at it: "redraw this card as
/// SVG", "what is in this picture". Every provider takes pictures its own way; the hub hides that.
/// A model that cannot see pictures answers with an error, which comes back as an AiException.
/// </summary>
public sealed record AiImage(byte[] Data, string MediaType)
{
    /// <summary>The picture as a data: address, the form the chat-completions APIs take.</summary>
    public string DataUrl => $"data:{MediaType};base64,{Convert.ToBase64String(Data)}";

    public string Base64 => Convert.ToBase64String(Data);

    /// <summary>A picture from a file, its type taken from the extension; null for a type the providers do not take.</summary>
    public static AiImage? FromFile(string path)
    {
        string? type = MediaTypeFor(path);
        return type is null ? null : new AiImage(File.ReadAllBytes(path), type);
    }

    /// <summary>PNG, JPEG, GIF and WebP: the four every vision model accepts.</summary>
    public static string? MediaTypeFor(string pathOrName) =>
        Path.GetExtension(pathOrName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null,
        };
}
