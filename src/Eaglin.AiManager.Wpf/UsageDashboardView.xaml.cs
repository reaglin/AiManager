using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Eaglin.AiManager.Usage;

namespace Eaglin.AiManager.Wpf;

/// <summary>
/// The AI dashboard: for a chosen month (and optionally one app) the totals, the breakdown by
/// app, by provider/model and by day, and the most recent calls — all read from the usage
/// ledger. "Copy as CSV" puts the month's calls on the clipboard.
/// </summary>
public partial class UsageDashboardView : UserControl
{
    private const string AllApps = "(all apps)";
    private readonly AiHub _hub;
    private bool _loading;

    private sealed record DayRow(string Day, int Prompts, double CostUsd, double BarWidth);

    private sealed record RecentRow(string When, string App, string Provider, string Model, string Purpose, int InTok, int OutTok, string Cost, string Time, string Result);

    /// <param name="hub">Any hub; the ledger is shared by every app.</param>
    /// <param name="appFilter">Preselect one app (e.g. the calling app), or null for all.</param>
    public UsageDashboardView(AiHub hub, string? appFilter = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
        InitializeComponent();
        LoadFilters(appFilter);
        Refresh();
    }

    private void LoadFilters(string? appFilter)
    {
        _loading = true;
        var months = new SortedSet<string>(StringComparer.Ordinal) { DateTime.Now.ToString("yyyy-MM") };
        var dir = AiPaths.UsageDir(_hub.Root);
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "usage-????-??.jsonl"))
                months.Add(Path.GetFileNameWithoutExtension(f)["usage-".Length..]);
        MonthBox.ItemsSource = months.Reverse().ToList();
        MonthBox.SelectedIndex = 0;

        var apps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in months)
            foreach (var r in _hub.Usage.Read(ParseMonth(m)))
                if (r.App.Length > 0) apps.Add(r.App);
        if (appFilter is not null) apps.Add(appFilter);
        var appItems = new List<string> { AllApps };
        appItems.AddRange(apps);
        AppBox.ItemsSource = appItems;
        AppBox.SelectedItem = appFilter is not null && appItems.Contains(appFilter, StringComparer.OrdinalIgnoreCase)
            ? appItems.First(a => string.Equals(a, appFilter, StringComparison.OrdinalIgnoreCase)) : AllApps;
        _loading = false;
    }

    private static DateTime ParseMonth(string m) =>
        DateTime.ParseExact(m + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private DateTime SelectedMonth => MonthBox.SelectedItem is string m ? ParseMonth(m) : DateTime.Now;
    private string? SelectedApp => AppBox.SelectedItem is string a && a != AllApps ? a : null;

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var app = SelectedApp;
        LoadFilters(app);
        Refresh();
    }

    public void Refresh()
    {
        _hub.Reload();
        var s = _hub.Usage.Summarize(SelectedMonth, SelectedApp);
        var t = s.Totals;

        TilePrompts.Text = t.Prompts.ToString("N0", CultureInfo.CurrentCulture);
        TileIn.Text      = t.InputTokens.ToString("N0", CultureInfo.CurrentCulture);
        TileOut.Text     = t.OutputTokens.ToString("N0", CultureInfo.CurrentCulture);
        TileCost.Text    = t.CostUsd.ToString("$0.00", CultureInfo.InvariantCulture);
        TileErrors.Text  = $"{t.Failed} / {t.Cancelled}";
        TileAvg.Text     = t.Prompts == 0 ? "–" : t.AverageMs >= 1000 ? $"{t.AverageMs / 1000:0.0} s" : $"{t.AverageMs:0} ms";

        ByAppGrid.ItemsSource   = s.ByApp;
        ByModelGrid.ItemsSource = s.ByModel;
        var maxCost = s.ByDay.Count == 0 ? 0 : s.ByDay.Max(g => g.CostUsd);
        ByDayGrid.ItemsSource = s.ByDay.Select(g => new DayRow(g.Key[5..], g.Prompts, g.CostUsd, maxCost <= 0 ? 0 : 120 * g.CostUsd / maxCost)).ToList();

        var records = _hub.Usage.Read(SelectedMonth).Where(r => SelectedApp is null || string.Equals(r.App, SelectedApp, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.T).Take(200);
        RecentGrid.ItemsSource = records.Select(r => new RecentRow(
            r.T.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), r.App, r.Provider.ToString(), r.Model, r.Purpose,
            r.InTok, r.OutTok, r.CostUsd is { } c ? c.ToString("$0.0000", CultureInfo.InvariantCulture) : "?",
            r.Ms >= 1000 ? $"{r.Ms / 1000.0:0.0} s" : $"{r.Ms} ms",
            r.Cancelled ? "cancelled" : r.Ok ? "ok" : r.Error ?? "failed")).ToList();

        if (_hub.Settings.MonthlyBudgetUsd is { } budget && budget > 0 && SelectedMonth.ToString("yyyy-MM") == DateTime.Now.ToString("yyyy-MM"))
        {
            var spent = _hub.Usage.SpentThisMonth();
            var over = spent > budget;
            BudgetText.Text = $"Budget this month: ${budget:0.00} · spent ${spent:0.00} (estimated, all apps){(over ? "  — over budget" : "")}";
            BudgetText.Foreground = (Brush)FindResource(over ? "Ai.Bad" : "Ai.Muted");
            BudgetText.Visibility = Visibility.Visible;
        }
        else BudgetText.Visibility = Visibility.Collapsed;

        SubText.Text = SelectedApp is null
            ? "Every call made through the AI Manager, by every app. Costs are estimates from list prices."
            : $"Calls made by {SelectedApp}. Costs are estimates from list prices.";
    }

    private void Csv_Click(object sender, RoutedEventArgs e)
    {
        var records = _hub.Usage.Read(SelectedMonth).Where(r => SelectedApp is null || string.Equals(r.App, SelectedApp, StringComparison.OrdinalIgnoreCase));
        var sb = new StringBuilder("Time,App,Provider,Model,Purpose,InputTokens,OutputTokens,EstimatedCostUsd,Milliseconds,Streamed,Result\n");
        foreach (var r in records)
        {
            sb.Append(r.T.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(r.App)).Append(',').Append(r.Provider).Append(',').Append(Csv(r.Model)).Append(',').Append(Csv(r.Purpose)).Append(',')
              .Append(r.InTok).Append(',').Append(r.OutTok).Append(',')
              .Append(r.CostUsd?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(r.Ms).Append(',').Append(r.Streamed ? "yes" : "no").Append(',')
              .Append(r.Cancelled ? "cancelled" : r.Ok ? "ok" : r.Error ?? "failed").Append('\n');
        }
        try
        {
            Clipboard.SetText(sb.ToString());
            SubText.Text = $"Copied {records.Count()} rows as CSV.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not copy: {ex.Message}", "AI Usage", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
