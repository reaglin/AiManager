using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eaglin.AiManager.Usage;

/// <summary>One line of the ledger: a call that completed, failed or was cancelled.</summary>
public sealed class UsageRecord
{
    public DateTime       T         { get; set; } = DateTime.UtcNow;
    public string         App       { get; set; } = string.Empty;
    public AiProviderType Provider  { get; set; }
    public string         Model     { get; set; } = string.Empty;
    public string         Purpose   { get; set; } = string.Empty;
    public int            InTok     { get; set; }
    public int            OutTok    { get; set; }
    public double?        CostUsd   { get; set; }
    public long           Ms        { get; set; }
    public bool           Ok        { get; set; }
    public string?        Error     { get; set; }
    public bool           Streamed  { get; set; }
    public bool           Cancelled { get; set; }
}

/// <summary>Totals for one slice of the ledger (a month, an app, a model, a day).</summary>
public sealed class UsageGroup
{
    public string Key          { get; init; } = string.Empty;
    public int    Prompts      { get; set; }
    public int    Succeeded    { get; set; }
    public int    Failed       { get; set; }
    public int    Cancelled    { get; set; }
    public long   InputTokens  { get; set; }
    public long   OutputTokens { get; set; }
    public double CostUsd      { get; set; }
    public long   TotalMs      { get; set; }
    public double AverageMs    => Prompts == 0 ? 0 : (double)TotalMs / Prompts;

    internal void Add(UsageRecord r)
    {
        Prompts++;
        if (r.Cancelled) Cancelled++; else if (r.Ok) Succeeded++; else Failed++;
        InputTokens  += r.InTok;
        OutputTokens += r.OutTok;
        CostUsd      += r.CostUsd ?? 0;
        TotalMs      += r.Ms;
    }
}

public sealed class UsageSummary
{
    public required string Month { get; init; }          // "2026-09"
    public string? App { get; init; }                    // null = every app
    public required UsageGroup Totals { get; init; }
    public required IReadOnlyList<UsageGroup> ByApp   { get; init; }
    public required IReadOnlyList<UsageGroup> ByModel { get; init; }   // key "Provider/model"
    public required IReadOnlyList<UsageGroup> ByDay   { get; init; }   // key "yyyy-MM-dd", ascending
}

/// <summary>
/// The usage ledger: <c>{root}\usage\usage-YYYY-MM.jsonl</c>, one JSON object per call, appended
/// by every app. Month files stay small; the dashboard reads one at a time. Appends open the
/// file with shared access and retry, like the activity log.
/// </summary>
public sealed class UsageLedger
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly object WriteLock = new();

    public string Root { get; }

    public UsageLedger(string root) => Root = root;

    public string PathFor(DateTime month) =>
        Path.Combine(AiPaths.UsageDir(Root), $"usage-{month:yyyy-MM}.jsonl");

    /// <summary>Appends a record to the month file of its local time. Never throws.</summary>
    public void Append(UsageRecord record)
    {
        var line  = JsonSerializer.Serialize(record, Json) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(line);
        var path  = PathFor(record.T.ToLocalTime());

        lock (WriteLock)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(AiPaths.UsageDir(Root));
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    fs.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (IOException) { Thread.Sleep(10 * (attempt + 1)); }
                catch (Exception ex) { ActivityLog.Error("UsageLedger", "append failed", ex); return; }
            }
        }
    }

    /// <summary>Every record of a month, in file order. Unreadable lines are skipped.</summary>
    public IReadOnlyList<UsageRecord> Read(DateTime month)
    {
        var path = PathFor(month);
        var list = new List<UsageRecord>();
        if (!File.Exists(path)) return list;

        string[] lines;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch (Exception ex)
        {
            ActivityLog.Error("UsageLedger", $"Could not read {path}", ex);
            return list;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                var r = JsonSerializer.Deserialize<UsageRecord>(line, Json);
                if (r is not null) list.Add(r);
            }
            catch (JsonException) { /* skip a torn line */ }
        }
        return list;
    }

    public UsageSummary Summarize(DateTime month, string? app = null)
    {
        var records = Read(month);
        if (app is not null)
            records = records.Where(r => string.Equals(r.App, app, StringComparison.OrdinalIgnoreCase)).ToList();

        var totals = new UsageGroup { Key = $"{month:yyyy-MM}" };
        var byApp   = new Dictionary<string, UsageGroup>(StringComparer.OrdinalIgnoreCase);
        var byModel = new Dictionary<string, UsageGroup>(StringComparer.OrdinalIgnoreCase);
        var byDay   = new SortedDictionary<string, UsageGroup>(StringComparer.Ordinal);

        foreach (var r in records)
        {
            totals.Add(r);
            Group(byApp,   r.App.Length == 0 ? "-" : r.App).Add(r);
            Group(byModel, $"{r.Provider}/{r.Model}").Add(r);
            Group(byDay,   r.T.ToLocalTime().ToString("yyyy-MM-dd")).Add(r);
        }

        return new UsageSummary
        {
            Month   = $"{month:yyyy-MM}",
            App     = app,
            Totals  = totals,
            ByApp   = byApp.Values.OrderByDescending(g => g.CostUsd).ThenByDescending(g => g.Prompts).ToList(),
            ByModel = byModel.Values.OrderByDescending(g => g.CostUsd).ThenByDescending(g => g.Prompts).ToList(),
            ByDay   = byDay.Values.ToList()
        };

        static UsageGroup Group(IDictionary<string, UsageGroup> d, string key)
        {
            if (!d.TryGetValue(key, out var g)) d[key] = g = new UsageGroup { Key = key };
            return g;
        }
    }

    /// <summary>The most recent calls (this month and last), newest first.</summary>
    public IReadOnlyList<UsageRecord> Recent(int count, string? app = null)
    {
        var now = DateTime.Now;
        var records = Read(now).Concat(Read(now.AddMonths(-1)));
        if (app is not null)
            records = records.Where(r => string.Equals(r.App, app, StringComparison.OrdinalIgnoreCase));
        return records.OrderByDescending(r => r.T).Take(count).ToList();
    }

    /// <summary>
    /// The months that have a ledger file, newest first, as "yyyy-MM" — always including the
    /// current month, so a dashboard has something to select before the first call.
    /// </summary>
    public IReadOnlyList<string> Months()
    {
        var months = new SortedSet<string>(StringComparer.Ordinal) { DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture) };
        var dir = AiPaths.UsageDir(Root);
        try
        {
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "usage-????-??.jsonl"))
                {
                    var m = Path.GetFileNameWithoutExtension(f)["usage-".Length..];
                    if (DateTime.TryParseExact(m + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        months.Add(m);
                }
        }
        catch (IOException ex) { ActivityLog.Error("UsageLedger", $"Could not list {dir}", ex); }
        catch (UnauthorizedAccessException ex) { ActivityLog.Error("UsageLedger", $"Could not list {dir}", ex); }
        return months.Reverse().ToList();
    }

    /// <summary>The CSV header written by <see cref="ToCsv"/>.</summary>
    public const string CsvHeader = "Time,App,Provider,Model,Purpose,InputTokens,OutputTokens,EstimatedCostUsd,Milliseconds,Streamed,Result";

    /// <summary>
    /// Records as CSV (header first, local times, invariant numbers, RFC 4180 quoting) — what
    /// the dashboards' "Copy as CSV" puts on the clipboard.
    /// </summary>
    public static string ToCsv(IEnumerable<UsageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var sb = new StringBuilder(CsvHeader).Append('\n');
        foreach (var r in records)
        {
            sb.Append(r.T.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(r.App)).Append(',').Append(r.Provider).Append(',').Append(Csv(r.Model)).Append(',').Append(Csv(r.Purpose)).Append(',')
              .Append(r.InTok.ToString(CultureInfo.InvariantCulture)).Append(',').Append(r.OutTok.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.CostUsd?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(r.Ms.ToString(CultureInfo.InvariantCulture)).Append(',').Append(r.Streamed ? "yes" : "no").Append(',')
              .Append(Csv(r.Cancelled ? "cancelled" : r.Ok ? "ok" : r.Error ?? "failed")).Append('\n');
        }
        return sb.ToString();

        static string Csv(string? s)
        {
            s ??= string.Empty;
            return s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }

    /// <summary>Month-to-date estimated spend across every app.</summary>
    public double SpentThisMonth() => Summarize(DateTime.Now).Totals.CostUsd;
}
