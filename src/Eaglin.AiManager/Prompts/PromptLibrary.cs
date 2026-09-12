using System.Text;
using System.Text.Json;

namespace Eaglin.AiManager.Prompts;

/// <summary>
/// One app's prompt templates on disk: <c>{root}\prompts\{App}\{name}.json</c>. The app registers
/// its built-in defaults at startup; the library writes any that are missing and refreshes ones
/// the user has never edited, but never touches a file the user saved themselves.
/// </summary>
public sealed class PromptLibrary
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string App { get; }
    public string Dir { get; }

    private readonly Dictionary<string, PromptTemplate> _defaults = new(StringComparer.OrdinalIgnoreCase);

    public PromptLibrary(string root, string app)
    {
        App = app;
        Dir = AiPaths.PromptsDir(root, app);
    }

    public string PathFor(string name) => Path.Combine(Dir, AiPaths.SafeName(name) + ".json");

    public bool HasDefault(string name) => _defaults.ContainsKey(name);
    public bool ExistsOnDisk(string name) => File.Exists(PathFor(name));

    /// <summary>
    /// Registers the app's built-in templates. Missing files are written; files whose text
    /// still matches an older default (not user-edited) are updated to the new default;
    /// user-edited files are left alone.
    /// </summary>
    public void RegisterDefaults(IEnumerable<PromptTemplate> defaults)
    {
        foreach (var d in defaults)
        {
            if (string.IsNullOrWhiteSpace(d.Name)) throw new ArgumentException("A template needs a name.", nameof(defaults));
            var copy = d.Clone();
            copy.UserEdited = false;
            _defaults[copy.Name] = copy;

            var onDisk = TryRead(copy.Name);
            if (onDisk is null || (!onDisk.UserEdited && !onDisk.SameTextAs(copy)))
                Write(copy, userEdited: false);
        }
    }

    /// <summary>The user's version if one exists on disk, else the registered default.</summary>
    public PromptTemplate Get(string name)
    {
        return TryRead(name)
               ?? (_defaults.TryGetValue(name, out var d) ? d.Clone()
                   : throw new KeyNotFoundException($"No prompt template named '{name}' for {App}."));
    }

    /// <summary>Every template known for this app: files on disk plus defaults not yet written, by name.</summary>
    public IReadOnlyList<PromptTemplate> List()
    {
        var byName = new Dictionary<string, PromptTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _defaults.Values) byName[d.Name] = d.Clone();
        if (Directory.Exists(Dir))
        {
            foreach (var file in Directory.GetFiles(Dir, "*.json"))
            {
                var t = TryReadFile(file);
                if (t is not null && !string.IsNullOrWhiteSpace(t.Name)) byName[t.Name] = t;
            }
        }
        return byName.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Saves the user's version. Marks it edited so later defaults do not overwrite it.</summary>
    public void Save(PromptTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (string.IsNullOrWhiteSpace(template.Name)) throw new ArgumentException("A template needs a name.", nameof(template));
        Write(template, userEdited: true);
        ActivityLog.Info("PromptLibrary", $"{App}: saved template '{template.Name}'");
    }

    /// <summary>Restores the registered default over the user's version.</summary>
    public PromptTemplate ResetToDefault(string name)
    {
        if (!_defaults.TryGetValue(name, out var d))
            throw new KeyNotFoundException($"'{name}' has no built-in default to reset to.");
        Write(d, userEdited: false);
        ActivityLog.Info("PromptLibrary", $"{App}: reset template '{name}' to default");
        return d.Clone();
    }

    /// <summary>Removes a user-created template (one with no default). A default's file is reset instead.</summary>
    public void Delete(string name)
    {
        if (_defaults.ContainsKey(name)) { ResetToDefault(name); return; }
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private void Write(PromptTemplate t, bool userEdited)
    {
        var copy = t.Clone();
        copy.UserEdited  = userEdited;
        copy.ModifiedUtc = DateTime.UtcNow;
        Directory.CreateDirectory(Dir);
        var path = PathFor(copy.Name);
        var tmp  = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(copy, Json), Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    private PromptTemplate? TryRead(string name) => TryReadFile(PathFor(name));

    private static PromptTemplate? TryReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<PromptTemplate>(File.ReadAllText(path, Encoding.UTF8), Json);
        }
        catch (Exception ex)
        {
            ActivityLog.Error("PromptLibrary", $"Could not read {path}", ex);
            return null;
        }
    }
}
