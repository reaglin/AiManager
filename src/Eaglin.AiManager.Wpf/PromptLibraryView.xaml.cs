using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Eaglin.AiManager.Prompts;

namespace Eaglin.AiManager.Wpf;

/// <summary>
/// Edits the prompt templates of one app. Opened from an app it shows that app's library
/// (with its registered defaults, so Reset works); opened from the standalone AI Manager it
/// can switch between every app that has written templates, where Reset is unavailable
/// because only the app itself knows its defaults.
/// </summary>
public partial class PromptLibraryView : UserControl
{
    private readonly AiHub _hub;
    private PromptLibrary _library;
    private bool _loading;

    private sealed record Entry(PromptTemplate Template)
    {
        public string Label => Template.UserEdited ? $"{Template.Name}  (edited)" : Template.Name;
    }

    /// <param name="hub">The hub whose app's library is shown.</param>
    /// <param name="allowAppSwitch">Show an app chooser listing every app with templates on disk (standalone use).</param>
    public PromptLibraryView(AiHub hub, bool allowAppSwitch = false)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
        _library = hub.Prompts;
        InitializeComponent();

        if (allowAppSwitch)
        {
            var promptsRoot = Path.Combine(hub.Root, "prompts");
            var apps = Directory.Exists(promptsRoot)
                ? Directory.GetDirectories(promptsRoot).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).OrderBy(n => n).ToList()
                : new List<string>();
            if (!apps.Contains(hub.App, StringComparer.OrdinalIgnoreCase)) apps.Insert(0, hub.App);
            AppBox.ItemsSource = apps;
            AppBox.SelectedItem = apps.FirstOrDefault(a => string.Equals(a, hub.App, StringComparison.OrdinalIgnoreCase)) ?? apps.First();
        }
        else
        {
            AppLabel.Visibility = Visibility.Collapsed;
            AppBox.Visibility   = Visibility.Collapsed;
            LoadList();
        }
    }

    private void AppBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppBox.SelectedItem is not string app) return;
        _library = string.Equals(app, _hub.App, StringComparison.OrdinalIgnoreCase) ? _hub.Prompts : new PromptLibrary(_hub.Root, app);
        LoadList();
    }

    private void LoadList(string? select = null)
    {
        _loading = true;
        var entries = _library.List().Select(t => new Entry(t)).ToList();
        TemplateList.ItemsSource = entries;
        _loading = false;
        TemplateList.SelectedItem = entries.FirstOrDefault(e => e.Template.Name.Equals(select, StringComparison.OrdinalIgnoreCase)) ?? entries.FirstOrDefault();
        if (entries.Count == 0) ShowTemplate(null);
    }

    private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ShowTemplate((TemplateList.SelectedItem as Entry)?.Template);
    }

    private void ShowTemplate(PromptTemplate? t)
    {
        _loading = true;
        EditorPanel.IsEnabled = t is not null;
        NameBox.Text        = t?.Name ?? string.Empty;
        NameBox.IsReadOnly  = t is not null && (_library.HasDefault(t.Name) || _library.ExistsOnDisk(t.Name));
        DescriptionBox.Text = t?.Description ?? string.Empty;
        SystemBox.Text      = t?.System ?? string.Empty;
        UserBox.Text        = t?.User ?? string.Empty;
        MaxTokensBox.Text   = t?.MaxTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        KindText.Text       = t is null ? string.Empty
                            : t.UserEdited ? "your version" + (_library.HasDefault(t.Name) ? " (a default exists)" : "")
                            : _library.HasDefault(t.Name) ? "built-in default" : "on disk";
        ResetButton.IsEnabled  = t is not null && _library.HasDefault(t.Name);
        ResetButton.ToolTip    = ResetButton.IsEnabled ? null : "Only the app that owns this template knows its default; open the library from that app to reset.";
        DeleteButton.IsEnabled = t is not null && !_library.HasDefault(t.Name);
        PreviewBox.Text = string.Empty;
        _loading = false;
        UpdatePlaceholders();
        FooterStatus.Text = t is null ? string.Empty : _library.PathFor(t.Name);
    }

    private PromptTemplate ReadEditor()
    {
        int? max = null;
        if (int.TryParse(MaxTokensBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0) max = m;
        return new PromptTemplate(NameBox.Text.Trim(), SystemBox.Text, UserBox.Text, DescriptionBox.Text.Trim(), max);
    }

    private void Text_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) UpdatePlaceholders();
    }

    private void UpdatePlaceholders()
    {
        var names = ReadEditor().Placeholders;
        PlaceholdersText.Text = names.Count == 0 ? "(none)" : string.Join("  ", names.Select(n => "{" + n + "}"));

        SampleGrid.Children.Clear();
        SampleGrid.RowDefinitions.Clear();
        int r = 0;
        foreach (var n in names)
        {
            SampleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = n, Margin = new Thickness(0, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
            var box = new TextBox { Margin = new Thickness(0, 2, 0, 2), Tag = n, Text = $"<{n}>" };
            Grid.SetRow(label, r); Grid.SetColumn(label, 0);
            Grid.SetRow(box, r);   Grid.SetColumn(box, 1);
            SampleGrid.Children.Add(label);
            SampleGrid.Children.Add(box);
            r++;
        }
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        var values = SampleGrid.Children.OfType<TextBox>().ToDictionary(b => (string)b.Tag, b => b.Text);
        try
        {
            var req = ReadEditor().Bind(values);
            PreviewBox.Text = $"[system]\n{req.System}\n\n[user]\n{req.User}\n\n[max_tokens {req.MaxTokens}]";
        }
        catch (ArgumentException ex)
        {
            PreviewBox.Text = ex.Message;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var t = ReadEditor();
        if (t.Name.Length == 0)
        {
            MessageBox.Show("Give the template a name.", "Prompt Library", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _library.Save(t);
        LoadList(t.Name);
        FooterStatus.Text = $"Saved {t.Name} at {DateTime.Now:HH:mm:ss}.";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (TemplateList.SelectedItem is not Entry entry) return;
        if (MessageBox.Show($"Replace your version of '{entry.Template.Name}' with the built-in default?", "Prompt Library",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _library.ResetToDefault(entry.Template.Name);
        LoadList(entry.Template.Name);
        FooterStatus.Text = $"Reset {entry.Template.Name} to its default.";
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        TemplateList.SelectedItem = null;
        ShowTemplate(new PromptTemplate("new-template", "You are a helpful assistant.", "{text}", "Describe what this prompt is for."));
        NameBox.IsReadOnly = false;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (TemplateList.SelectedItem is not Entry entry) return;
        if (MessageBox.Show($"Delete the template '{entry.Template.Name}'? This cannot be undone.", "Prompt Library",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _library.Delete(entry.Template.Name);
        LoadList();
    }
}
