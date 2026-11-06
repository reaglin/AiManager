using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Eaglin.AiManager.Wpf;

/// <summary>
/// Choosing a Google voice (1.8.0): filters by language, kind (with its price) and gender, a search
/// box, the list, a sample sentence and Hear it. OK returns the chosen voice in
/// <see cref="SelectedVoice"/>. Usage:
/// <code>
/// var w = new VoiceChooserWindow(hub, character.Voice, "en-US") { Owner = this };
/// if (w.ShowDialog() == true) character.Voice = w.SelectedVoice!.Name;
/// </code>
/// </summary>
public sealed class VoiceChooserWindow : Window
{
    public const string DefaultSample = "The old house stood at the end of the lane, and every window was dark but one.";
    private const string AllLanguages = "All languages";
    private const string AllKinds = "All kinds";
    private const string AnyGender = "Any";

    private readonly AiHub _hub;
    private readonly string? _currentVoice;
    private readonly string _language;
    private readonly bool _browseOnly;
    private IReadOnlyList<AiVoice> _all = [];
    private bool _filling;

    private readonly ComboBox _languageBox = new() { MinWidth = 130, Margin = new Thickness(0, 0, 14, 0) };
    private readonly ComboBox _kindBox = new() { MinWidth = 250, Margin = new Thickness(0, 0, 14, 0) };
    private readonly ComboBox _genderBox = new() { MinWidth = 90, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBox _searchBox = new() { MinWidth = 160, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListView _list = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox _sampleBox = new() { TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MinHeight = 44, VerticalContentAlignment = VerticalAlignment.Top };
    private readonly Button _hearButton = new() { Content = "Hear it", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0), FontWeight = FontWeights.SemiBold };
    private readonly Button _stopButton = new() { Content = "Stop", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _okButton = new() { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _settingsButton = new() { Content = "Open AI Settings…", Padding = new Thickness(10, 3, 10, 3), Visibility = Visibility.Collapsed };

    private readonly MediaPlayer _player = new();
    private readonly Dictionary<(string Voice, string Text), string> _samples = new();
    private CancellationTokenSource? _speaking;

    /// <summary>The voice chosen when the window closed with OK; null otherwise.</summary>
    public AiVoice? SelectedVoice { get; private set; }

    /// <param name="hub">The program's hub (its credential, ledger and app name).</param>
    /// <param name="currentVoice">The voice chosen now (selected when the list opens), or null.</param>
    /// <param name="languageCode">The language shown first ("en-US", or "en" for every English voice); the person can change it.</param>
    /// <param name="sample">The sentence Hear it speaks; the person can change it.</param>
    /// <param name="browseOnly">True to listen to voices without choosing one (AI Settings' "Try the voices…"): no OK button, Close instead of Cancel.</param>
    public VoiceChooserWindow(AiHub hub, string? currentVoice, string languageCode = "en-US", string sample = DefaultSample, bool browseOnly = false)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
        _browseOnly = browseOnly;
        _currentVoice = string.IsNullOrWhiteSpace(currentVoice) ? null : currentVoice.Trim();
        _language = _currentVoice is not null && GoogleVoiceNames.Language(_currentVoice) is { Length: > 0 } fromVoice ? fromVoice
                  : string.IsNullOrWhiteSpace(languageCode) ? AllLanguages : languageCode.Trim();

        Title = browseOnly ? "Google voices" : "Choose a voice";
        Width = 860; Height = 640; MinWidth = 620; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _sampleBox.Text = string.IsNullOrWhiteSpace(sample) ? DefaultSample : sample;
        Content = Build();

        Loaded += async (_, _) => await LoadAsync(refresh: false);
        Closed += (_, _) => CleanUp();
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private UIElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(16) };

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = (_browseOnly
                       ? "Listen to Google's voices. Narrow the list with the boxes, click a voice, then Hear it. To give a narrator or a character a voice, " +
                         "choose it in your program (for example Author+). "
                       : "Pick the voice that reads aloud. Narrow the list with the boxes, click a voice, then Hear it to listen to the sample sentence. ") +
                   "The price is per million characters (a 4,000-word chapter is about 22,000 characters); Google gives free characters every month, " +
                   "so trying voices usually costs nothing."
        };
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        filters.Children.Add(Label("Language", _languageBox));
        filters.Children.Add(_languageBox);
        filters.Children.Add(Label("Kind", _kindBox));
        filters.Children.Add(_kindBox);
        filters.Children.Add(Label("Gender", _genderBox));
        filters.Children.Add(_genderBox);
        filters.Children.Add(Label("Search", _searchBox));
        filters.Children.Add(_searchBox);
        DockPanel.SetDock(filters, Dock.Top);
        root.Children.Add(filters);

        _languageBox.ToolTip = "Shows only the voices for one language. \"en-US\" is American English, \"en-GB\" British English, and so on.";
        _kindBox.ToolTip = "Google's kinds of voice. Chirp 3: HD and Studio sound most natural and cost more; Standard and WaveNet cost least. The price is per million characters spoken.";
        _genderBox.ToolTip = "Shows only female, male or neutral voices.";
        _searchBox.ToolTip = "Type part of a voice's name (for example Charon or Neural2) to find it.";
        AutomationProperties.SetName(_languageBox, "Language");
        AutomationProperties.SetName(_kindBox, "Kind of voice");
        AutomationProperties.SetName(_genderBox, "Gender");
        AutomationProperties.SetName(_searchBox, "Search voices");
        _languageBox.SelectionChanged += (_, _) => Refilter();
        _kindBox.SelectionChanged += (_, _) => Refilter();
        _genderBox.SelectionChanged += (_, _) => Refilter();
        _searchBox.TextChanged += (_, _) => Refilter();
        _genderBox.ItemsSource = new[] { AnyGender, "Female", "Male", "Neutral" };
        _genderBox.SelectedIndex = 0;

        // Bottom: OK / Cancel
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = _browseOnly ? "Close" : "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0),
                                  ToolTip = _browseOnly ? "Closes this window." : "Closes this window without changing the voice." };
        if (_browseOnly) _okButton.Visibility = Visibility.Collapsed;
        var okCancel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        okCancel.Children.Add(_okButton);
        okCancel.Children.Add(cancel);
        DockPanel.SetDock(okCancel, Dock.Right);
        bottom.Children.Add(okCancel);
        _settingsButton.ToolTip = "Opens AI Settings, where the Google text-to-speech key is added (the Voices section).";
        _settingsButton.Click += (_, _) => OpenSettings();
        bottom.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _settingsButton } });
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        _okButton.Click += (_, _) => Accept();
        ToolTipService.SetShowOnDisabled(_okButton, true);

        // Sample + Hear it
        var sample = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var sampleLabel = new TextBlock { Text = "Sample sentence", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 10, 0) };
        DockPanel.SetDock(sampleLabel, Dock.Left);
        sample.Children.Add(sampleLabel);
        var hear = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        hear.Children.Add(_hearButton);
        hear.Children.Add(_stopButton);
        DockPanel.SetDock(hear, Dock.Right);
        sample.Children.Add(hear);
        sample.Children.Add(_sampleBox);
        _sampleBox.ToolTip = "The words Hear it speaks. Type anything, for example a line from your own book.";
        AutomationProperties.SetName(_sampleBox, "Sample sentence");
        _hearButton.Click += async (_, _) => await HearAsync();
        _stopButton.Click += (_, _) => Stop();
        _stopButton.ToolTip = "Stops the sample playing.";
        ToolTipService.SetShowOnDisabled(_hearButton, true);
        ToolTipService.SetShowOnDisabled(_stopButton, true);

        var lower = new StackPanel();
        lower.Children.Add(sample);
        lower.Children.Add(_status);
        DockPanel.SetDock(lower, Dock.Bottom);
        root.Children.Add(lower);

        // The list
        var grid = new GridView();
        grid.Columns.Add(Column("Voice", nameof(Row.Speaker), 130));
        grid.Columns.Add(Column("Kind", nameof(Row.Family), 110));
        grid.Columns.Add(Column("Gender", nameof(Row.Gender), 80));
        grid.Columns.Add(Column("Language", nameof(Row.Language), 90));
        grid.Columns.Add(Column("$ per million characters", nameof(Row.Price), 150));
        grid.Columns.Add(Column("Google's name", nameof(Row.Name), 220));
        _list.View = grid;
        _list.SelectionMode = SelectionMode.Single;
        AutomationProperties.SetName(_list, "Voices");
        _list.ToolTip = _browseOnly ? "Click a voice, then Hear it. Double-click a voice to hear it." : "Click a voice, then Hear it. Double-click a voice to choose it.";
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += async (_, e) =>
        {
            if (_list.SelectedItem is not Row || e.ChangedButton != MouseButton.Left) return;
            if (_browseOnly) await HearAsync(); else Accept();
        };
        _player.MediaFailed += (_, e) =>
        {
            _stopButton.IsEnabled = false;
            Say($"The sample could not be played on this computer ({e.ErrorException?.Message ?? "unknown reason"}). Check that the sound is on and a speaker is connected.", bad: true);
        };
        root.Children.Add(_list);

        UpdateButtons();
        return root;

        static TextBlock Label(string text, Control target)
        {
            var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            AutomationProperties.SetLabeledBy(target, t);
            return t;
        }

        static GridViewColumn Column(string header, string path, double width) =>
            new() { Header = header, Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(path) };
    }

    /// <summary>One line of the list.</summary>
    private sealed record Row(AiVoice Voice)
    {
        public string Speaker => Voice.Speaker;
        public string Family => Voice.Family;
        public string Gender => Voice.Gender;
        public string Language => string.Join(", ", Voice.LanguageCodes.DefaultIfEmpty(Voice.LanguageCode));
        public string Price => Voice.PricePerMillionChars is { } p ? p.ToString("$0.##", CultureInfo.InvariantCulture) : "not known";
        public string Name => Voice.Name;
    }

    // ── Loading and filtering ─────────────────────────────────────────────────

    private async Task LoadAsync(bool refresh)
    {
        if (!_hub.CanSpeak)
        {
            Say("No Google text-to-speech key is stored, so there are no voices to show. Click Open AI Settings… and add one in the Voices section " +
                "(How? there shows where to get it).", bad: true);
            _settingsButton.Visibility = Visibility.Visible;
            return;
        }
        _settingsButton.Visibility = Visibility.Collapsed;
        Say("Loading Google's voices…");
        try
        {
            ShowVoices(await _hub.ListVoicesAsync(refresh: refresh));
        }
        catch (AiException ex)
        {
            Say(ex.Message, bad: true);
            if (ex.Kind is AiErrorKind.NoKey or AiErrorKind.Auth) _settingsButton.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Fills the filters and the list from a voice list (also the test seam: no network).</summary>
    internal void ShowVoices(IReadOnlyList<AiVoice> voices)
    {
        _all = voices;
        _filling = true;
        try
        {
            var languages = voices.SelectMany(v => v.LanguageCodes.DefaultIfEmpty(v.LanguageCode))
                                  .Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                                  .OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
            if (_language != AllLanguages && !languages.Contains(_language, StringComparer.OrdinalIgnoreCase)) languages.Insert(0, _language);
            _languageBox.ItemsSource = new[] { AllLanguages }.Concat(languages).ToList();
            _languageBox.SelectedItem = ((List<string>)_languageBox.ItemsSource).FirstOrDefault(l => string.Equals(l, _language, StringComparison.OrdinalIgnoreCase)) ?? AllLanguages;

            var kinds = voices.GroupBy(v => v.Family).Select(g => new KindItem(g.Key, g.First().PricePerMillionChars))
                              .OrderBy(k => k.Price ?? double.MaxValue).ThenBy(k => k.Family, StringComparer.Ordinal).ToList<object>();
            kinds.Insert(0, AllKinds);
            _kindBox.ItemsSource = kinds;
            _kindBox.SelectedIndex = 0;
        }
        finally { _filling = false; }
        Refilter();

        if (_currentVoice is not null && _list.Items.OfType<Row>().FirstOrDefault(r => string.Equals(r.Name, _currentVoice, StringComparison.OrdinalIgnoreCase)) is { } current)
        {
            _list.SelectedItem = current;
            _list.ScrollIntoView(current);
        }
        Say(voices.Count == 0 ? "Google returned no voices." : $"{_list.Items.Count:N0} voices shown of {voices.Count:N0}.");
    }

    private sealed record KindItem(string Family, double? Price)
    {
        public override string ToString() => Price is { } p ? $"{Family} — {p.ToString("$0.##", CultureInfo.InvariantCulture)} per million characters" : $"{Family} — price not known";
    }

    private void Refilter()
    {
        if (_filling) return;
        var selected = (_list.SelectedItem as Row)?.Name;
        var lang = _languageBox.SelectedItem as string;
        var kind = (_kindBox.SelectedItem as KindItem)?.Family;
        var gender = _genderBox.SelectedItem as string;
        var search = _searchBox.Text.Trim();

        var rows = _all.Where(v =>
                (lang is null || lang == AllLanguages || v.LanguageCodes.DefaultIfEmpty(v.LanguageCode)
                    .Any(l => string.Equals(l, lang, StringComparison.OrdinalIgnoreCase) || l.StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase)))
             && (kind is null || v.Family == kind)
             && (gender is null || gender == AnyGender || string.Equals(v.Gender, gender, StringComparison.OrdinalIgnoreCase))
             && (search.Length == 0 || v.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || v.Family.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Select(v => new Row(v)).ToList();
        _list.ItemsSource = rows;
        if (selected is not null && rows.FirstOrDefault(r => r.Name == selected) is { } keep) _list.SelectedItem = keep;
        if (_all.Count > 0)
            Say(rows.Count == 0 ? "No voice matches these choices. Choose \"All kinds\" or \"Any\", or clear the search box." : $"{rows.Count:N0} voices shown of {_all.Count:N0}.");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var has = _list.SelectedItem is Row;
        _okButton.IsEnabled = has;
        _okButton.ToolTip = has ? "Chooses the selected voice and closes this window." : "Click a voice in the list first.";
        _hearButton.IsEnabled = has && _speaking is null;
        _hearButton.ToolTip = has
            ? "Speaks the sample sentence with the selected voice. Google charges for the characters (a fraction of a cent), or nothing within its free monthly characters."
            : "Click a voice in the list first.";
    }

    // ── Hear it ───────────────────────────────────────────────────────────────

    private async Task HearAsync()
    {
        if (_list.SelectedItem is not Row row) return;
        var text = _sampleBox.Text.Trim();
        if (text.Length == 0) { Say("Type a sample sentence first.", bad: true); return; }

        Stop();
        if (_samples.TryGetValue((row.Name, text), out var path) && File.Exists(path))
        {
            Play(path);
            Say($"Playing {row.Speaker} ({row.Family}) again — nothing charged this time.");
            return;
        }

        _speaking = new CancellationTokenSource();
        UpdateButtons();
        _stopButton.IsEnabled = true;
        Say($"Asking Google to speak with {row.Speaker}…");
        try
        {
            var speech = await _hub.SpeakAsync(new AiSpeechRequest { Text = text, Voice = row.Name, Purpose = "voice-sample" }, null, _speaking.Token);
            path = Path.Combine(Path.GetTempPath(), $"AiManager-voice-sample-{Guid.NewGuid():N}{speech.FileExtension}");
            await File.WriteAllBytesAsync(path, speech.Audio);
            _samples[(row.Name, text)] = path;
            Play(path);
            var cost = speech.EstimatedCostUsd is { } c ? (c < 0.01 ? "less than a cent" : $"about ${c.ToString("0.00", CultureInfo.InvariantCulture)}") : "a price that is not known";
            Say($"Playing {row.Speaker} ({row.Family}): {speech.Characters:N0} characters, {cost} — or nothing while within Google's free monthly characters.");
        }
        catch (OperationCanceledException) { Say("Stopped."); }
        catch (AiException ex) { Say(ex.Message, bad: true); }
        catch (IOException ex) { Say($"The sample could not be saved to play it: {ex.Message}", bad: true); }
        finally
        {
            _speaking?.Dispose();
            _speaking = null;
            UpdateButtons();
        }
    }

    private void Play(string path)
    {
        _player.Open(new Uri(path));
        _player.Play();
        _stopButton.IsEnabled = true;
        _player.MediaEnded -= Ended;
        _player.MediaEnded += Ended;
    }

    private void Ended(object? sender, EventArgs e) => _stopButton.IsEnabled = _speaking is not null;

    private void Stop()
    {
        _speaking?.Cancel();
        _player.Stop();
        _stopButton.IsEnabled = false;
    }

    // ── Closing ───────────────────────────────────────────────────────────────

    private void Accept()
    {
        if (_browseOnly || _list.SelectedItem is not Row row) return;
        SelectedVoice = row.Voice;
        DialogResult = true;
    }

    private void OpenSettings()
    {
        new AiSettingsWindow(_hub) { Owner = this }.ShowDialog();
        _hub.Reload();
        _ = LoadAsync(refresh: false);
    }

    private void CleanUp()
    {
        _speaking?.Cancel();
        _player.Close();
        foreach (var path in _samples.Values)
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Say(string text, bool bad = false)
    {
        _status.Text = text;
        _status.Foreground = bad ? Brushes.Firebrick : SystemColors.ControlTextBrush;
    }

    // ── Test seams ────────────────────────────────────────────────────────────

    internal IReadOnlyList<string> ShownVoiceNames => _list.Items.OfType<Row>().Select(r => r.Name).ToList();
    internal string? SelectedVoiceName => (_list.SelectedItem as Row)?.Name;
    internal bool OkEnabled => _okButton.IsEnabled;
    internal bool OkVisible => _okButton.Visibility == Visibility.Visible;
    internal string StatusText => _status.Text;
    internal void SetSearch(string text) => _searchBox.Text = text;
    internal void PickLanguage(string language) => _languageBox.SelectedItem = ((IEnumerable<string>)_languageBox.ItemsSource).First(l => l == language);
    internal void PickKind(string family) => _kindBox.SelectedItem = _kindBox.Items.OfType<KindItem>().First(k => k.Family == family);
}
