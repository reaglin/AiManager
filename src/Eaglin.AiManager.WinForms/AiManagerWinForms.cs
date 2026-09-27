namespace Eaglin.AiManager.WinForms;

/// <summary>
/// The one-line way for a WinForms app to open the AI Manager windows — the counterpart of the
/// window classes in <c>Eaglin.AiManager.Wpf</c>.
/// <code>
/// // AI Settings… menu item
/// if (AiManagerWinForms.ShowSettings(this, "PreseMaker")) RefreshAiMenu();
/// // AI Usage… menu item
/// AiManagerWinForms.ShowUsage(this, "PreseMaker");
/// </code>
/// An app that already holds an <see cref="AiHub"/> passes it instead of the name, so the hub
/// it uses sees the saved keys at once.
/// </summary>
public static class AiManagerWinForms
{
    /// <summary>Opens AI Settings as a dialog. Returns true when the user saved.</summary>
    /// <param name="owner">The window to centre on and block; null for none.</param>
    /// <param name="appName">The calling app's name, as passed to <see cref="AiHub.Open(string, string?)"/>.</param>
    public static bool ShowSettings(IWin32Window? owner, string appName) =>
        ShowSettings(owner, AiHub.Open(appName));

    /// <summary>Opens AI Settings over an existing hub. On Save the hub holds the new keys; on Cancel it is re-read from disk.</summary>
    public static bool ShowSettings(IWin32Window? owner, AiHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        using var form = new AiSettingsForm(hub);
        return form.ShowDialog(owner) == DialogResult.OK;
    }

    /// <summary>Opens AI Usage as a dialog.</summary>
    /// <param name="owner">The window to centre on and block; null for none.</param>
    /// <param name="appName">The calling app's name.</param>
    /// <param name="onlyThisApp">Start filtered to this app's calls (the user can switch to all programs).</param>
    public static void ShowUsage(IWin32Window? owner, string appName, bool onlyThisApp = false) =>
        ShowUsage(owner, AiHub.Open(appName), onlyThisApp);

    /// <summary>Opens AI Usage over an existing hub.</summary>
    public static void ShowUsage(IWin32Window? owner, AiHub hub, bool onlyThisApp = false)
    {
        ArgumentNullException.ThrowIfNull(hub);
        using var form = new UsageDashboardForm(hub, onlyThisApp ? hub.App : null);
        form.ShowDialog(owner);
    }
}
