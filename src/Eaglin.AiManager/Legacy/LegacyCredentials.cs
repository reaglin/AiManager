using System.Text;
using System.Text.Json;
using Eaglin.AiManager.Store;

namespace Eaglin.AiManager.Legacy;

/// <summary>Keys found in one app's pre-AiManager settings file, decrypted in memory.</summary>
public sealed record LegacySource(string App, string Path, IReadOnlyDictionary<AiProviderType, string> Keys)
{
    public IReadOnlyList<AiProviderType> Providers => Keys.Keys.OrderBy(k => k).ToList();
}

/// <summary>Where each app kept its keys before the shared store. Override in tests.</summary>
public sealed class LegacyPaths
{
    private static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private static string AppData   => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>DPAPI, PascalCase <c>*ApiKeyEncrypted</c> fields at the root.</summary>
    public string PreseMaker { get; init; } = Path.Combine(Documents, "PreseMaker", "app_settings.json");

    /// <summary>DPAPI, same field names as PreseMaker (it copied the scheme).</summary>
    public string AuthorPlus { get; init; } = Path.Combine(AppData, "AuthorPlus", "ai_settings.json");

    /// <summary>Plain text, under an <c>AiSettings</c> object: <c>ClaudeApiKey</c> … (either casing).</summary>
    public string CiatleFce  { get; init; } = Path.Combine(AppData, "CIATLE-FCE", "prefs.json");
}

/// <summary>
/// Finds API keys the existing apps saved before the AI Manager existed and copies them into the
/// shared <see cref="CredentialStore"/>. Discovery only reads; import is explicit (a button in the
/// settings UI, or <c>--import</c> in the self-test tool). Legacy files are never modified.
/// </summary>
public static class LegacyCredentials
{
    public static IReadOnlyList<LegacySource> Discover(LegacyPaths? paths = null)
    {
        paths ??= new LegacyPaths();
        var found = new List<LegacySource>();
        Add(found, ReadDpapiPascal("PreseMaker", paths.PreseMaker));
        Add(found, ReadDpapiPascal("AuthorPlus", paths.AuthorPlus));
        Add(found, ReadCiatleFce(paths.CiatleFce));
        return found;

        static void Add(List<LegacySource> list, LegacySource? s) { if (s is { Keys.Count: > 0 }) list.Add(s); }
    }

    /// <summary>
    /// Copies a source's keys into the store (in memory; the caller saves). Existing keys are kept
    /// unless <paramref name="overwrite"/>. Returns the number of keys written.
    /// </summary>
    public static int Import(CredentialStore store, LegacySource source, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(source);
        int n = 0;
        foreach (var (p, key) in source.Keys)
        {
            if (!overwrite && store.HasKey(p)) continue;
            store.SetKey(p, key, $"import:{source.App}");
            n++;
        }
        ActivityLog.Info("LegacyCredentials", $"Imported {n} key(s) from {source.App} ({source.Path})");
        return n;
    }

    // ── Google text-to-speech (1.8.0) ─────────────────────────────────────────

    /// <summary>
    /// The service-account file PreseMaker (CourseBuilder+) narrates with — the path in its
    /// <c>app_settings.json</c> (<c>TtsCredentialsFilePath</c>) — when the file is there and is a
    /// service-account key; else null.
    /// </summary>
    public static string? FindGoogleTtsServiceAccount(LegacyPaths? paths = null)
    {
        paths ??= new LegacyPaths();
        var root = TryParse(paths.PreseMaker);
        if (root is null) return null;
        var file = GetString(root.Value, "TtsCredentialsFilePath").Trim();
        if (file.Length == 0 || !File.Exists(file)) return null;
        try
        {
            Speech.GoogleServiceAccount.Parse(File.ReadAllText(file, Encoding.UTF8));
            return file;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Copies PreseMaker's service-account file into the store as the Google text-to-speech
    /// credential (in memory; the caller saves) — only when none is stored, unless
    /// <paramref name="overwrite"/>. Returns true when it copied one. The file is not changed.
    /// </summary>
    public static bool ImportGoogleTts(CredentialStore store, LegacyPaths? paths = null, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.GoogleTts is not null && !overwrite) return false;
        var file = FindGoogleTtsServiceAccount(paths);
        if (file is null) return false;
        try { store.SetGoogleTtsServiceAccount(File.ReadAllText(file, Encoding.UTF8), "import:PreseMaker"); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("LegacyCredentials", $"Could not import the text-to-speech service account from {file}", ex);
            return false;
        }
        ActivityLog.Info("LegacyCredentials", $"Imported PreseMaker's text-to-speech service account ({file})");
        return true;
    }

    // ── Readers ───────────────────────────────────────────────────────────────

    private static readonly (string Field, AiProviderType Provider)[] PascalFields =
    {
        ("ClaudeApiKeyEncrypted",  AiProviderType.Claude),
        ("GeminiApiKeyEncrypted",  AiProviderType.Gemini),
        ("OpenAiApiKeyEncrypted",  AiProviderType.OpenAi),
        ("MistralApiKeyEncrypted", AiProviderType.Mistral),
        ("XAiApiKeyEncrypted",     AiProviderType.XAi)
    };

    private static readonly (string Field, AiProviderType Provider)[] PlainFields =
    {
        ("ClaudeApiKey",  AiProviderType.Claude),
        ("GeminiApiKey",  AiProviderType.Gemini),
        ("OpenAiApiKey",  AiProviderType.OpenAi),
        ("MistralApiKey", AiProviderType.Mistral)
    };

    private static LegacySource? ReadDpapiPascal(string app, string path)
    {
        var root = TryParse(path);
        if (root is null) return null;
        var keys = new Dictionary<AiProviderType, string>();
        foreach (var (field, p) in PascalFields)
        {
            var enc = GetString(root.Value, field);
            var key = Dpapi.Unprotect(enc);
            if (key.Length > 0) keys[p] = key;
        }
        return new LegacySource(app, path, keys);
    }

    private static LegacySource? ReadCiatleFce(string path)
    {
        var root = TryParse(path);
        if (root is null) return null;
        if (!TryGetPropertyIgnoreCase(root.Value, "AiSettings", out var ai) || ai.ValueKind != JsonValueKind.Object)
            return null;
        var keys = new Dictionary<AiProviderType, string>();
        foreach (var (field, p) in PlainFields)
        {
            var key = GetString(ai, field).Trim();
            if (key.Length > 0) keys[p] = key;
        }
        return new LegacySource("CIATLE-FCE", path, keys);
    }

    private static JsonElement? TryParse(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch { return null; }
    }

    private static string GetString(JsonElement obj, string name) =>
        TryGetPropertyIgnoreCase(obj, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
