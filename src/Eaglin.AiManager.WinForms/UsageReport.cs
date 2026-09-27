using System.Globalization;
using Eaglin.AiManager.Usage;

namespace Eaglin.AiManager.WinForms;

/// <summary>One row of a breakdown table (by app, by model, by day), already formatted.</summary>
internal sealed record UsageRow(string Name, string Prompts, string TokensIn, string TokensOut, string Cost, double CostUsd);

/// <summary>One recent call, already formatted.</summary>
internal sealed record CallRow(string When, string App, string Provider, string Model, string Purpose, string TokensIn, string TokensOut, string Cost, string Time, string Result);

/// <summary>
/// The usage form's numbers for one month (and optionally one app), read from the ledger and
/// formatted for display — kept apart from the form so the figures can be tested without a window.
/// </summary>
internal sealed class UsageReport
{
    public const string AllApps = "All programs";

    public required string Month { get; init; }
    public string? App { get; init; }
    public required UsageGroup Totals { get; init; }
    public required IReadOnlyList<UsageRow> ByApp { get; init; }
    public required IReadOnlyList<UsageRow> ByModel { get; init; }
    public required IReadOnlyList<UsageRow> ByDay { get; init; }
    public required IReadOnlyList<CallRow> Recent { get; init; }
    public required IReadOnlyList<UsageRecord> Records { get; init; }
    /// <summary>Null when no budget is set or the month is not the current one.</summary>
    public string? BudgetLine { get; init; }
    public bool OverBudget { get; init; }

    public static DateTime ParseMonth(string month) =>
        DateTime.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"September 2026" for "2026-09".</summary>
    public static string MonthLabel(string month) =>
        ParseMonth(month).ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>Every app that appears in any month's ledger, plus the calling app, sorted.</summary>
    public static IReadOnlyList<string> Apps(UsageLedger ledger, string? include = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var apps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ledger.Months())
            foreach (var r in ledger.Read(ParseMonth(m)))
                if (r.App.Length > 0) apps.Add(r.App);
        if (!string.IsNullOrWhiteSpace(include)) apps.Add(include);
        return apps.ToList();
    }

    public static UsageReport Build(AiHub hub, string month, string? app, int recentCount = 200)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var when = ParseMonth(month);
        var s = hub.Usage.Summarize(when, app);
        var records = hub.Usage.Read(when)
            .Where(r => app is null || string.Equals(r.App, app, StringComparison.OrdinalIgnoreCase)).ToList();

        string? budgetLine = null;
        var over = false;
        if (hub.Settings.MonthlyBudgetUsd is { } budget && budget > 0 &&
            month == DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture))
        {
            var spent = hub.Usage.SpentThisMonth();
            over = spent > budget;
            budgetLine = $"Spending warning set at {Money(budget, 2)} a month. Spent so far this month, all programs: {Money(spent, 2)} (estimate)."
                       + (over ? " This is over the warning amount." : "");
        }

        return new UsageReport
        {
            Month   = month,
            App     = app,
            Totals  = s.Totals,
            ByApp   = s.ByApp.Select(g => Row(g.Key, g)).ToList(),
            ByModel = s.ByModel.Select(g => Row(ModelName(g.Key), g)).ToList(),
            ByDay   = s.ByDay.Select(g => Row(DayName(g.Key), g)).ToList(),
            Recent  = records.OrderByDescending(r => r.T).Take(recentCount).Select(Call).ToList(),
            Records = records,
            BudgetLine = budgetLine,
            OverBudget = over
        };
    }

    public string PromptsText  => Number(Totals.Prompts);
    public string TokensInText => Number(Totals.InputTokens);
    public string TokensOutText => Number(Totals.OutputTokens);
    /// <summary>Two decimals, or four for a total under a cent, so a light month does not read "$0.00" (free).</summary>
    public string CostText     => Money(Totals.CostUsd, Totals.CostUsd is > 0 and < 0.01 ? 4 : 2);
    public string ProblemsText => $"{Number(Totals.Failed)} failed, {Number(Totals.Cancelled)} stopped";
    public string AverageText  => Totals.Prompts == 0 ? "–" : Duration(Totals.AverageMs);

    public string Csv() => UsageLedger.ToCsv(Records);

    // ── Formatting ────────────────────────────────────────────────────────────

    internal static string Number(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Dollars, always with a $ (the prices are US list prices whatever the user's locale).</summary>
    internal static string Money(double usd, int decimals) =>
        "$" + usd.ToString("0." + new string('0', decimals), CultureInfo.InvariantCulture);

    internal static string Duration(double ms) =>
        ms >= 1000 ? (ms / 1000).ToString("0.0", CultureInfo.CurrentCulture) + " s" : ms.ToString("0", CultureInfo.CurrentCulture) + " ms";

    /// <summary>"Claude/claude-opus-5" → "Claude (Anthropic) · claude-opus-5".</summary>
    internal static string ModelName(string key)
    {
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0) return key;
        var provider = key[..slash];
        var model = key[(slash + 1)..];
        return Enum.TryParse<AiProviderType>(provider, out var p) ? $"{AiHub.DisplayName(p)} · {model}" : key;
    }

    /// <summary>"2026-09-14" → "Mon 14 Sep".</summary>
    internal static string DayName(string key) =>
        DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("ddd d MMM", CultureInfo.CurrentCulture) : key;

    private static UsageRow Row(string name, UsageGroup g) =>
        new(name, Number(g.Prompts), Number(g.InputTokens), Number(g.OutputTokens), Money(g.CostUsd, 4), g.CostUsd);

    private static CallRow Call(UsageRecord r) => new(
        r.T.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture),
        r.App,
        AiHub.DisplayName(r.Provider),
        r.Model,
        PurposeWords(r.Purpose),
        Number(r.InTok),
        Number(r.OutTok),
        r.CostUsd is { } c ? Money(c, 4) : "unknown",
        Duration(r.Ms),
        r.Cancelled ? "stopped by the user" : r.Ok ? "ok" : "failed: " + ErrorWords(r.Error));

    /// <summary>What a request was for, in words: the program's own purpose name, or ours for the Test button.</summary>
    internal static string PurposeWords(string? purpose) => purpose switch
    {
        null or "" or "(unnamed)" => "(not named by the program)",
        "connection-test" => "Test button in AI Settings",
        _ => purpose
    };

    /// <summary>The ledger's error kind (an <see cref="AiErrorKind"/> name) as a short reason.</summary>
    internal static string ErrorWords(string? error) => Enum.TryParse<AiErrorKind>(error, out var kind)
        ? kind switch
        {
            AiErrorKind.NoKey         => "no key",
            AiErrorKind.Auth          => "key not accepted",
            AiErrorKind.RateLimit     => "over the limit or out of credit",
            AiErrorKind.Network       => "no connection",
            AiErrorKind.Timeout       => "took too long",
            AiErrorKind.Refused       => "the AI declined",
            AiErrorKind.EmptyResponse => "empty answer",
            _                         => "provider error"
        }
        : string.IsNullOrWhiteSpace(error) ? "reason not recorded" : error;
}
