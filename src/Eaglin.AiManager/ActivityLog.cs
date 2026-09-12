using System.Text;
using System.Text.RegularExpressions;

namespace Eaglin.AiManager;

/// <summary>
/// The one activity log for every app: <c>{root}\logs\activity-YYYY-MM.log</c>, one line per
/// event tagged with the app that wrote it. Several apps may append to the same file at once,
/// so each write opens the file with shared access and retries briefly on a sharing violation.
///
/// Long prompt/response bodies are offloaded to <c>logs\prompts\{app}\{timestamp}_{type}.prompt</c>
/// so the main log stays readable (see <see cref="AiCall"/>).
///
/// This logger never throws and never propagates a failure to the caller. It is process-wide
/// (one app per process); <see cref="AiHub.Open"/> initialises it.
/// </summary>
public static class ActivityLog
{
    private static readonly object WriteLock = new();
    private static string  _root = AiPaths.DefaultRoot;
    private static string  _app  = "-";
    private const int PromptBodyThreshold = 200;

    public static string Root => _root;
    public static string App  => _app;

    /// <summary>Current month's log file (the path changes at the month boundary).</summary>
    public static string CurrentLogPath => Path.Combine(AiPaths.LogsDir(_root), $"activity-{DateTime.Now:yyyy-MM}.log");

    public static void Initialize(string root, string app)
    {
        _root = root;
        _app  = string.IsNullOrWhiteSpace(app) ? "-" : app.Trim();
        try { Directory.CreateDirectory(AiPaths.LogsDir(_root)); } catch { /* first write retries */ }
    }

    public static void Info(string context, string message) => Write("INFO ", context, message);
    public static void Warn(string context, string message) => Write("WARN ", context, message);

    public static void Error(string context, string message, Exception? ex = null)
    {
        var full = ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}";
        Write("ERROR", context, full);
    }

    /// <summary>
    /// Logs an AI call. A message of the form "META>>\nBODY" with a long body writes the body
    /// to a .prompt file and records the path instead.
    /// </summary>
    public static void AiCall(string context, string message)
    {
        const string sep = ">>\n";
        var splitIdx = message.IndexOf(sep, StringComparison.Ordinal);
        if (splitIdx >= 0)
        {
            var metadata = message[..splitIdx].Trim();
            var body     = message[(splitIdx + sep.Length)..];
            if (body.Length >= PromptBodyThreshold)
            {
                var ts   = DateTime.Now;
                var file = SavePromptFile(context, ts, ExtractTypeTag(metadata), body);
                Write("AI   ", context, file is not null ? $"{metadata} → {MakeRelative(file)}" : $"{metadata} (body not saved)", ts);
                return;
            }
        }
        Write("AI   ", context, message);
    }

    private static string? SavePromptFile(string context, DateTime ts, string typeTag, string body)
    {
        try
        {
            var promptDir = Path.Combine(AiPaths.LogsDir(_root), "prompts", AiPaths.SafeName(_app));
            Directory.CreateDirectory(promptDir);

            var filePath = Path.Combine(promptDir, $"{ts:yyyy-MM-dd_HH-mm-ss}_{typeTag}.prompt");
            for (int n = 2; File.Exists(filePath); n++)
                filePath = Path.Combine(promptDir, $"{ts:yyyy-MM-dd_HH-mm-ss}_{typeTag}_{n}.prompt");

            lock (WriteLock)
            {
                File.WriteAllText(filePath,
                    $"App:       {_app}\r\nContext:   {context}\r\nTimestamp: {ts:yyyy-MM-dd HH:mm:ss.fff}\r\nType:      {typeTag}\r\n" +
                    $"Length:    {body.Length} chars\r\n{new string('─', 60)}\r\n\r\n{body}",
                    Encoding.UTF8);
            }
            return filePath;
        }
        catch { return null; }
    }

    private static void Write(string level, string context, string message, DateTime? tsOverride = null)
    {
        var ts    = tsOverride ?? DateTime.Now;
        var entry = $"[{ts:yyyy-MM-dd HH:mm:ss.fff}]  {level}  {_app,-16}  {context,-28}  {message}{Environment.NewLine}";
        var bytes = Encoding.UTF8.GetBytes(entry);

        lock (WriteLock)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(AiPaths.LogsDir(_root));
                    using var fs = new FileStream(CurrentLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    fs.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (IOException) { Thread.Sleep(10 * (attempt + 1)); }   // another app is writing
                catch { return; }                                              // never affect the caller
            }
        }
    }

    private static string ExtractTypeTag(string metadata)
    {
        var m = Regex.Match(metadata, @"^([A-Z]+)");
        return m.Success ? m.Groups[1].Value : "BODY";
    }

    private static string MakeRelative(string fullPath)
    {
        try
        {
            var rel = Path.GetRelativePath(AiPaths.LogsDir(_root), fullPath);
            return rel.Length < fullPath.Length ? rel : fullPath;
        }
        catch { return fullPath; }
    }
}
