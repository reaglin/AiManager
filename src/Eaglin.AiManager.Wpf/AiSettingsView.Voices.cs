using System.IO;
using System.Windows;
using System.Windows.Media;
using Eaglin.AiManager.Store;

namespace Eaglin.AiManager.Wpf;

/// <summary>
/// The Voices section of AI Settings (1.8.0): the Google text-to-speech credential — an API key or
/// a service-account file — saved at once (not by the view's Save button), Test, Try the voices.
/// </summary>
public partial class AiSettingsView
{
    /// <summary>Shows what is stored for Google text-to-speech. Called from <see cref="Load"/>.</summary>
    /// <remarks>The saved key is never put back in the box (a hidden box would let a pasted key land after it); the status names it.</remarks>
    private void LoadVoices()
    {
        VoiceKeyBox.Password = string.Empty;
        var c = _hub.Credentials.GoogleTts;
        if (c is null)
        {
            SetVoiceStatus("No key yet. Programs cannot read aloud until one is saved here. How? shows where to get one.", "Ai.Off", "Ai.Muted");
        }
        else
        {
            var when = c.UpdatedUtc is { } u ? $" on {u.ToLocalTime():yyyy-MM-dd}" : "";
            var by = string.IsNullOrEmpty(c.UpdatedBy) ? "" : $" by {c.UpdatedBy}";
            SetVoiceStatus($"Saved: {c.Describe()}{when}{by}. Not tested yet — click Test. To replace it, paste a new key into the empty box and click Save key.",
                           "Ai.Off", "Ai.Muted");
        }
        SetVoiceButtons(enabled: true);
    }

    private void SetVoiceButtons(bool enabled)
    {
        var has = _hub.Credentials.GoogleTts is not null;
        VoiceSaveButton.IsEnabled = VoiceFileButton.IsEnabled = enabled;
        VoiceClearButton.IsEnabled = VoiceTestButton.IsEnabled = VoiceTryButton.IsEnabled = enabled && has;
        const string first = " Save a key or choose a service-account file first.";
        VoiceTestButton.ToolTip = has ? "Checks the stored key with Google: lists the voices, then speaks one short word (5 characters — free within Google's free monthly characters)."
                                      : "Nothing to test yet." + first;
        VoiceTryButton.ToolTip  = has ? "Opens the list of Google's voices, where you can hear a sample sentence in any of them. Each sample costs less than a cent (or nothing within Google's free monthly characters)."
                                      : "No voices without a key." + first;
        VoiceClearButton.ToolTip = has ? $"Removes the stored {What()} from this computer (it asks first)." : "Nothing is stored to clear.";
    }

    /// <summary>"Google Cloud key" or "service-account file", for the words on screen.</summary>
    private string What() => _hub.Credentials.GoogleTts?.Kind == GoogleTtsCredentialKind.ServiceAccount ? "service-account file" : "Google Cloud key";

    private void Say(string text, MessageBoxImage image) => Ask(text, MessageBoxButton.OK, image);

    private MessageBoxResult Ask(string text, MessageBoxButton buttons, MessageBoxImage image = MessageBoxImage.Question)
    {
        var owner = Window.GetWindow(this);
        var preset = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.OK;
        return owner is null
            ? MessageBox.Show(text, "Google text-to-speech", buttons, image, preset)
            : MessageBox.Show(owner, text, "Google text-to-speech", buttons, image, preset);
    }

    private void SetVoiceStatus(string text, string dot, string colour)
    {
        VoiceStatus.Text = text;
        VoiceStatus.Foreground = (Brush)FindResource(colour);
        VoiceDot.Fill = (Brush)FindResource(dot);
        VoiceDot.ToolTip = text;
    }

    /// <summary>
    /// Writes the voice credential at once. The store is re-read first, so keys typed in the
    /// provider rows but not yet saved are not written by this button (Save writes those).
    /// </summary>
    private bool SaveVoiceCredential(Action<CredentialStore> change, string done)
    {
        try
        {
            _hub.Credentials.Reload();
            change(_hub.Credentials);
            _hub.Credentials.Save();
        }
        catch (ArgumentException ex)
        {
            Say(ex.Message, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"It could not be saved: {ex.Message}", MessageBoxImage.Error);
            return false;
        }
        LoadVoices();
        FooterStatus.Text = done + " (Already saved: Revert or closing the window does not undo it.)";
        return true;
    }

    private void VoiceSave_Click(object sender, RoutedEventArgs e) => SaveVoiceKey(VoiceKeyBox.Password);

    private void SaveVoiceKey(string key)
    {
        key = key.Trim();
        if (key.Length == 0)
        {
            SetVoiceStatus("Paste the Google Cloud key into the box first, then click Save key. How? shows where to get one.", "Ai.Off", "Ai.Bad");
            return;
        }
        // A Gemini key (also "AIza…") is the commonest mistake: it is limited to Gemini and its project has no billing.
        var gemini = _hub.Credentials.KeyFor(AiProviderType.Gemini);
        if (gemini.Length > 0 && string.Equals(gemini, key, StringComparison.Ordinal))
        {
            SetVoiceStatus("This is your Gemini key. Voices need a separate Google Cloud key, from a project with billing and the " +
                           "Cloud Text-to-Speech API turned on. Click How? for the steps. Nothing was saved.", "Ai.Bad", "Ai.Bad");
            return;
        }
        if (SaveVoiceCredential(s => s.SetGoogleTtsApiKey(key, _hub.App), $"Google Cloud key for voices saved {DateTime.Now:HH:mm:ss}."))
            SetVoiceStatus($"Saved ({_hub.Credentials.GoogleTts?.Describe()}). Not tested yet — click Test to check that Google accepts it.", "Ai.Off", "Ai.Muted");
    }

    private void VoiceClear_Click(object sender, RoutedEventArgs e)
    {
        var what = What();
        var answer = Ask(
            $"Remove the {what} for voices from this computer?\n\nEvery program that uses AI Manager will stop being able to read aloud " +
            $"until one is saved again. Nothing is deleted at Google{(what == "service-account file" ? " or from the file itself" : "")}.",
            MessageBoxButton.YesNo);
        if (answer == MessageBoxResult.Yes)
            SaveVoiceCredential(s => s.ClearGoogleTts(), $"The {what} for voices was removed.");
    }

    private void VoiceFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the Google Cloud service-account key file",
            Filter = "Service-account key (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        string json;
        try { json = File.ReadAllText(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"The file could not be read: {ex.Message}", MessageBoxImage.Warning);
            return;
        }
        if (SaveVoiceCredential(s => s.SetGoogleTtsServiceAccount(json, _hub.App), "Service-account file for voices saved."))
            SetVoiceStatus($"Saved: {_hub.Credentials.GoogleTts?.Describe()}. AI Manager no longer needs the file. Not tested yet — click Test.", "Ai.Off", "Ai.Muted");
    }

    private void VoiceHow_Click(object sender, RoutedEventArgs e) =>
        new VoiceKeyHelpWindow(_hub) { Owner = Window.GetWindow(this) }.ShowDialog();

    /// <summary>
    /// Lists the voices, then speaks one short word: the list alone may succeed on a project without
    /// billing, while speaking would fail — so only speaking proves the key works.
    /// </summary>
    private async void VoiceTest_Click(object sender, RoutedEventArgs e)
    {
        SetVoiceButtons(enabled: false);
        SetVoiceStatus("Asking Google for its voices…", "Ai.Off", "Ai.Muted");
        try
        {
            var all = await _hub.ListVoicesAsync(refresh: true);
            var english = all.Count(v => v.LanguageCode.StartsWith("en-", StringComparison.OrdinalIgnoreCase));
            SetVoiceStatus("Speaking one test word…", "Ai.Off", "Ai.Muted");
            var voice = all.FirstOrDefault(v => v.Name.StartsWith("en-US-Standard-", StringComparison.OrdinalIgnoreCase))?.Name
                        ?? all.FirstOrDefault(v => v.Family == "Standard")?.Name ?? "en-US-Standard-A";
            await _hub.SpeakAsync(new AiSpeechRequest { Text = "Test.", Voice = voice, Purpose = "connection-test" });
            SetVoiceStatus($"Works: Google offers {all.Count:N0} voices ({english:N0} of them English) and spoke a test word.", "Ai.Ok", "Ai.Ok");
        }
        catch (AiException ex)
        {
            SetVoiceStatus(ex.Message, "Ai.Bad", "Ai.Bad");
        }
        finally { SetVoiceButtons(enabled: true); }
    }

    private void VoiceTry_Click(object sender, RoutedEventArgs e) =>
        new VoiceChooserWindow(_hub, null, browseOnly: true) { Owner = Window.GetWindow(this) }.ShowDialog();

    // ── Test seams ────────────────────────────────────────────────────────────

    internal void SaveVoiceKeyForTests(string key) => SaveVoiceKey(key);
    internal string VoiceStatusText => VoiceStatus.Text;
    internal bool VoiceTestEnabled => VoiceTestButton.IsEnabled;
}
