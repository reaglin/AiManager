using Eaglin.AiManager.Store;

namespace Eaglin.AiManager.Catalog;

/// <summary>
/// Estimated cost per call. Prices are USD per million tokens (input, output) and are matched
/// by the longest prefix of the model id, so dated or suffixed ids still price. The user can
/// correct or add prices in <c>settings.json</c> (<see cref="AiSettings.PriceOverrides"/>);
/// an override matches exactly first, then by prefix. Every figure the library produces from
/// this table is an <em>estimate</em> and is labelled so in the UIs.
/// </summary>
public static class Pricing
{
    /// <summary>Built-in table, as of 2026-09-12. Anthropic figures are from the current price list; the others are the vendors' published list prices at the time and should be checked when a bill matters.</summary>
    public static readonly IReadOnlyList<(string Prefix, ModelPrice Price)> BuiltIn = new (string, ModelPrice)[]
    {
        ("claude-fable-5-1",   new(10.00, 50.00)),
        ("claude-fable-5",     new(10.00, 50.00)),
        ("claude-opus-5",      new( 5.00, 25.00)),
        ("claude-opus-4",      new( 5.00, 25.00)),
        ("claude-sonnet-5",    new( 2.00, 10.00)),
        ("claude-sonnet-4-6",  new( 3.00, 15.00)),
        ("claude-haiku-4-5",   new( 1.00,  5.00)),
        ("gpt-4o-mini",        new( 0.15,  0.60)),
        ("gpt-4o",             new( 2.50, 10.00)),
        ("gemini-2.5-flash",   new( 0.30,  2.50)),
        ("gemini-2.5-pro",     new( 1.25, 10.00)),
        ("mistral-large",      new( 2.00,  6.00)),
        ("mistral-medium",     new( 0.40,  2.00)),
        ("mistral-small",      new( 0.10,  0.30)),
        ("grok-4",             new( 3.00, 15.00)),
        ("grok-3-mini",        new( 0.30,  0.50)),
        ("grok-3",             new( 3.00, 15.00))
    };

    /// <summary>"models/gemini-2.5-flash" → "gemini-2.5-flash"; case-insensitive matching.</summary>
    public static string Normalize(string model)
    {
        var m = (model ?? string.Empty).Trim().ToLowerInvariant();
        if (m.StartsWith("models/", StringComparison.Ordinal)) m = m["models/".Length..];
        return m;
    }

    public static ModelPrice? PriceFor(string model, IReadOnlyDictionary<string, ModelPrice>? overrides = null)
    {
        var id = Normalize(model);
        if (id.Length == 0) return null;

        if (overrides is { Count: > 0 })
        {
            ModelPrice? best = null; int bestLen = -1;
            foreach (var (key, price) in overrides)
            {
                var k = Normalize(key);
                if (id.StartsWith(k, StringComparison.Ordinal) && k.Length > bestLen) { best = price; bestLen = k.Length; }
            }
            if (best is not null) return best;
        }

        ModelPrice? found = null; int foundLen = -1;
        foreach (var (prefix, price) in BuiltIn)
            if (id.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > foundLen) { found = price; foundLen = prefix.Length; }
        return found;
    }

    /// <summary>Null when the model has no price; tokens are still worth recording.</summary>
    public static double? Estimate(string model, int inputTokens, int outputTokens, IReadOnlyDictionary<string, ModelPrice>? overrides = null)
    {
        var p = PriceFor(model, overrides);
        if (p is null) return null;
        return inputTokens / 1_000_000.0 * p.InputPerMTok + outputTokens / 1_000_000.0 * p.OutputPerMTok;
    }
}
