namespace Eaglin.AiManager;

/// <summary>
/// The AI vendors the manager can talk to. Serialised by name (never by number) in every file,
/// so reordering this enum would not break a store — but do not reuse a name for a different vendor.
/// </summary>
public enum AiProviderType
{
    None    = 0,
    Claude  = 1,   // Anthropic
    Gemini  = 2,   // Google
    OpenAi  = 3,
    Mistral = 4,
    XAi     = 5    // xAI (Grok)
}
