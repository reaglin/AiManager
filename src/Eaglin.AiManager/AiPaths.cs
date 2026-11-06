namespace Eaglin.AiManager;

/// <summary>
/// Where the shared store lives. <c>Documents\AiManager\</c> by default — Documents rather than
/// AppData because MSIX-packaged Store apps get a private, virtualised AppData and would each
/// see their own copy; every app, packaged or not, sees Documents the same way. Tests and the
/// self-test tool pass their own root.
/// </summary>
public static class AiPaths
{
    public const string FolderName = "AiManager";

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), FolderName);

    public static string CredentialsFile(string root) => Path.Combine(root, "credentials.json");
    public static string SettingsFile(string root)    => Path.Combine(root, "settings.json");
    public static string ModelsFile(string root)      => Path.Combine(root, "models.json");
    public static string LogsDir(string root)         => Path.Combine(root, "logs");
    public static string UsageDir(string root)        => Path.Combine(root, "usage");
    /// <summary>Text-to-speech files (1.8.0): the cached voice list <c>voices.json</c>.</summary>
    public static string SpeechDir(string root)       => Path.Combine(root, "speech");
    public static string VoicesFile(string root)      => Path.Combine(root, "speech", "voices.json");
    public static string PromptsDir(string root, string app) => Path.Combine(root, "prompts", SafeName(app));

    /// <summary>An app name made safe for use as a folder or file-name segment.</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "app" : cleaned;
    }
}
