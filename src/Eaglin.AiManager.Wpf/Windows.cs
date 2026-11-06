using System.Windows;

namespace Eaglin.AiManager.Wpf;

/// <summary>AI Settings as a dialog. Closes on Save. <c>new AiSettingsWindow(hub) { Owner = this }.ShowDialog();</c></summary>
public sealed class AiSettingsWindow : Window
{
    public AiSettingsView View { get; }

    public AiSettingsWindow(AiHub hub)
    {
        View = new AiSettingsView(hub);
        View.Saved += (_, _) => { DialogResult = true; Close(); };
        Title = hub.IsOpenedForAnApp ? $"AI Settings — {hub.App}" : "AI Settings";
        Content = View;
        Width = hub.IsOpenedForAnApp ? 1100 : 900; Height = 680;
        MinWidth = hub.IsOpenedForAnApp ? 1000 : 800; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
    }
}

/// <summary>The app's prompt templates as a window.</summary>
public sealed class PromptLibraryWindow : Window
{
    public PromptLibraryView View { get; }

    public PromptLibraryWindow(AiHub hub, bool allowAppSwitch = false)
    {
        View = new PromptLibraryView(hub, allowAppSwitch);
        Title = "Prompt Library";
        Content = View;
        Width = 920; Height = 640;
        MinWidth = 800; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }
}

/// <summary>The usage dashboard as a window.</summary>
public sealed class UsageDashboardWindow : Window
{
    public UsageDashboardView View { get; }

    public UsageDashboardWindow(AiHub hub, string? appFilter = null)
    {
        View = new UsageDashboardView(hub, appFilter);
        Title = "AI Usage";
        Content = View;
        Width = 1000; Height = 680;
        MinWidth = 860; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }
}

/// <summary>
/// How to get one provider's key: the steps and what to choose in its form (from
/// <see cref="Help.KeyHelp"/>), with buttons to the provider's key page and the full help page.
/// </summary>
public sealed class KeyHelpWindow : Window
{
    public KeyHelpWindow(AiHub hub, AiProviderType provider)
    {
        var h = Help.KeyHelp.For(provider);
        Title = $"How to get a {h.Name} key";
        Width = 620; Height = 560; MinWidth = 420; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var text = new System.Windows.Controls.TextBox
        {
            Text = Help.KeyHelp.ShortText(provider), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 12)
        };
        System.Windows.Automation.AutomationProperties.SetName(text, Title);
        System.Windows.Controls.Button Make(string caption, string tip, Action click)
        {
            var b = new System.Windows.Controls.Button { Content = caption, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
            b.Click += (_, _) => click();
            return b;
        }
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Make("Open the key page", $"Opens {h.KeyPage} in your web browser.", () => KeyHelpLinks.Open(this, h.KeyPage)));
        buttons.Children.Add(Make("Full help for every provider", "Opens the dated help page for every provider in your web browser.", () => KeyHelpLinks.OpenFullHelp(this, hub.Root)));
        var close = Make("Close", "Closes this window.", Close);
        close.IsCancel = true;
        buttons.Children.Add(close);

        var dock = new System.Windows.Controls.DockPanel { Margin = new Thickness(16) };
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(text);
        Content = dock;
    }
}

/// <summary>Opening the key pages and the help page from the WPF windows; says so in words when it cannot.</summary>
internal static class KeyHelpLinks
{
    public static void Open(Window? owner, string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Say(owner, $"Could not open your web browser ({ex.Message}).\n\nType this address into a browser instead:\n{url}",
                MessageBoxImage.Information);
        }
    }

    public static void OpenFullHelp(Window? owner, string root)
    {
        string path;
        try { path = Help.KeyHelp.WriteHtml(root); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Say(owner, $"The help page could not be written to {root}.\n\n{ex.Message}\n\nHow? beside each provider shows the same steps.",
                MessageBoxImage.Warning);
            return;
        }
        Open(owner, path);
    }

    private static void Say(Window? owner, string text, MessageBoxImage image)
    {
        if (owner is null) MessageBox.Show(text, "AI Settings", MessageBoxButton.OK, image);
        else MessageBox.Show(owner, text, "AI Settings", MessageBoxButton.OK, image);
    }
}

/// <summary>How to get a Google text-to-speech key (1.8.0): the steps from <see cref="Help.VoiceKeyHelp"/>, with buttons to the console and the full help page.</summary>
public sealed class VoiceKeyHelpWindow : Window
{
    public VoiceKeyHelpWindow(AiHub hub)
    {
        Title = "How to get a Google text-to-speech key";
        Width = 660; Height = 600; MinWidth = 440; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var text = new System.Windows.Controls.TextBox
        {
            Text = Help.VoiceKeyHelp.ShortText(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 12)
        };
        System.Windows.Automation.AutomationProperties.SetName(text, Title);
        System.Windows.Controls.Button Make(string caption, string tip, Action click)
        {
            var b = new System.Windows.Controls.Button { Content = caption, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
            b.Click += (_, _) => click();
            return b;
        }
        var buttons = new System.Windows.Controls.WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Make("Open Billing", $"Opens {Help.VoiceKeyHelp.BillingPage} in your web browser, where a billing account is linked to the project (step 3).", () => KeyHelpLinks.Open(this, Help.VoiceKeyHelp.BillingPage)));
        buttons.Children.Add(Make("Turn on the API", $"Opens {Help.VoiceKeyHelp.EnableApiPage} in your web browser (step 4).", () => KeyHelpLinks.Open(this, Help.VoiceKeyHelp.EnableApiPage)));
        buttons.Children.Add(Make("Open Credentials", $"Opens {Help.VoiceKeyHelp.CredentialsPage} in your web browser, where the key is made (step 5).", () => KeyHelpLinks.Open(this, Help.VoiceKeyHelp.CredentialsPage)));
        buttons.Children.Add(Make("Full help page", "Opens the dated help page for every key, voices included, in your web browser.", () => KeyHelpLinks.OpenFullHelp(this, hub.Root)));
        var close = Make("Close", "Closes this window.", Close);
        close.IsCancel = true;
        buttons.Children.Add(close);

        var dock = new System.Windows.Controls.DockPanel { Margin = new Thickness(16) };
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(text);
        Content = dock;
    }
}
