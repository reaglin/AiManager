using Eaglin.AiManager.Usage;

namespace Eaglin.AiManager.WinForms;

/// <summary>
/// AI Usage as a WinForms window — the counterpart of <c>Eaglin.AiManager.Wpf.UsageDashboardWindow</c>.
/// For a chosen month (and all programs, or one): the totals, then the same broken down by
/// program, by provider and model, and by day, and the most recent calls — all read from the
/// shared usage ledger. Copy as CSV puts the month's calls on the clipboard.
/// </summary>
public sealed class UsageDashboardForm : ScaledForm
{
    private readonly AiHub _hub;
    private readonly ComboBox _month;
    private readonly ComboBox _app;
    private readonly Label _subtitle;
    private readonly Label _budget;
    private readonly Label _empty;
    private readonly FlowLayoutPanel _tiles;
    private readonly DataGridView _byApp;
    private readonly DataGridView _byModel;
    private readonly DataGridView _byDay;
    private readonly DataGridView _recent;
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000, InitialDelay = 400 };
    private readonly Dictionary<string, Label> _tileValues = new();
    private bool _loading;
    private UsageReport? _report;

    private sealed record MonthItem(string Month)
    {
        public override string ToString() => UsageReport.MonthLabel(Month);
    }

    /// <param name="hub">Any hub; the ledger is shared by every program.</param>
    /// <param name="appFilter">Show one program's calls to start with (usually the calling app), or null for all programs.</param>
    public UsageDashboardForm(AiHub hub, string? appFilter = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;

        Text = "AI Usage";
        AccessibleName = "AI Usage";
        ClientSize = new Size(1080, 740);
        MinimumSize = new Size(880, 560);
        MinimizeBox = false;
        BackColor = Color.White;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 16, 20, 16) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // ── Header: title + filters ───────────────────────────────────────────
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(Ui.Title("AI Usage"), 0, 0);
        _subtitle = new WrapLabel(string.Empty, muted: true);

        var filters = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = new Padding(12, 6, 0, 0) };
        _month = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, AccessibleName = "Month", Margin = new Padding(0, 3, 12, 0) };
        _app = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, AccessibleName = "Program", Margin = new Padding(0, 3, 12, 0) };
        var refresh = Ui.Button("Refresh", "Reads the ledger again, including calls made since this window opened.");
        var csv = Ui.Button("Copy as CSV", "Copies every call of the chosen month (and program) to the clipboard, ready to paste into a spreadsheet.");
        filters.Controls.Add(new Label { Text = "Month:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        filters.Controls.Add(_month);
        filters.Controls.Add(new Label { Text = "Program:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        filters.Controls.Add(_app);
        filters.Controls.Add(refresh);
        filters.Controls.Add(csv);
        header.Controls.Add(filters, 1, 0);
        // Every row is placed explicitly: a TableLayoutPanel leaves hidden controls out of its
        // automatic placement, so the budget line or the empty-month note would shift the rows.
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_subtitle, 0, 1);

        _budget = new WrapLabel(string.Empty) { Font = new Font("Segoe UI Semibold", 9f), Visible = false, Margin = new Padding(0, 6, 0, 0) };
        root.Controls.Add(_budget, 0, 2);

        // ── Tiles ─────────────────────────────────────────────────────────────
        _tiles = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 10, 0, 4) };
        AddTile("prompts", "requests sent", "How many times a program asked an AI something.");
        AddTile("in", "tokens sent", "Tokens are pieces of words, roughly ¾ of a word each. Providers charge for the tokens they read…");
        AddTile("out", "tokens received", "…and, at a higher price, for the tokens they write back.");
        AddTile("pictures", "pictures made", "Pictures an AI drew for a program (OpenAI, Gemini or xAI). Each one is charged; the cost is in the estimated cost.");
        AddTile("cost", "estimated cost", "Worked out from each provider's list prices. The provider's own bill is the real figure.");
        AddTile("problems", "problems", "Requests that failed (for example a bad key or no credit) or were stopped by the user.");
        AddTile("avg", "average wait", "How long an answer took on average.");
        var tilesAndNote = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        tilesAndNote.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tilesAndNote.Controls.Add(_tiles, 0, 0);
        tilesAndNote.Controls.Add(new WrapLabel(
            "A token is a piece of a word, about three quarters of a word on average. Providers charge by the token: " +
            "for what they read (sent) and, at a higher price, for what they write back (received).", muted: true), 0, 1);
        root.Controls.Add(tilesAndNote, 0, 3);

        _empty = new WrapLabel(string.Empty, muted: true) { Visible = false };
        root.Controls.Add(_empty, 0, 4);

        // ── Breakdowns ────────────────────────────────────────────────────────
        var three = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 8, 0, 0) };
        three.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        three.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        three.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        three.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        three.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _byApp = Ui.Grid("Usage by program");
        _byApp.Columns.AddRange(Ui.Column("Program", nameof(UsageRow.Name), 100), Ui.Column("Requests", nameof(UsageRow.Prompts), right: true), Ui.Column("Cost", nameof(UsageRow.Cost), right: true));
        _byModel = Ui.Grid("Usage by provider and model");
        _byModel.Columns.AddRange(Ui.Column("Provider · model", nameof(UsageRow.Name), 100), Ui.Column("Requests", nameof(UsageRow.Prompts), right: true), Ui.Column("Sent", nameof(UsageRow.TokensIn), right: true), Ui.Column("Received", nameof(UsageRow.TokensOut), right: true), Ui.Column("Cost", nameof(UsageRow.Cost), right: true));
        _byDay = Ui.Grid("Usage by day");
        _byDay.Columns.AddRange(Ui.Column("Day", nameof(UsageRow.Name)), Ui.Column("Requests", nameof(UsageRow.Prompts), right: true), Ui.Column("Cost", nameof(UsageRow.Cost), right: true), Ui.Column("", nameof(UsageRow.CostUsd), 100));
        _byDay.Columns[3].Name = "bar";
        _byDay.CellPainting += PaintDayBar;
        foreach (var g in new[] { _byApp, _byModel, _byDay })
            g.DataBindingComplete += (sender, _) => Unselect((DataGridView)sender!);

        three.Controls.Add(Ui.Heading("By program"), 0, 0);
        three.Controls.Add(Ui.Heading("By provider and model"), 1, 0);
        three.Controls.Add(Ui.Heading("By day"), 2, 0);
        three.Controls.Add(Pad(_byApp, right: 8), 0, 1);
        three.Controls.Add(Pad(_byModel, right: 8), 1, 1);
        three.Controls.Add(Pad(_byDay, right: 0), 2, 1);

        root.Controls.Add(three, 0, 5);
        root.Controls.Add(Ui.Heading("Most recent requests"), 0, 6);

        _recent = Ui.Grid("Most recent requests");
        _recent.Columns.AddRange(
            Ui.Column("When", nameof(CallRow.When)), Ui.Column("Program", nameof(CallRow.App)), Ui.Column("Provider", nameof(CallRow.Provider)),
            Ui.Column("Model", nameof(CallRow.Model)), Ui.Column("What for", nameof(CallRow.Purpose), 100),
            Ui.Column("Sent", nameof(CallRow.TokensIn), right: true), Ui.Column("Received", nameof(CallRow.TokensOut), right: true),
            Ui.Column("Cost", nameof(CallRow.Cost), right: true), Ui.Column("Took", nameof(CallRow.Time), right: true), Ui.Column("Result", nameof(CallRow.Result)));
        _recent.DataBindingComplete += (_, _) => Unselect(_recent);
        root.Controls.Add(_recent, 0, 7);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 8, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var note = new Label
        {
            Text = "Every program that uses AI Manager writes each request here. Costs are estimates from the providers' list prices; " +
                   $"the provider's bill is the real figure. The records are kept in {AiPaths.UsageDir(hub.Root)}.",
            AutoSize = true, ForeColor = Ui.Muted, UseMnemonic = false, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 0)
        };
        var close = Ui.Button("Close", "Closes AI Usage.");
        close.Anchor = AnchorStyles.Right;
        close.Click += (_, _) => Close();
        bottom.Controls.Add(note, 0, 0);
        bottom.Controls.Add(close, 1, 0);
        bottom.Layout += (_, _) =>
        {
            var w = bottom.GetColumnWidths();
            if (w.Length == 2 && w[0] > 40 && note.MaximumSize.Width != w[0] - note.Margin.Horizontal)
                note.MaximumSize = new Size(w[0] - note.Margin.Horizontal, 0);
        };
        root.Controls.Add(bottom, 0, 8);
        CancelButton = close;

        // header, subtitle, budget, tiles, empty, breakdowns, heading, recent, note
        root.RowCount = 9;
        foreach (var i in Enumerable.Range(0, 9))
            root.RowStyles.Add(i switch { 5 => new RowStyle(SizeType.Percent, 45), 7 => new RowStyle(SizeType.Percent, 55), _ => new RowStyle(SizeType.AutoSize) });
        Controls.Add(root);

        _tips.SetToolTip(refresh, refresh.AccessibleDescription);
        _tips.SetToolTip(csv, csv.AccessibleDescription);
        _tips.SetToolTip(_month, "Which month to show.");
        _tips.SetToolTip(_app, "Show every program, or only one.");

        _month.SelectedIndexChanged += (_, _) => { if (!_loading) ShowReport(); };
        _app.SelectedIndexChanged += (_, _) => { if (!_loading) ShowReport(); };
        refresh.Click += (_, _) => { LoadFilters(SelectedApp, SelectedMonth); ShowReport(); };
        csv.Click += (_, _) => CopyCsv();

        LoadFilters(appFilter, null);
        ShowReport();
    }

    /// <summary>No row looks chosen: these tables are for reading, and a highlighted first row reads as "this one matters".</summary>
    private static void Unselect(DataGridView g)
    {
        g.ClearSelection();
        if (g.IsHandleCreated) g.CurrentCell = null;
    }

    private static Panel Pad(Control c, int right)
    {
        var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, right, 0), Margin = new Padding(0) };
        p.Controls.Add(c);
        return p;
    }

    private void AddTile(string id, string caption, string help)
    {
        var tile = new TableLayoutPanel
        {
            AutoSize = true, ColumnCount = 1, BackColor = Ui.Band, Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0, 0, 10, 8), MinimumSize = new Size(140, 0)
        };
        var value = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), ForeColor = Ui.Text, Margin = new Padding(0), UseMnemonic = false };
        var label = new Label { Text = caption, AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 2, 0, 0) };
        tile.Controls.Add(value);
        tile.Controls.Add(label);
        tile.AccessibleName = caption;
        _tips.SetToolTip(tile, help);
        _tips.SetToolTip(value, help);
        _tips.SetToolTip(label, help);
        _tileValues[id] = value;
        _tiles.Controls.Add(tile);
    }

    protected override void AfterScale()
    {
        foreach (var g in new[] { _byApp, _byModel, _byDay, _recent }) Ui.FitRows(g);
    }

    // ── Data ──────────────────────────────────────────────────────────────────

    private string SelectedMonth => _month.SelectedItem is MonthItem m ? m.Month : DateTime.Now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
    private string? SelectedApp => _app.SelectedIndex > 0 ? _app.SelectedItem as string : null;

    private void LoadFilters(string? app, string? month)
    {
        _loading = true;
        try
        {
            _hub.Reload();
            var months = _hub.Usage.Months();
            _month.Items.Clear();
            _month.Items.AddRange(months.Select(m => (object)new MonthItem(m)).ToArray());
            var mi = month is null ? 0 : Math.Max(0, months.ToList().IndexOf(month));
            _month.SelectedIndex = mi;

            var apps = UsageReport.Apps(_hub.Usage, app);
            _app.Items.Clear();
            _app.Items.Add(UsageReport.AllApps);
            _app.Items.AddRange(apps.Cast<object>().ToArray());
            var ai = app is null ? -1 : apps.ToList().FindIndex(a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase));
            _app.SelectedIndex = ai < 0 ? 0 : ai + 1;
        }
        finally { _loading = false; }
    }

    private void ShowReport()
    {
        var r = _report = UsageReport.Build(_hub, SelectedMonth, SelectedApp);

        _tileValues["prompts"].Text = r.PromptsText;
        _tileValues["in"].Text = r.TokensInText;
        _tileValues["out"].Text = r.TokensOutText;
        _tileValues["cost"].Text = r.CostText;
        _tileValues["pictures"].Text = r.PicturesText;
        _tileValues["problems"].Text = r.ProblemsText;
        _tileValues["avg"].Text = r.AverageText;

        _byApp.DataSource = r.ByApp.ToList();
        _byModel.DataSource = r.ByModel.ToList();
        _byDay.DataSource = r.ByDay.ToList();
        _recent.DataSource = r.Recent.ToList();
        foreach (var g in new[] { _byApp, _byModel, _byDay, _recent }) Unselect(g);

        _empty.Visible = r.Totals.Prompts == 0;
        _empty.Text = $"No AI requests were made {(SelectedApp is null ? "" : "by " + SelectedApp + " ")}in {UsageReport.MonthLabel(r.Month)}. " +
                      "When a program uses the AI, each request appears here with what it cost.";
        _budget.Visible = r.BudgetLine is not null;
        _budget.Text = r.BudgetLine ?? string.Empty;
        _budget.ForeColor = r.OverBudget ? Ui.Bad : Ui.Text;
        _subtitle.ForeColor = Ui.Muted;
        _subtitle.Text = SelectedApp is null
            ? $"What every program that uses AI Manager asked for in {UsageReport.MonthLabel(r.Month)}, and what it cost."
            : $"What {SelectedApp} asked for in {UsageReport.MonthLabel(r.Month)}, and what it cost.";
    }

    /// <summary>A small bar per day, as long as that day's cost relative to the month's most expensive day.</summary>
    private void PaintDayBar(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 3 || e.Graphics is null || _report is null || e.RowIndex >= _report.ByDay.Count) return;
        e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
        var max = _report.ByDay.Max(d => d.CostUsd);
        var cost = _report.ByDay[e.RowIndex].CostUsd;
        if (max > 0 && cost > 0)
        {
            var pad = _byDay.LogicalToDeviceUnits(4);
            var h = Math.Max(4, e.CellBounds.Height / 3);
            var w = (int)((e.CellBounds.Width - 2 * pad) * cost / max);
            using var brush = new SolidBrush(Ui.Accent);
            e.Graphics.FillRectangle(brush, e.CellBounds.X + pad, e.CellBounds.Y + (e.CellBounds.Height - h) / 2, Math.Max(2, w), h);
        }
        e.Handled = true;
    }

    private void CopyCsv()
    {
        if (_report is null) return;
        if (_report.Records.Count == 0)
        {
            MessageBox.Show(this, "There are no requests in this month to copy.", "Copy as CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Clipboard.SetText(_report.Csv());
            var said = _subtitle.Text;
            _subtitle.Text = $"Copied {_report.Records.Count} requests to the clipboard as CSV. Paste them into a spreadsheet.";
            _subtitle.ForeColor = Ui.Ok;
            var timer = new System.Windows.Forms.Timer { Interval = 6000 };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                if (IsDisposed) return;
                _subtitle.Text = said;
                _subtitle.ForeColor = Ui.Muted;
            };
            timer.Start();
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            MessageBox.Show(this, $"The clipboard is in use by another program, so nothing was copied ({ex.Message}). Try again in a moment.",
                "Copy as CSV", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }

    // ── Test seams ────────────────────────────────────────────────────────────

    internal UsageReport? Report => _report;
    internal string TileText(string id) => _tileValues[id].Text;
    internal int RecentRowCount => _recent.Rows.Count;
    internal string? AppFilter => SelectedApp;
}
