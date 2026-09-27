using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Eaglin.AiManager;
using Eaglin.AiManager.Prompts;
using Eaglin.AiManager.Wpf;

namespace AiManagerApp;

/// <summary>
/// Four tabs over one <see cref="AiHub"/> opened as "AiManager": Settings, Prompt Library (any
/// app's), Usage (every app's), and a "Try it" tab that streams a prompt through the
/// <see cref="AiRunPanel"/> so a key can be seen working end to end.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AiHub _hub;
    private readonly AiSettingsView _settings;
    private readonly PromptLibraryView _prompts;
    private readonly UsageDashboardView _usage;
    private readonly AiRunPanel _run = new();
    private ComboBox _tryProvider = null!;
    private TextBox _tryPrompt = null!;

    public MainWindow()
    {
        InitializeComponent();
        _hub = AiHub.Open(AiHub.ManagerAppName);
        _hub.Prompts.RegisterDefaults(new[]
        {
            new PromptTemplate("try-it", "You are a helpful assistant. Answer plainly.", "{text}", "The prompt behind the Try it tab.", 2000)
        });

        _settings = new AiSettingsView(_hub);
        _prompts  = new PromptLibraryView(_hub, allowAppSwitch: true);
        _usage    = new UsageDashboardView(_hub);
        SettingsTab.Content = _settings;
        PromptsTab.Content  = _prompts;
        UsageTab.Content    = _usage;
        TryTab.Content      = BuildTryTab();

        _settings.Saved += (_, _) => { StatusText.Text = "Settings saved."; FillTryProviders(); };
        _hub.BudgetExceeded += s => Dispatcher.Invoke(() =>
            MessageBox.Show($"Estimated AI spend this month is ${s.SpentUsd:0.00}, over the ${s.BudgetUsd:0.00} budget set in Settings.", "AI Manager", MessageBoxButton.OK, MessageBoxImage.Warning));

        StatusText.Text  = $"Store: {_hub.Root}";
        VersionText.Text = $"AI Manager {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
    }

    private UIElement BuildTryTab()
    {
        _run.Hub = _hub;
        _tryProvider = new ComboBox { Width = 200, Margin = new Thickness(0, 0, 8, 0) };
        _tryPrompt = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                   Text = "In three sentences, what makes a good opening chapter?" };
        var send = new Button { Content = "Send", Padding = new Thickness(14, 4, 14, 4), FontWeight = FontWeights.SemiBold };
        send.Click += async (_, _) => await SendAsync();
        FillTryProviders();

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(send, Dock.Right);
        top.Children.Add(send);
        top.Children.Add(new TextBlock { Text = "Provider:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        top.Children.Add(_tryProvider);
        top.Children.Add(new TextBlock { Text = "Streams the answer through the AiRunPanel control apps embed.", VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.Gray });

        var panel = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_tryPrompt, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(_tryPrompt);
        _run.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_run);
        return panel;
    }

    private void FillTryProviders()
    {
        var items = _hub.ProvidersWithKeys.Select(p => new ProviderItem(p)).ToList();
        _tryProvider.ItemsSource = items;
        _tryProvider.SelectedItem = items.FirstOrDefault(i => i.Provider == _hub.DefaultProvider) ?? items.FirstOrDefault();
        _tryProvider.IsEnabled = items.Count > 0;
    }

    private async Task SendAsync()
    {
        if (_run.IsRunning) return;
        if (_tryProvider.SelectedItem is not ProviderItem item)
        {
            MessageBox.Show("Add an API key on the Settings tab first.", "AI Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var req = _hub.Prompts.Get("try-it").Bind(new { text = _tryPrompt.Text }, item.Provider);
        await _run.RunAsync(req);
        _usage.Refresh();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return;
        if (Tabs.SelectedItem == UsageTab) _usage.Refresh();
    }

    private sealed record ProviderItem(AiProviderType Provider)
    {
        public override string ToString() => AiHub.DisplayName(Provider);
    }
}
