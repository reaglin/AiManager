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
        Title = "AI Settings";
        Content = View;
        Width = 900; Height = 520;
        MinWidth = 800; MinHeight = 460;
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
