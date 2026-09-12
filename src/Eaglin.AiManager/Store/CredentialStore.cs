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
        try
        {
            if (!File.Exists(Path)) return;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(Path, Encoding.UTF8), Json);
            if (dto?.Providers is null) return;

            foreach (var (name, e) in dto.Providers)
            {
                if (!Enum.TryParse<AiProviderType>(name, ignoreCase: true, out var p) || p == AiProviderType.None) continue;
                var key = Dpapi.Unprotect(e.ApiKeyEncrypted ?? string.Empty);
                if (key.Length == 0) continue;
                _entries[p] = new Entry(key, e.UpdatedUtc, e.UpdatedBy);
            }
        }
        catch (Exception ex)
        {
            _entries.Clear();
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
        key = key?.Trim() ?? string.Empty;
        if (key.Length == 0) { _entries.Remove(p); return; }
        _entries[p] = new Entry(key, DateTime.UtcNow, updatedBy);
    }

    public void Save()
    {
        var dto = new Dto
        {
            Providers = _entries.OrderBy(kv => kv.Key).ToDictionary(
                kv => kv.Key.ToString(),
                kv => new EntryDto { ApiKeyEncrypted = Dpapi.Protect(kv.Value.Key), UpdatedUtc = kv.Value.UpdatedUtc, UpdatedBy = kv.Value.UpdatedBy })
        };

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Json), Encoding.UTF8);
        File.Move(tmp, Path, overwrite: true);
        ActivityLog.Info("CredentialStore", $"Saved keys for: {string.Join(", ", _entries.Keys.OrderBy(k => k))}");
    }

    private sealed class Dto
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, EntryDto> Providers { get; set; } = new();
    }

    private sealed class EntryDto
    {
        public string?   ApiKeyEncrypted { get; set; }
        public DateTime? UpdatedUtc      { get; set; }
        public string?   UpdatedBy       { get; set; }
    }
}
