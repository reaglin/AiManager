using System.Diagnostics;

namespace Eaglin.AiManager.WinForms;

/// <summary>
/// The base of both forms: laid out at the 96-DPI baseline in the constructor, scaled in
/// <see cref="OnLoad"/> — the same approach as CIATLE's <c>DpiAwareForm</c>, and for the same
/// reason. WinForms' <see cref="AutoScaleMode.Dpi"/> only scales controls that exist when the
/// scale baseline is established; setting it before the constructor has added its controls
/// scales an empty form. So the baseline is set here, after every control exists, and
/// <see cref="Form.PerformAutoScale"/> scales the whole tree. Under PerMonitorV2 the form then
/// rescales itself when it is dragged to a monitor with a different DPI (AutoScaleMode.Dpi).
///
/// Rules the forms keep so nothing is scaled twice or not at all:
/// - pixel sizes in the constructor are 96-DPI numbers; nothing there is computed from font
///   metrics (the font already renders at the monitor's DPI, so a size taken from it and then
///   auto-scaled would be scaled twice) — font-derived sizes go in <see cref="AfterScale"/>;
/// - DataGridView columns are never fixed-width (auto-scaling does not scale them): they size
///   to content or fill; row and header heights are set from the font in AfterScale;
/// - a ComboBox is never Dock=Fill in a table cell (its height is fixed by its font; it is
///   anchored left-right instead) and its drop-down width is set in AfterScale.
/// </summary>
public class ScaledForm : Form
{
    private bool _scaled;

    public ScaledForm()
    {
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.None;   // until OnLoad; see the class summary
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        ShowInTaskbar = false;
    }

    protected override void OnLoad(EventArgs e)
    {
        if (!_scaled)
        {
            _scaled = true;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            PerformAutoScale();
            AfterScale();
            KeepOnScreen();
        }
        base.OnLoad(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (_scaled) BeginInvoke(AfterScale);
    }

    /// <summary>Sizes that come from the font (row heights, wrap widths). Runs after scaling and after every DPI change.</summary>
    protected virtual void AfterScale() { }

    /// <summary>A scaled-up window may be taller than a small screen: fit it to the working area.</summary>
    private void KeepOnScreen()
    {
        var area = (Owner is not null ? Screen.FromControl(Owner) : Screen.FromPoint(Cursor.Position)).WorkingArea;
        if (Width > area.Width) Width = area.Width;
        if (Height > area.Height) Height = area.Height;
        if (StartPosition == FormStartPosition.CenterParent && Owner is null)
            StartPosition = FormStartPosition.CenterScreen;
    }

    /// <summary>Opens a web page in the user's browser; says so in words if it cannot.</summary>
    internal void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(this, $"Could not open your web browser ({ex.Message}).\n\nType this address into a browser instead:\n{url}",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
