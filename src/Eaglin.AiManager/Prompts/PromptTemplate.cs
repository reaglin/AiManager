using System.Collections;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Eaglin.AiManager.Prompts;

/// <summary>
/// A named system + user prompt pair with <c>{placeholders}</c>. Apps ship defaults in code;
/// users edit copies in <c>prompts\&lt;App&gt;\</c> through the Prompt Library window.
/// <see cref="Bind"/> turns a template plus values into an <see cref="AiRequest"/>.
/// </summary>
public sealed class PromptTemplate
{
    private static readonly Regex PlaceholderRx = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    public string    Name        { get; set; } = string.Empty;
    public string    Description { get; set; } = string.Empty;
    public string    System      { get; set; } = string.Empty;
    public string    User        { get; set; } = string.Empty;
    public int?      MaxTokens   { get; set; }

    /// <summary>True once the user has saved their own version; such a file is never overwritten by a newer default.</summary>
    public bool      UserEdited  { get; set; }
    public DateTime? ModifiedUtc { get; set; }

    public PromptTemplate() { }

    public PromptTemplate(string name, string system, string user, string description = "", int? maxTokens = null)
    {
        Name = name; System = system; User = user; Description = description; MaxTokens = maxTokens;
    }

    /// <summary>Distinct placeholder names in System and User, in order of first appearance.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Placeholders =>
        PlaceholderRx.Matches(System + "\n" + User).Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>True when the prompt text and limits match (edit flags and dates ignored).</summary>
    public bool SameTextAs(PromptTemplate other) =>
        other is not null && System == other.System && User == other.User &&
        Description == other.Description && MaxTokens == other.MaxTokens;

    public PromptTemplate Clone() => (PromptTemplate)MemberwiseClone();

    /// <summary>
    /// Fills the placeholders from <paramref name="values"/> — an anonymous object, any object
    /// with public properties, or an <c>IDictionary</c> keyed by name — and returns the request.
    /// Throws <see cref="ArgumentException"/> naming any placeholder left without a value,
    /// so a prompt is never sent with a literal "{text}" in it.
    /// </summary>
    public AiRequest Bind(object? values = null, AiProviderType? provider = null, string? model = null, string? purpose = null, int? maxTokens = null)
    {
        var map = ToMap(values);
        var missing = Placeholders.Where(p => !map.ContainsKey(p)).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"Template '{Name}' needs values for: {string.Join(", ", missing)}", nameof(values));

        return new AiRequest
        {
            Provider  = provider,
            Model     = model,
            System    = Fill(System, map),
            User      = Fill(User, map),
            MaxTokens = maxTokens ?? MaxTokens ?? 4000,
            Purpose   = purpose ?? Name
        };
    }

    private static string Fill(string text, IReadOnlyDictionary<string, string> map) =>
        PlaceholderRx.Replace(text, m => map.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    private static Dictionary<string, string> ToMap(object? values)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (values)
        {
            case null: break;
            case IDictionary dict:
                foreach (DictionaryEntry e in dict)
                    if (e.Key is string k) map[k] = e.Value?.ToString() ?? string.Empty;
                break;
            default:
                foreach (var p in values.GetType().GetProperties())
                    if (p.CanRead && p.GetIndexParameters().Length == 0)
                        map[p.Name] = p.GetValue(values)?.ToString() ?? string.Empty;
                break;
        }
        return map;
    }
}
