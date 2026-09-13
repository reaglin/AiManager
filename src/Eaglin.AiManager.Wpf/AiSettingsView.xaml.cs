using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Eaglin.AiManager.Legacy;

namespace Eaglin.AiManager.Wpf;

/// <summary>
/// The AI Settings surface: one row per provider (key, model, test, catalog refresh), the
/// default provider (global and, when opened from an app, for that app), the monthly budget,
/// and legacy key import. Edits live in memory until Save; Revert re-reads the store.
/// </summary>
public partial class AiSettingsView : UserControl
{
    private const string UseGlobal = "(use the global default)";

    private readonly AiHub _hub;
    private readonly List<Row> _rows = new();

    /// <summary>Raised after a successful Save (a hosting window may close on it).</summary>
    public event EventHandler? Saved;

    private sealed class Row
    {
        public required AiProviderType Provider;
        public required Ellipse Dot;
        public required PasswordBox Key;
        public required TextBox KeyPlain;
        public required ToggleButton Show;
        public required ComboBox Model;
        public required Button Refresh;
        public required Button Test;
        public required TextBlock Status;

        public string KeyText => Show.IsChecked == true ? KeyPlain.Text.Trim() : Key.Password.Trim();
        public void SetKey(string key) { Key.Password = key; KeyPlain.Text = key; }
    }

    public AiSettingsView(AiHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
        InitializeComponent();
        BuildRows();
        Load();
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private void BuildRows()
    {
        int r = 0;
        foreach (var p in AiProviderRouter.AllProviders)
        {
            ProviderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var dot = new Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = AiHub.DisplayName(p), VerticalAlignment = VerticalAlignment.Center };
            var key = new PasswordBox { Margin = new Thickness(0, 3, 0, 3), VerticalContentAlignment = VerticalAlignment.Center };
            var keyPlain = new TextBox { Margin = new Thickness(0, 3, 0, 3), Visibility = Visibility.Collapsed, VerticalContentAlignment = VerticalAlignment.Center };
            var show = new ToggleButton { Content = "Show", Margin = new Thickness(6, 0, 10, 0), Padding = new Thickness(6, 1, 6, 1) };
            var model = new ComboBox { IsEditable = true, Margin = new Thickness(0, 3, 0, 3), ToolTip = "Model id. Pick from the list or type any id the vendor accepts." };
            var refresh = new Button { Content = "Models", ToolTip = "Fetch the vendor's current model list into the drop-down (uses the key above).", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(4, 0, 0, 0) };
            var test = new Button { Content = "Test", ToolTip = "Send a one-word prompt with this key and model.", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 0, 8, 0) };
            var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)FindResource("Ai.Muted") };

            var row = new Row { Provider = p, Dot = dot, Key = key, KeyPlain = keyPlain, Show = show, Model = model, Refresh = refresh, Test = test, Status = status };
            _rows.Add(row);

            show.Checked   += (_, _) => { keyPlain.Text = key.Password; key.Visibility = Visibility.Collapsed; keyPlain.Visibility = Visibility.Visible; };
            show.Unchecked += (_, _) => { key.Password = keyPlain.Text; keyPlain.Visibility = Visibility.Collapsed; key.Visibility = Visibility.Visible; };
            key.PasswordChanged += (_, _) => UpdateDot(row);
            keyPlain.TextChanged += (_, _) => UpdateDot(row);
            refresh.Click += async (_, _) => await RefreshModelsAsync(row);
            test.Click    += async (_, _) => await TestAsync(row);

            Place(dot, r, 0); Place(name, r, 1); Place(key, r, 2); Place(keyPlain, r, 2); Place(show, r, 3);
            Place(model, r, 4); Place(refresh, r, 5); Place(test, r, 6); Place(status, r, 7);
            r++;
        }

        void Place(UIElement e, int row, int col)
        {
            Grid.SetRow(e, row); Grid.SetColumn(e, col);
            ProviderGrid.Children.Add(e);
        }
    }

    // ── Load / Save ───────────────────────────────────────────────────────────

    private void Load()
    {
        _hub.Reload();
        foreach (var row in _rows)
        {
            row.SetKey(_hub.Credentials.KeyFor(row.Provider));
            FillModels(row, _hub.Models.Cached(row.Provider)?.Models.Select(m => m.Id));
            row.Model.Text = _hub.Settings.ModelFor(row.Provider) ?? AiProviderRouter.DefaultModels[row.Provider];
            row.Status.Text = _hub.Credentials.EntryFor(row.Provider) is { } e
                ? $"saved {e.UpdatedUtc:yyyy-MM-dd} by {e.UpdatedBy}" : "no key";
            UpdateDot(row);
        }

        DefaultProviderBox.ItemsSource = AiProviderRouter.AllProviders.Select(p => new ProviderItem(p)).ToList();
        DefaultProviderBox.SelectedItem = ((List<ProviderItem>)DefaultProviderBox.ItemsSource).First(i => i.Provider == _hub.Settings.DefaultProvider);

        var appItems = new List<object> { UseGlobal };
        appItems.AddRange(AiProviderRouter.AllProviders.Select(p => new ProviderItem(p)));
        AppDefaultProviderBox.ItemsSource = appItems;
        var appDefault = _hub.Settings.Apps.TryGetValue(_hub.App, out var a) ? a.DefaultProvider : null;
        AppDefaultProviderBox.SelectedItem = appDefault is { } ad && ad != AiProviderType.None
            ? appItems.OfType<ProviderItem>().First(i => i.Provider == ad) : UseGlobal;
        AppDefaultLabel.Text = $"Default provider for {_hub.App}";
        AppDefaultHint.Text  = $"Only {_hub.App} is affected; other apps keep the global default.";

        BudgetBox.Text = _hub.Settings.MonthlyBudgetUsd is { } b ? b.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
        var spent = _hub.Usage.SpentThisMonth();
        PathText.Text  = $"Spent this month, all apps: ${spent:0.00} (estimated from list prices)   ·   Store: {_hub.Root}";
        FooterStatus.Text = string.Empty;
    }

    private void FillModels(Row row, IEnumerable<string>? fetched)
    {
        var fallback = AiProviderRouter.FallbackModels[row.Provider];
        var ids = fallback.Concat(fetched ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var current = row.Model.Text;
        row.Model.ItemsSource = ids;
        if (!string.IsNullOrWhiteSpace(current)) row.Model.Text = current;
    }

    private void UpdateDot(Row row)
    {
        row.Dot.Fill = (Brush)FindResource(row.KeyText.Length > 0 ? "Ai.Ok" : "Ai.Off");
        row.Dot.ToolTip = row.KeyText.Length > 0 ? "A key is present (not yet tested)" : "No key";
    }

    /// <summary>Pushes the typed keys into the hub's in-memory store so Test and Refresh use them. Nothing is written.</summary>
    private void ApplyKeysInMemory()
    {
        foreach (var row in _rows)
        {
            var typed = row.KeyText;
            if (typed != _hub.Credentials.KeyFor(row.Provider))
                _hub.Credentials.SetKey(row.Provider, typed, _hub.App);
        }
    }

    private bool TryReadBudget(out double? budget)
    {
        budget = null;
        var text = BudgetBox.Text.Trim().TrimStart('$');
        if (text.Length == 0) return true;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0) { budget = v == 0 ? null : v; return true; }
        MessageBox.Show("The monthly budget must be a number of dollars, or blank.", "AI Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadBudget(out var budget)) return;

        ApplyKeysInMemory();
        foreach (var row in _rows)
            _hub.Settings.SetModel(row.Provider, row.Model.Text);

        if (DefaultProviderBox.SelectedItem is ProviderItem gp) _hub.Settings.DefaultProvider = gp.Provider;
        var app = _hub.Settings.ForApp(_hub.App);
        app.DefaultProvider = AppDefaultProviderBox.SelectedItem is ProviderItem ap ? ap.Provider : null;
        if (app.DefaultProvider is null && app.Models.Count == 0) _hub.Settings.Apps.Remove(_hub.App);
        _hub.Settings.MonthlyBudgetUsd = budget;

        try
        {
            _hub.Credentials.Save();
            _hub.SaveSettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save: {ex.Message}", "AI Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        FooterStatus.Text = $"Saved {DateTime.Now:HH:mm:ss}.";
        Load();
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        Load();
        FooterStatus.Text = "Reverted to what is on disk.";
    }

    // ── Per-row actions ───────────────────────────────────────────────────────

    private async Task TestAsync(Row row)
    {
        ApplyKeysInMemory();
        if (row.KeyText.Length == 0) { row.Status.Text = "Enter a key first."; return; }

        row.Test.IsEnabled = false;
        row.Status.Text = "Testing…";
        row.Status.Foreground = (Brush)FindResource("Ai.Muted");
        try
        {
            var r = await _hub.CompleteAsync(new AiRequest
            {
                Provider  = row.Provider,
                Model     = row.Model.Text,
                System    = "You are a connection test. Reply with exactly the word OK and nothing else.",
                User      = "Connection test.",
                MaxTokens = 16,
                Purpose   = "connection-test"
            });
            row.Status.Text = $"OK · {r.Elapsed.TotalSeconds:0.0}s · {r.Model}";
            row.Status.Foreground = (Brush)FindResource("Ai.Ok");
            row.Dot.Fill = (Brush)FindResource("Ai.Ok");
            row.Dot.ToolTip = "Tested OK";
        }
        catch (AiException ex)
        {
            row.Status.Text = $"{ex.Kind}: {ex.Message}";
            row.Status.ToolTip = ex.Message;
            row.Status.Foreground = (Brush)FindResource("Ai.Bad");
            row.Dot.Fill = (Brush)FindResource("Ai.Bad");
            row.Dot.ToolTip = ex.Message;
        }
        finally { row.Test.IsEnabled = true; }
    }

    private async Task RefreshModelsAsync(Row row)
    {
        ApplyKeysInMemory();
        row.Refresh.IsEnabled = false;
        row.Status.Text = "Fetching models…";
        row.Status.Foreground = (Brush)FindResource("Ai.Muted");
        try
        {
            var list = await _hub.Models.ListAsync(row.Provider, refresh: true);
            FillModels(row, list.Models.Select(m => m.Id));
            row.Status.Text = $"{list.Models.Count} models listed";
        }
        catch (AiException ex)
        {
            row.Status.Text = $"{ex.Kind}: {ex.Message}";
            row.Status.ToolTip = ex.Message;
            row.Status.Foreground = (Brush)FindResource("Ai.Bad");
        }
        finally { row.Refresh.IsEnabled = true; }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var found = LegacyCredentials.Discover();
        if (found.Count == 0)
        {
            MessageBox.Show("No keys saved by PreseMaker, CIATLE or AuthorPlus were found on this computer.",
                "Import keys", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lines = found.Select(s => $"• {s.App}: {string.Join(", ", s.Providers.Select(AiHub.DisplayName))}\n    {s.Path}");
        var answer = MessageBox.Show(
            "Keys were found in:\n\n" + string.Join("\n", lines) +
            "\n\nCopy them into the shared store? Providers that already have a key here are left as they are. " +
            "The original files are not changed. Click Save afterwards to keep the result.",
            "Import keys", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        ApplyKeysInMemory();
        int n = 0;
        foreach (var s in found) n += LegacyCredentials.Import(_hub.Credentials, s);
        foreach (var row in _rows)
        {
            row.SetKey(_hub.Credentials.KeyFor(row.Provider));
            UpdateDot(row);
        }
        FooterStatus.Text = n == 0 ? "Nothing to import: every provider found already has a key." : $"Imported {n} key(s) — click Save to keep them.";
    }

    private sealed record ProviderItem(AiProviderType Provider)
    {
        public override string ToString() => AiHub.DisplayName(Provider);
    }
}
