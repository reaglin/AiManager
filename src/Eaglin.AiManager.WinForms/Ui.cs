namespace Eaglin.AiManager.WinForms;

/// <summary>Colours, fonts and the few control shapes both forms use.</summary>
internal static class Ui
{
    public static readonly Color Text   = Color.FromArgb(32, 32, 32);
    public static readonly Color Muted  = Color.FromArgb(84, 84, 84);      // 7.4:1 on white
    public static readonly Color Ok     = Color.FromArgb(16, 124, 16);
    public static readonly Color Bad    = Color.FromArgb(180, 30, 30);
    public static readonly Color Off    = Color.FromArgb(150, 150, 150);
    public static readonly Color Accent = Color.FromArgb(0, 95, 184);
    public static readonly Color Band   = Color.FromArgb(243, 246, 250);
    public static readonly Color Line   = Color.FromArgb(214, 219, 226);

    public static Label Title(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), ForeColor = Text,
        Margin = new Padding(0, 0, 0, 4), UseMnemonic = false
    };

    public static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Text,
        Margin = new Padding(0, 14, 0, 6), UseMnemonic = false
    };

    public static Label Plain(string text, bool muted = false) => new()
    {
        Text = text, AutoSize = true, ForeColor = muted ? Muted : Text, Margin = new Padding(0, 6, 8, 0), UseMnemonic = false
    };

    public static Button Button(string text, string? accessibleDescription = null) => new()
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(84, 28),
        Padding = new Padding(8, 0, 8, 0), Margin = new Padding(6, 2, 0, 2), UseVisualStyleBackColor = true,
        AccessibleDescription = accessibleDescription
    };

    public static DataGridView Grid(string accessibleName)
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Line,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            EnableHeadersVisualStyles = false,
            AccessibleName = accessibleName,
            StandardTab = true,
            Margin = new Padding(0)
        };
        g.ColumnHeadersDefaultCellStyle.BackColor = Band;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Text;
        g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9f);
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Band;
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 234, 248);
        g.DefaultCellStyle.SelectionForeColor = Text;
        g.DefaultCellStyle.ForeColor = Text;
        return g;
    }

    /// <summary>
    /// A column that never has a fixed pixel width (auto-scaling would not scale it): it fills
    /// the grid by <paramref name="fill"/> weight, or sizes to its content when fill is 0.
    /// </summary>
    public static DataGridViewTextBoxColumn Column(string header, string property, float fill = 0, bool right = false)
    {
        var c = new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            DataPropertyName = property,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            AutoSizeMode = fill > 0 ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.AllCells
        };
        if (fill > 0) c.FillWeight = fill;
        if (right)
        {
            c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
        return c;
    }

    /// <summary>Row height from the grid's own font, so rows are never clipped at 150% or 200%.</summary>
    public static void FitRows(DataGridView g)
    {
        var h = TextRenderer.MeasureText("Ag", g.Font).Height + g.LogicalToDeviceUnits(8);
        g.RowTemplate.Height = h;
        foreach (DataGridViewRow r in g.Rows) r.Height = h;
    }
}

/// <summary>
/// A label that wraps to the width of its container (a one-column table or a panel) instead
/// of running off the right edge. The width comes from the laid-out parent, never from the
/// font, so it is right at every DPI.
/// </summary>
internal sealed class WrapLabel : Label
{
    public WrapLabel(string text, bool muted = false)
    {
        Text = text;
        AutoSize = true;
        UseMnemonic = false;
        ForeColor = muted ? Ui.Muted : Ui.Text;
        Margin = new Padding(0, 2, 0, 4);
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (Parent is not null) Parent.SizeChanged += (_, _) => Fit();
        Fit();
    }

    public void Fit()
    {
        if (Parent is null) return;
        var w = Parent.ClientSize.Width - Parent.Padding.Horizontal - Margin.Horizontal;
        if (Parent is ScrollableControl { VerticalScroll.Visible: true }) w -= SystemInformation.VerticalScrollBarWidth;
        if (w > 50 && MaximumSize.Width != w) MaximumSize = new Size(w, 0);
    }
}
