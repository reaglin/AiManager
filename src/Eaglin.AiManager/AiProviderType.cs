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
    XAi     = 5,   // xAI (Grok)
    /// <summary>
    /// Google Cloud Text-to-Speech (1.8.0). Not a chat provider: it is left out of
    /// <see cref="AiProviderRouter.AllProviders"/>, has its own credential (an API key or a
    /// service-account file, <c>CredentialStore.GoogleTts</c>), and appears only on speech lines
    /// of the usage ledger and in <see cref="AiException.Provider"/> for speech errors.
    /// </summary>
    GoogleTts = 6
}
