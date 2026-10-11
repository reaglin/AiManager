using Eaglin.AiManager.Providers;

namespace Eaglin.AiManager;

/// <summary>
/// Display names, built-in default models and the fallback model lists (used until
/// <c>ModelCatalog</c> has fetched live lists), plus the one place a provider object is built.
/// </summary>
public static class AiProviderRouter
{
    private static readonly Dictionary<AiProviderType, string> DisplayNames = new()
    {
        { AiProviderType.Claude,  "Claude (Anthropic)" },
        { AiProviderType.Gemini,  "Gemini (Google)"    },
        { AiProviderType.OpenAi,  "OpenAI"             },
        { AiProviderType.Mistral, "Mistral AI"         },
        { AiProviderType.XAi,     "xAI (Grok)"         }
    };

    /// <summary>Model used when neither the app nor the global settings name one.</summary>
    public static readonly IReadOnlyDictionary<AiProviderType, string> DefaultModels = new Dictionary<AiProviderType, string>
    {
        { AiProviderType.Claude,  "claude-opus-5"           },
        { AiProviderType.Gemini,  "models/gemini-2.5-flash" },
        { AiProviderType.OpenAi,  "gpt-4o"                  },
        { AiProviderType.Mistral, "mistral-large-latest"    },
        { AiProviderType.XAi,     "grok-4"                  }
    };

    /// <summary>Choices offered before a live catalog fetch; the user may also type any other id.</summary>
    public static readonly IReadOnlyDictionary<AiProviderType, string[]> FallbackModels = new Dictionary<AiProviderType, string[]>
    {
        { AiProviderType.Claude,  new[] { "claude-opus-5", "claude-sonnet-5", "claude-fable-5-1", "claude-haiku-4-5" } },
        { AiProviderType.Gemini,  new[] { "models/gemini-2.5-flash", "models/gemini-2.5-pro" } },
        { AiProviderType.OpenAi,  new[] { "gpt-4o", "gpt-4o-mini" } },
        { AiProviderType.Mistral, new[] { "mistral-large-latest", "mistral-medium-latest", "mistral-small-latest" } },
        { AiProviderType.XAi,     new[] { "grok-4", "grok-3", "grok-3-mini" } }
    };

    /// <summary>
    /// The page on each vendor's site where a signed-in user creates an API key. Shown as a link
    /// in the settings UIs so a first-time user knows where a key comes from. The steps on each
    /// page are in <see cref="Help.KeyHelp"/>; change the two together (docs/PROVIDER-KEY-HELP.md).
    /// </summary>
    public static readonly IReadOnlyDictionary<AiProviderType, string> KeyPages = new Dictionary<AiProviderType, string>
    {
        { AiProviderType.Claude,  "https://platform.claude.com/settings/keys"   },
        { AiProviderType.Gemini,  "https://aistudio.google.com/apikey"          },
        { AiProviderType.OpenAi,  "https://platform.openai.com/api-keys"        },
        { AiProviderType.Mistral, "https://console.mistral.ai/api-keys"         },
        { AiProviderType.XAi,     "https://console.x.ai/team/default/api-keys"  }
    };

    /// <summary>All provider types except None, in enum order.</summary>
    public static IReadOnlyList<AiProviderType> AllProviders { get; } =
        Enum.GetValues<AiProviderType>().Where(p => p != AiProviderType.None).ToList();

    public static string DisplayName(AiProviderType provider) =>
        DisplayNames.TryGetValue(provider, out var n) ? n : provider.ToString();

    /// <summary>Builds a provider. The key may be blank — the provider then reports <c>HasApiKey == false</c>.</summary>
    public static IAiProvider Build(AiProviderType type, string apiKey, string model, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(model))
            model = DefaultModels.TryGetValue(type, out var d) ? d : string.Empty;

        return type switch
        {
            AiProviderType.Claude  => new ClaudeProvider (apiKey, model, http),
            AiProviderType.Gemini  => new GeminiProvider (apiKey, model, http),
            AiProviderType.OpenAi  => new OpenAiProvider (apiKey, model, http),
            AiProviderType.Mistral => new MistralProvider(apiKey, model, http),
            AiProviderType.XAi     => new XAiProvider    (apiKey, model, http),
            _ => throw new ArgumentException($"Unknown provider: {type}", nameof(type))
        };
    }
}
