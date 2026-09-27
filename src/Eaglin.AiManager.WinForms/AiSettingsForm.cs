using System.Globalization;
using Eaglin.AiManager.Legacy;

namespace Eaglin.AiManager.WinForms;

/// <summary>
/// AI Settings as a WinForms dialog — the counterpart of <c>Eaglin.AiManager.Wpf.AiSettingsWindow</c>.
/// One row per provider (the key, masked with Show; a status dot; Get a key; the model for all
/// programs and, when opened for a program, that program's own model; Models; Test), the default
/// provider for all programs and for the calling app, the monthly spending
/// warning, and Import keys from other programs. Nothing is written until Save; Cancel (or the
/// window's close box) throws the edits away, including a key that was only tested.
/// <code>using var f = new AiSettingsForm(hub); if (f.ShowDialog(this) == DialogResult.OK) …</code>
/// or simply <see cref="AiManagerWinForms.ShowSettings(IWin32Window?, string)"/>.
/// </summary>
public sealed class AiSettingsForm : ScaledForm
{
    private readonly AiSettingsSession _session;
    private readonly List<Row> _rows = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000, InitialDelay = 400 };

    private readonly TableLayoutPanel _providers;
    private readonly ComboBox _defaultProvider;
    private readonly ComboBox _appProvider;
    private readonly TextBox _budget;
    private readonly Label _spent;
    private readonly Label _footer;
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly List<(TableLayoutPanel Table, Label Label, int Col, int Span)> _fitted = new();

    private string _snapshot = string.Empty;
    private bool _saved;
    private bool _discarding;   // Cancel (or Esc) was pressed: close without asking, as its tooltip says

    /// <summary>Where Import looks for other programs' keys. Tests and screenshots point it at synthetic files.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal LegacyPaths? LegacyPaths { get; set; }

    private sealed class Row
    {
        public required AiProviderType Provider;
        public required Label Dot;
        public required TextBox Key;
        public required CheckBox Show;
        public required ComboBox Model;
        /// <summary>"Model for &lt;app&gt;"; null when the window was not opened for a program.</summary>
        public ComboBox? AppModel;
        public required Button Models;
        public required Button Test;
        public required Label Status;
        public bool Tested;
        /// <summary>The user picked from, or typed in, the model box: save it even if it is the built-in default.</summary>
        public bool ModelChosen;
    }

    private sealed record ProviderItem(AiProviderType Provider)
    {
        public override string ToString() => AiHub.DisplayName(Provider);
    }

    private sealed record SameAsAll
    {
        public override string ToString() => "Same as all programs";
    }

    public AiSettingsForm(AiHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _session = new AiSettingsSession(hub);
        var app = hub.App;

        Text = $"AI Settings — {app}";
        AccessibleName = "AI Settings";
        ClientSize = new Size(_session.ShowsAppModels ? 1180 : 1000, 720);
        MinimumSize = new Size(_session.ShowsAppModels ? 1020 : 860, 520);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        BackColor = Color.White;

        // ── Body (scrolls) ────────────────────────────────────────────────────
        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20, 16, 20, 8) };
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.Controls.Add(stack);

        stack.Controls.Add(Ui.Title("AI Settings"));
        stack.Controls.Add(new WrapLabel(
            $"{app} can use an AI to help with your work. The AI runs at a provider — the company that makes it — " +
            "and the provider needs an API key to know the request comes from your account."));
        stack.Controls.Add(new WrapLabel(
            "An API key is a long code, like a password, that you create on the provider's website after signing in. " +
            "The provider charges that account for what you use, usually a few cents a request. You need a key for only one provider. " +
            "To get one: click Get a key next to a provider, sign in (or sign up), create a key, copy it, and paste it into the box. " +
            "Then press Test to check it, and Save."));
        stack.Controls.Add(new WrapLabel(
            $"Your keys are stored encrypted for your Windows account in {hub.Root}. They are shared with the other programs " +
            "from the same publisher that use AI Manager, so a key entered once works in each of them — and changing or removing " +
            "a key here changes it for all of them.", muted: true));

        stack.Controls.Add(Ui.Heading("Providers"));
        _providers = BuildProviderTable();
        stack.Controls.Add(_providers);
        if (_session.ShowsAppModels)
            stack.Controls.Add(new WrapLabel(
                $"Model for {app}: \"Same as all programs\" uses the model to its left. Pick another model to have {app} use it while the other programs keep theirs. It takes effect when you press Save.",
                muted: true));

        stack.Controls.Add(Ui.Heading("Which provider to use"));
        var defaults = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0) };
        defaults.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        defaults.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        defaults.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _defaultProvider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Default provider for all programs", Margin = new Padding(0, 3, 12, 3) };
        _appProvider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right, AccessibleName = $"Provider for {app}", Margin = new Padding(0, 3, 12, 3) };
        _budget = new TextBox { Width = 100, Anchor = AnchorStyles.Left, AccessibleName = "Monthly spending warning in US dollars", Margin = new Padding(0, 3, 12, 3) };

        AddDefaultRow(defaults, 0, "Default provider for all programs:", _defaultProvider,
            "Used by every program that has not chosen its own. It needs a key above.");
        AddDefaultRow(defaults, 1, $"Provider for {app}:", _appProvider,
            $"Only {app}. \"Same as all programs\" follows the setting above.");
        AddDefaultRow(defaults, 2, "Monthly spending warning (US$):", _budget,
            "Leave blank for no warning. When the estimated spending of all programs passes this amount in a month, they warn you once. Nothing is blocked.");
        stack.Controls.Add(defaults);

        stack.Controls.Add(Ui.Heading("Keys from other programs"));
        stack.Controls.Add(new WrapLabel(
            "If you typed keys into PreseMaker, AuthorPlus or CIATLE before, you can copy them here instead of typing them again. " +
            "You will see what was found before anything is copied, and those programs' own files are not changed."));
        var import = Ui.Button("Import keys from other programs…", "Looks for keys saved by PreseMaker, AuthorPlus and CIATLE and offers to copy them here.");
        import.Anchor = AnchorStyles.Left;
        import.Margin = new Padding(0, 4, 0, 4);
        import.Click += (_, _) => ImportKeys();
        stack.Controls.Add(import);

        _spent = new WrapLabel(string.Empty, muted: true) { Margin = new Padding(0, 16, 0, 2) };
        stack.Controls.Add(_spent);
        var usage = new LinkLabel
        {
            Text = "See what each program spent…", AutoSize = true, LinkColor = Ui.Accent, Margin = new Padding(0, 0, 0, 8),
            AccessibleName = "See what each program spent (opens AI Usage)"
        };
        usage.LinkClicked += (_, _) =>
        {
            using var f = new UsageDashboardForm(_session.Hub);
            f.ShowDialog(this);
        };
        _tips.SetToolTip(usage, "Opens AI Usage: requests, tokens and estimated cost by program, provider and day.");
        stack.Controls.Add(usage);

        // ── Footer ────────────────────────────────────────────────────────────
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, BackColor = Ui.Band,
            Padding = new Padding(20, 10, 20, 10)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Ui.Line), 0, 0, footer.Width, 0);
        _footer = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Ui.Muted, UseMnemonic = false };
        _save = Ui.Button("Save", "Saves the keys and choices for every program that uses AI Manager, and closes.");
        _cancel = Ui.Button("Cancel", "Closes without saving. Keys that were only tested are forgotten.");
        _save.Font = new Font("Segoe UI Semibold", 9f);
        _save.Click += (_, _) => SaveAndClose();
        _cancel.Click += (_, _) => { _discarding = true; Close(); };
        footer.Controls.Add(_footer, 0, 0);
        footer.Controls.Add(_save, 1, 0);
        footer.Controls.Add(_cancel, 2, 0);
        _fitted.Add((footer, _footer, 0, 1));

        Controls.Add(body);
        Controls.Add(footer);
        CancelButton = _cancel;

        _tips.SetToolTip(_save, _save.AccessibleDescription);
        _tips.SetToolTip(_cancel, _cancel.AccessibleDescription);
        _tips.SetToolTip(import, import.AccessibleDescription);

        LoadFromStore();
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private TableLayoutPanel BuildProviderTable()
    {
        var app = _session.App;
        var perApp = _session.ShowsAppModels;
        var cModels = perApp ? 6 : 5;   // Models and Test move right when "Model for <app>" is shown
        var cTest = cModels + 1;
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = cTest + 1, Margin = new Padding(0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));    // dot
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));   // name / Get a key
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));    // key / status
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));        // Show
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, perApp ? 210 : 230));   // model (all programs)
        if (perApp) t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));      // model for <app>
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));        // Models
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));        // Test

        var r = 0;
        t.Controls.Add(Ui.Plain("Provider", muted: true), 1, r);
        t.Controls.Add(Ui.Plain("API key", muted: true), 2, r);
        t.Controls.Add(Ui.Plain(perApp ? "Model for all programs" : "Model (which AI to ask)", muted: true), 4, r);
        if (perApp) t.Controls.Add(Ui.Plain($"Model for {app}", muted: true), 5, r);
        r++;

        foreach (var p in AiProviderRouter.AllProviders)
        {
            var name = AiHub.DisplayName(p);
            var dot = new Label { Text = "●", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font("Segoe UI", 11f), ForeColor = Ui.Off, Margin = new Padding(0, 2, 4, 0), AccessibleName = $"{name}: no key" };
            var label = new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font("Segoe UI Semibold", 9f), ForeColor = Ui.Text, UseMnemonic = false };
            var key = new TextBox { UseSystemPasswordChar = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, AccessibleName = $"{name} API key", Margin = new Padding(0, 4, 4, 2) };
            var show = new CheckBox { Text = "Show", AutoSize = true, Anchor = AnchorStyles.Left, AccessibleName = $"Show the {name} key", Margin = new Padding(4, 4, 12, 2) };
            var model = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Anchor = AnchorStyles.Left | AnchorStyles.Right, AccessibleName = perApp ? $"{name} model for all programs" : $"{name} model", Margin = new Padding(0, 4, 4, 2) };
            var appModel = perApp
                ? new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right, AccessibleName = $"{name} model for {app}", Margin = new Padding(0, 4, 4, 2) }
                : null;
            var models = Ui.Button("Models", $"Asks {name} for its current list of models (needs the key).");
            var test = Ui.Button("Test", perApp
                ? $"Sends {name} a one-word question with this key and the model {app} uses, to check they work. It costs a fraction of a cent."
                : $"Sends {name} a one-word question with this key and model, to check they work. It costs a fraction of a cent.");
            models.MinimumSize = test.MinimumSize = new Size(70, 26);
            var link = new LinkLabel { Text = "Get a key", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 0, 0, 10), AccessibleName = $"Get a key from {name} (opens the website)", LinkColor = Ui.Accent };
            var status = new Label { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, ForeColor = Ui.Muted, Margin = new Padding(0, 0, 0, 10), UseMnemonic = false };

            models.AccessibleName = $"List {name} models";
            test.AccessibleName = $"Test the {name} key";
            var row = new Row { Provider = p, Dot = dot, Key = key, Show = show, Model = model, AppModel = appModel, Models = models, Test = test, Status = status };
            _rows.Add(row);

            show.CheckedChanged += (_, _) => key.UseSystemPasswordChar = !show.Checked;
            key.TextChanged += (_, _) =>
            {
                if (_loading) return;
                row.Tested = false;
                UpdateDot(row, null);
                SetStatus(row, key.Text.Trim().Length == 0
                    ? "No key. Get one from the link on the left and paste it into the box, or leave it empty if you do not use this provider."
                    : "Key changed. Press Test to check it, then Save.", null);
            };
            model.SelectionChangeCommitted += (_, _) => { if (!_loading) row.ModelChosen = true; };
            model.TextUpdate += (_, _) => { if (!_loading) row.ModelChosen = true; };
            models.Click += async (_, _) => await RefreshModelsAsync(row);
            test.Click += async (_, _) => await TestAsync(row);
            var url = AiProviderRouter.KeyPages[p];
            link.LinkClicked += (_, _) => OpenLink(url);

            _tips.SetToolTip(link, $"Opens {url} in your web browser. Sign in, create a key, copy it and paste it into the box.");
            _tips.SetToolTip(key, $"Paste your {name} API key here. It is hidden unless Show is ticked.");
            _tips.SetToolTip(model, perApp
                ? $"Which of the provider's AIs every program asks, unless it has its own choice. Pick one from the list, or type any name the provider accepts."
                : "Which of the provider's AIs to ask. Pick one from the list, or type any name the provider accepts.");
            if (appModel is not null)
                _tips.SetToolTip(appModel, $"Which of the provider's AIs {app} asks. \"Same as all programs\" follows the box to the left. A model picked here is for {app} only, and stays fixed for it even if the box to the left changes later.");
            _tips.SetToolTip(models, models.AccessibleDescription);
            _tips.SetToolTip(test, test.AccessibleDescription);

            t.Controls.Add(dot, 0, r);
            t.Controls.Add(label, 1, r);
            t.Controls.Add(key, 2, r);
            t.Controls.Add(show, 3, r);
            t.Controls.Add(model, 4, r);
            if (appModel is not null) t.Controls.Add(appModel, 5, r);
            t.Controls.Add(models, cModels, r);
            t.Controls.Add(test, cTest, r);
            r++;
            t.Controls.Add(link, 1, r);
            t.Controls.Add(status, 2, r);
            t.SetColumnSpan(status, cTest - 1);
            _fitted.Add((t, status, 2, cTest - 1));
            r++;
        }

        t.Layout += (_, _) => FitLabels();
        return t;
    }

    private void AddDefaultRow(TableLayoutPanel t, int r, string caption, Control control, string hint)
    {
        var label = new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6), UseMnemonic = false };
        var help = new Label { Text = hint, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Ui.Muted, Margin = new Padding(0, 6, 0, 6), UseMnemonic = false };
        t.Controls.Add(label, 0, r);
        t.Controls.Add(control, 1, r);
        t.Controls.Add(help, 2, r);
        _tips.SetToolTip(control, hint);
        _fitted.Add((t, help, 2, 1));
        t.Layout += (_, _) => FitLabels();
    }

    /// <summary>Wraps each status/hint label to the width of the table columns it sits in.</summary>
    private void FitLabels()
    {
        foreach (var (table, label, col, span) in _fitted)
        {
            var widths = table.GetColumnWidths();
            if (widths.Length < col + span) continue;
            var w = 0;
            for (var i = col; i < col + span; i++) w += widths[i];
            w -= label.Margin.Horizontal;
            if (w > 40 && label.MaximumSize.Width != w) label.MaximumSize = new Size(w, 0);
        }
    }

    protected override void AfterScale()
    {
        foreach (var row in _rows)
        {
            FitDropDown(row.Model);
            if (row.AppModel is not null) FitDropDown(row.AppModel);
        }
        FitDropDown(_defaultProvider);
        FitDropDown(_appProvider);
        FitLabels();
    }

    /// <summary>A drop-down list as wide as its longest item (the box itself may be narrower).</summary>
    private static void FitDropDown(ComboBox box)
    {
        var w = box.Width;
        foreach (var item in box.Items)
            w = Math.Max(w, TextRenderer.MeasureText(item?.ToString() ?? string.Empty, box.Font).Width + SystemInformation.VerticalScrollBarWidth + box.LogicalToDeviceUnits(8));
        box.DropDownWidth = w;
    }

    // ── Load / Save ───────────────────────────────────────────────────────────

    private bool _loading;

    private void LoadFromStore()
    {
        _loading = true;
        try { LoadFromStoreCore(); }
        finally { _loading = false; }
    }

    private void LoadFromStoreCore()
    {
        _session.Load();
        foreach (var row in _rows)
        {
            row.Key.Text = _session.KeyFor(row.Provider);
            FillModels(row, _session.ModelChoices(row.Provider));
            row.Model.Text = _session.ModelFor(row.Provider);
            SelectAppModel(row, _session.AppModelFor(row.Provider));
            row.Tested = false;
            row.ModelChosen = false;
            SetStatus(row, _session.SavedNote(row.Provider), null);
            UpdateDot(row, null);
        }

        var providers = AiProviderRouter.AllProviders.Select(p => new ProviderItem(p)).ToList();
        _defaultProvider.Items.Clear();
        _defaultProvider.Items.AddRange(providers.Cast<object>().ToArray());
        _defaultProvider.SelectedItem = providers.First(i => i.Provider == _session.DefaultProvider);

        _appProvider.Items.Clear();
        _appProvider.Items.Add(new SameAsAll());
        _appProvider.Items.AddRange(providers.Cast<object>().ToArray());
        _appProvider.SelectedIndex = _session.AppDefaultProvider is { } ap ? 1 + providers.FindIndex(i => i.Provider == ap) : 0;

        _budget.Text = _session.MonthlyBudgetUsd is { } b ? b.ToString("0.##", CultureInfo.CurrentCulture) : string.Empty;
        _spent.Text = $"Spent this month by all programs: {UsageReport.Money(_session.SpentThisMonth, 2)} (an estimate from the providers' list prices; the provider's bill is the real figure).";
        _footer.Text = string.Empty;
        _snapshot = Snapshot();
    }

    private static void FillModels(Row row, IReadOnlyList<string> ids)
    {
        var current = row.Model.Text;
        row.Model.BeginUpdate();
        row.Model.Items.Clear();
        row.Model.Items.AddRange(ids.Cast<object>().ToArray());
        row.Model.EndUpdate();
        if (!string.IsNullOrWhiteSpace(current)) row.Model.Text = current;

        if (row.AppModel is not null)
        {
            var chosen = AppModelOf(row);
            row.AppModel.BeginUpdate();
            row.AppModel.Items.Clear();
            row.AppModel.Items.Add(new SameAsAll());
            row.AppModel.Items.AddRange(ids.Cast<object>().ToArray());
            row.AppModel.EndUpdate();
            SelectAppModel(row, chosen);
        }
    }

    /// <summary>Selects a model in "Model for &lt;app&gt;" (null = Same as all programs), adding it to the list if it is not there.</summary>
    private static void SelectAppModel(Row row, string? model)
    {
        if (row.AppModel is not { } box) return;
        if (box.Items.Count == 0) box.Items.Add(new SameAsAll());
        if (string.IsNullOrWhiteSpace(model)) { box.SelectedIndex = 0; return; }
        var i = box.Items.Cast<object>().ToList().FindIndex(o => o is string s && string.Equals(s, model, StringComparison.OrdinalIgnoreCase));
        if (i < 0) i = box.Items.Add(model);
        box.SelectedIndex = i;
    }

    /// <summary>What "Model for &lt;app&gt;" holds: a model id, or null for Same as all programs.</summary>
    private static string? AppModelOf(Row row) => row.AppModel?.SelectedItem as string;

    private Dictionary<AiProviderType, string> TypedKeys() =>
        _rows.ToDictionary(r => r.Provider, r => r.Key.Text.Trim());

    private string Snapshot() =>
        string.Join("\u001f", _rows.Select(r => r.Key.Text.Trim() + "\u001e" + r.Model.Text.Trim() + "\u001e" + AppModelOf(r)))
        + "\u001f" + _defaultProvider.SelectedIndex + "\u001f" + _appProvider.SelectedIndex + "\u001f" + _budget.Text.Trim();

    private bool IsDirty => !_saved && Snapshot() != _snapshot;

    private bool SaveAndClose()
    {
        if (!AiSettingsSession.TryParseBudget(_budget.Text, out var budget))
        {
            MessageBox.Show(this,
                "The monthly spending warning must be an amount in dollars, such as 5 or 12.50 — or blank for no warning.",
                "AI Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _budget.Focus();
            _budget.SelectAll();
            return false;
        }

        var input = new SettingsInput
        {
            DefaultProvider = _defaultProvider.SelectedItem is ProviderItem gp ? gp.Provider : AiProviderType.Claude,
            AppDefaultProvider = _appProvider.SelectedItem is ProviderItem ap ? ap.Provider : null,
            MonthlyBudgetUsd = budget
        };
        foreach (var row in _rows)
        {
            input.Keys[row.Provider] = row.Key.Text;
            input.Models[row.Provider] = row.Model.Text;
            if (row.ModelChosen) input.ModelsChosen.Add(row.Provider);
            if (row.AppModel is not null) input.AppModels[row.Provider] = AppModelOf(row);
        }

        var chosen = input.AppDefaultProvider ?? input.DefaultProvider;
        if (input.Keys[chosen].Trim().Length == 0 && input.Keys.Values.Any(k => k.Trim().Length > 0))
        {
            var answer = MessageBox.Show(this,
                $"{_session.App} is set to use {AiHub.DisplayName(chosen)}, but there is no {AiHub.DisplayName(chosen)} key, so its AI features will not work.\n\n" +
                $"Save anyway? Choose No to go back and pick, under \"Which provider to use\", a provider that has a key.",
                "AI Settings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return false;
        }

        try
        {
            _session.Save(input);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                $"The settings could not be saved to {_session.Hub.Root}.\n\n{ex.Message}\n\nCheck that the folder exists and is not read-only, then press Save again.",
                "AI Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        _saved = true;
        DialogResult = DialogResult.OK;
        Close();
        return true;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // An editable ComboBox and a focused TextBox select their whole text when it is set;
        // a first-time user would read the highlight as "something is wrong".
        foreach (var row in _rows)
        {
            row.Model.SelectionLength = 0;
            row.Key.SelectionLength = 0;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!e.Cancel && !_saved && !_discarding && e.CloseReason == CloseReason.UserClosing && IsDirty)
        {
            // Only the window's close box asks; Cancel means "forget the changes" and says so.
            var answer = Ask("You changed keys or settings and have not saved them.",
                "Save them before closing? Other programs that use AI Manager see them only after Save.",
                ("Save and close", DialogResult.Yes), ("Close without saving", DialogResult.No), ("Keep editing", DialogResult.Cancel));
            if (answer == DialogResult.Cancel) e.Cancel = true;
            else if (answer == DialogResult.Yes && !SaveAndClose()) e.Cancel = true;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing.Cancel();
        if (!_saved) _session.Discard();   // a tested-but-unsaved key must not linger in the app's hub
        base.OnFormClosed(e);
    }

    /// <summary>
    /// A question whose buttons say what they do. A TaskDialog when the app has visual styles
    /// (every current WinForms app does, through ApplicationConfiguration.Initialize); otherwise a
    /// MessageBox whose text names what each of its buttons will do.
    /// </summary>
    private DialogResult Ask(string heading, string text, params (string Label, DialogResult Result)[] choices)
    {
        if (Application.RenderWithVisualStyles)
        {
            var buttons = choices.Select(c => (Button: new TaskDialogButton(c.Label), c.Result)).ToList();
            var page = new TaskDialogPage
            {
                Caption = "AI Settings", Heading = heading, Text = text, Icon = TaskDialogIcon.Information,
                AllowCancel = choices.Any(c => c.Result == DialogResult.Cancel)
            };
            foreach (var b in buttons) page.Buttons.Add(b.Button);
            page.DefaultButton = buttons[0].Button;
            var pressed = TaskDialog.ShowDialog(this, page);
            foreach (var b in buttons)
                if (b.Button == pressed) return b.Result;
            return DialogResult.Cancel;   // closed with Esc or the close box
        }

        var names = choices.Length == 2 ? new[] { "OK", "Cancel" } : new[] { "Yes", "No", "Cancel" };
        var legend = string.Join("\n", choices.Select((c, i) => $"{names[i]} = {c.Label}"));
        var result = MessageBox.Show(this, heading + "\n\n" + text + "\n\n" + legend, "AI Settings",
            choices.Length == 2 ? MessageBoxButtons.OKCancel : MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        var index = result switch { DialogResult.OK or DialogResult.Yes => 0, DialogResult.No => 1, _ => choices.Length - 1 };
        return choices[index].Result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    // ── Per-row actions ───────────────────────────────────────────────────────

    private void UpdateDot(Row row, bool? worked)
    {
        var hasKey = row.Key.Text.Trim().Length > 0;
        (row.Dot.ForeColor, var tip) = worked switch
        {
            true  => (Ui.Ok,  "Tested: the key works."),
            false => (Ui.Bad, "Tested: the key did not work. See the message under it."),
            _     => hasKey ? (Ui.Accent, "A key is entered but has not been tested.") : (Ui.Off, "No key.")
        };
        row.Dot.AccessibleName = $"{AiHub.DisplayName(row.Provider)}: {tip}";
        _tips.SetToolTip(row.Dot, tip);
    }

    private void SetStatus(Row row, string text, bool? ok)
    {
        row.Status.Text = text;
        row.Status.ForeColor = ok switch { true => Ui.Ok, false => Ui.Bad, _ => Ui.Muted };
    }

    private async Task TestAsync(Row row)
    {
        row.Test.Enabled = false;
        SetStatus(row, $"Testing — asking {AiHub.DisplayName(row.Provider)} a one-word question…", null);
        try
        {
            // Test the model this program will actually use: its own choice, else the one for all programs.
            var own = AppModelOf(row);
            var check = await _session.TestAsync(row.Provider, row.Key.Text, own ?? row.Model.Text, _closing.Token);
            if (check.Ok && own is not null)
                check = check with { Message = check.Message.Replace($"using {own}.", $"using {own}, the model for {_session.App}.", StringComparison.Ordinal) };
            if (IsDisposed) return;
            SetStatus(row, check.Message, check.Ok);
            row.Tested = check.Ok;
            UpdateDot(row, row.Key.Text.Trim().Length == 0 ? null : check.Ok);
        }
        catch (OperationCanceledException) { /* the form closed */ }
        finally
        {
            if (!IsDisposed) row.Test.Enabled = true;
        }
    }

    private async Task RefreshModelsAsync(Row row)
    {
        row.Models.Enabled = false;
        SetStatus(row, $"Asking {AiHub.DisplayName(row.Provider)} for its list of models…", null);
        try
        {
            var (check, ids) = await _session.RefreshModelsAsync(row.Provider, row.Key.Text, _closing.Token);
            if (IsDisposed) return;
            FillModels(row, ids);
            FitDropDown(row.Model);
            SetStatus(row, check.Message, check.Ok ? null : false);
            if (check.Ok) row.Model.DroppedDown = true;
        }
        catch (OperationCanceledException) { /* the form closed */ }
        finally
        {
            if (!IsDisposed) row.Models.Enabled = true;
        }
    }

    private void ImportKeys()
    {
        var found = AiSettingsSession.Discover(LegacyPaths);
        if (found.Count == 0)
        {
            var where = LegacyPaths ?? new LegacyPaths();
            MessageBox.Show(this,
                "No keys saved by other programs were found on this computer. These places were checked:\n\n" +
                $"PreseMaker: {where.PreseMaker}\nAuthorPlus: {where.AuthorPlus}\nCIATLE: {where.CiatleFce}\n\n" +
                "Nothing was changed. To add a key, use Get a key next to a provider.",
                "Import keys from other programs", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var typed = TypedKeys();
        var plan = AiSettingsSession.DescribeImport(found, p => typed.TryGetValue(p, out var k) && k.Length > 0);
        if (!plan.AnyToCopy)
        {
            MessageBox.Show(this, "Keys were found here:\n\n" + plan.Text + "\n\nEvery one of these providers already has a key here, so nothing needs copying. Nothing was changed.",
                "Import keys from other programs", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var answer = Ask("Keys from other programs were found.",
            plan.Text + "\n\nA provider that already has a key here keeps it. The other programs' files are not changed. " +
            "Nothing is saved until you press Save.",
            ("Copy these keys", DialogResult.OK), ("Don't copy", DialogResult.Cancel));
        if (answer != DialogResult.OK) return;
        ApplyImport(found);
    }

    private IReadOnlyList<AiProviderType> ApplyImport(IReadOnlyList<LegacySource> found)
    {
        var added = _session.Import(TypedKeys(), found);
        foreach (var row in _rows)
        {
            if (!added.Contains(row.Provider)) continue;
            row.Key.Text = _session.KeyFor(row.Provider);
            SetStatus(row, "Key copied from another program. Press Test to check it, then Save to keep it.", null);
        }
        _footer.Text = added.Count == 0
            ? "Nothing was copied: every provider that was found already has a key here."
            : $"Copied keys for {string.Join(", ", added.Select(AiHub.DisplayName))}. Press Save to keep them.";
        return added;
    }

    // ── Test seams (the tests drive the form without showing it) ─────────────

    internal string KeyText(AiProviderType p) => _rows.First(r => r.Provider == p).Key.Text;
    internal void SetKeyText(AiProviderType p, string key) => _rows.First(r => r.Provider == p).Key.Text = key;
    internal string StatusText(AiProviderType p) => _rows.First(r => r.Provider == p).Status.Text;
    internal Color DotColor(AiProviderType p) => _rows.First(r => r.Provider == p).Dot.ForeColor;
    internal bool KeyMasked(AiProviderType p) => _rows.First(r => r.Provider == p).Key.UseSystemPasswordChar;
    internal void SetShow(AiProviderType p, bool show) => _rows.First(r => r.Provider == p).Show.Checked = show;
    internal int ProviderRowCount => _rows.Count;
    internal bool ShowsAppModels => _rows.All(r => r.AppModel is not null) && _rows.Count > 0;
    internal string? AppModelText(AiProviderType p) => AppModelOf(_rows.First(r => r.Provider == p));
    internal IReadOnlyList<string> AppModelChoices(AiProviderType p) =>
        _rows.First(r => r.Provider == p).AppModel?.Items.Cast<object>().Select(o => o.ToString() ?? string.Empty).ToList() ?? new List<string>();
    /// <summary>As if the user picked from "Model for &lt;app&gt;" (null = Same as all programs).</summary>
    internal void PickAppModel(AiProviderType p, string? model) => SelectAppModel(_rows.First(r => r.Provider == p), model);
    /// <summary>As if the user picked <paramref name="model"/> from the model box for all programs.</summary>
    internal void PickModel(AiProviderType p, string model)
    {
        var row = _rows.First(r => r.Provider == p);
        row.Model.Text = model;
        row.ModelChosen = true;
    }
    internal bool SaveForTests() => SaveAndClose();
    internal string FooterText => _footer.Text;
    internal bool Dirty => IsDirty;
    internal Task TestForTests(AiProviderType p) => TestAsync(_rows.First(r => r.Provider == p));
    internal IReadOnlyList<AiProviderType> ImportForTests() => ApplyImport(AiSettingsSession.Discover(LegacyPaths));
}
