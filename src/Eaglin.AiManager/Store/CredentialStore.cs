using System.Text;
using System.Text.Json;

namespace Eaglin.AiManager.Store;

/// <summary>
/// The shared API-key file: <c>{root}\credentials.json</c>. Keys are DPAPI-encrypted on disk
/// (<see cref="Dpapi"/>) and plain text only in this object's memory. Writes are temp-then-move.
/// A missing or corrupt file is an empty store (logged), never an exception.
/// </summary>
public sealed class CredentialStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Path { get; }

    private readonly Dictionary<AiProviderType, Entry> _entries = new();

    /// <summary>The Google text-to-speech credential (1.8.0), or null when none is stored.</summary>
    public GoogleTtsCredential? GoogleTts { get; private set; }

    public sealed record Entry(string Key, DateTime? UpdatedUtc, string? UpdatedBy);

    public CredentialStore(string root)
    {
        Path = AiPaths.CredentialsFile(root);
        Reload();
    }

    public bool FileExists => File.Exists(Path);

    /// <summary>Re-reads the file (another app may have saved a key since this object was created).</summary>
    public void Reload()
    {
        _entries.Clear();
        GoogleTts = null;
        try
        {
            if (!File.Exists(Path)) return;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(Path, Encoding.UTF8), Json);
            if (dto is null) return;
            if (dto.Services?.GetValueOrDefault(GoogleTtsServiceName) is { } g) GoogleTts = ReadGoogleTts(g);
            if (dto.Providers is null) return;

            foreach (var (name, e) in dto.Providers)
            {
                if (!Enum.TryParse<AiProviderType>(name, ignoreCase: true, out var p) || p is AiProviderType.None or AiProviderType.GoogleTts) continue;
                var key = Dpapi.Unprotect(e.ApiKeyEncrypted ?? string.Empty);
                if (key.Length == 0) continue;
                _entries[p] = new Entry(key, e.UpdatedUtc, e.UpdatedBy);
            }
        }
        catch (Exception ex)
        {
            _entries.Clear();
            GoogleTts = null;
            ActivityLog.Error("CredentialStore", $"Could not read {Path}; treating as empty", ex);
        }
    }

    public string KeyFor(AiProviderType p) => _entries.TryGetValue(p, out var e) ? e.Key : string.Empty;
    public bool   HasKey(AiProviderType p) => KeyFor(p).Length > 0;
    public Entry? EntryFor(AiProviderType p) => _entries.TryGetValue(p, out var e) ? e : null;

    public IReadOnlyList<AiProviderType> ProvidersWithKeys =>
        AiProviderRouter.AllProviders.Where(HasKey).ToList();

    /// <summary>Sets (or, with a blank key, removes) a key in memory. Call <see cref="Save"/> to persist.</summary>
    public void SetKey(AiProviderType p, string key, string updatedBy)
    {
        if (p == AiProviderType.None) throw new ArgumentException("Choose a provider.", nameof(p));
        if (p == AiProviderType.GoogleTts) throw new ArgumentException("Google text-to-speech has its own credential: use SetGoogleTtsApiKey or SetGoogleTtsServiceAccount.", nameof(p));
        key = key?.Trim() ?? string.Empty;
        if (key.Length == 0) { _entries.Remove(p); return; }
        _entries[p] = new Entry(key, DateTime.UtcNow, updatedBy);
    }

    // ── Google text-to-speech (1.8.0) ────────────────────────────────────────

    /// <summary>The name of the Google text-to-speech entry in the file's <c>Services</c> section.</summary>
    public const string GoogleTtsServiceName = "GoogleTts";

    /// <summary>Stores a Google Cloud API key for text-to-speech (in memory; call <see cref="Save"/>). A blank key clears it.</summary>
    public void SetGoogleTtsApiKey(string apiKey, string updatedBy)
    {
        apiKey = apiKey?.Trim() ?? string.Empty;
        if (apiKey.Length == 0) { GoogleTts = null; return; }
        GoogleTts = new GoogleTtsCredential(GoogleTtsCredentialKind.ApiKey, apiKey, null, null, DateTime.UtcNow, updatedBy);
    }

    /// <summary>
    /// Stores the <em>content</em> of a Google Cloud service-account key file (in memory; call
    /// <see cref="Save"/>). The content is kept, not the path, so moving the file later does not
    /// matter. Throws <see cref="ArgumentException"/> in plain words when it is not a service-account key.
    /// </summary>
    public void SetGoogleTtsServiceAccount(string json, string updatedBy)
    {
        var sa = Speech.GoogleServiceAccount.Parse(json);
        GoogleTts = new GoogleTtsCredential(GoogleTtsCredentialKind.ServiceAccount, json.Trim(), sa.ProjectId, sa.ClientEmail, DateTime.UtcNow, updatedBy);
    }

    /// <summary>Removes the Google text-to-speech credential (in memory; call <see cref="Save"/>).</summary>
    public void ClearGoogleTts() => GoogleTts = null;

    private static GoogleTtsCredential? ReadGoogleTts(ServiceDto g)
    {
        var key = Dpapi.Unprotect(g.ApiKeyEncrypted ?? string.Empty);
        if (key.Length > 0)
            return new GoogleTtsCredential(GoogleTtsCredentialKind.ApiKey, key, g.ProjectId, null, g.UpdatedUtc, g.UpdatedBy);
        var json = Dpapi.Unprotect(g.ServiceAccountJsonEncrypted ?? string.Empty);
        return json.Length > 0
            ? new GoogleTtsCredential(GoogleTtsCredentialKind.ServiceAccount, json, g.ProjectId, g.ClientEmail, g.UpdatedUtc, g.UpdatedBy)
            : null;
    }

    public void Save()
    {
        var dto = new Dto
        {
            Providers = _entries.OrderBy(kv => kv.Key).ToDictionary(
                kv => kv.Key.ToString(),
                kv => new EntryDto { ApiKeyEncrypted = Dpapi.Protect(kv.Value.Key), UpdatedUtc = kv.Value.UpdatedUtc, UpdatedBy = kv.Value.UpdatedBy })
        };
        if (GoogleTts is { } g)
            dto.Services = new Dictionary<string, ServiceDto>
            {
                [GoogleTtsServiceName] = new ServiceDto
                {
                    ApiKeyEncrypted            = g.Kind == GoogleTtsCredentialKind.ApiKey ? Dpapi.Protect(g.Secret) : null,
                    ServiceAccountJsonEncrypted = g.Kind == GoogleTtsCredentialKind.ServiceAccount ? Dpapi.Protect(g.Secret) : null,
                    ProjectId = g.ProjectId, ClientEmail = g.ClientEmail, UpdatedUtc = g.UpdatedUtc, UpdatedBy = g.UpdatedBy
                }
            };

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Json), Encoding.UTF8);
        File.Move(tmp, Path, overwrite: true);
        ActivityLog.Info("CredentialStore", $"Saved keys for: {string.Join(", ", _entries.Keys.OrderBy(k => k))}{(GoogleTts is { } t ? $"; Google text-to-speech ({t.Kind})" : "")}");
    }

    private sealed class Dto
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, EntryDto> Providers { get; set; } = new();
        /// <summary>Credentials that are not chat providers (1.8.0: Google text-to-speech). Absent when empty.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, ServiceDto>? Services { get; set; }
    }

    private sealed class ServiceDto
    {
        public string?   ApiKeyEncrypted             { get; set; }
        public string?   ServiceAccountJsonEncrypted { get; set; }
        public string?   ProjectId                   { get; set; }
        public string?   ClientEmail                 { get; set; }
        public DateTime? UpdatedUtc                  { get; set; }
        public string?   UpdatedBy                   { get; set; }
    }

    private sealed class EntryDto
    {
        public string?   ApiKeyEncrypted { get; set; }
        public DateTime? UpdatedUtc      { get; set; }
        public string?   UpdatedBy       { get; set; }
    }
}

/// <summary>How the Google text-to-speech credential signs in (1.8.0).</summary>
public enum GoogleTtsCredentialKind
{
    /// <summary>A Google Cloud API key, sent with each request.</summary>
    ApiKey,
    /// <summary>A service-account key file (JSON); a sign-in token is made from it.</summary>
    ServiceAccount
}

/// <summary>
/// The stored Google text-to-speech credential. <see cref="Secret"/> is the API key or the whole
/// service-account JSON, plain text in memory only. <see cref="ToString"/> never shows it.
/// </summary>
public sealed record GoogleTtsCredential(GoogleTtsCredentialKind Kind, string Secret, string? ProjectId, string? ClientEmail,
                                         DateTime? UpdatedUtc, string? UpdatedBy)
{
    /// <summary>For a person: "an API key ending …x7Qa" or "the service account tts@project.iam…".</summary>
    public string Describe() => Kind == GoogleTtsCredentialKind.ApiKey
        ? $"an API key ending …{(Secret.Length > 4 ? Secret[^4..] : Secret)}"
        : $"the service account {ClientEmail}{(string.IsNullOrEmpty(ProjectId) ? "" : $" (project {ProjectId})")}";

    public override string ToString() => $"Google text-to-speech: {Describe()}";
}
