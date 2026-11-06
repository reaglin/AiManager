using Eaglin.AiManager.Store;
using Eaglin.AiManager.Tests.WinForms;
using Eaglin.AiManager.Usage;
using Eaglin.AiManager.Wpf;

namespace Eaglin.AiManager.Tests.Wpf;

/// <summary>1.8.0: the Voices section of the WPF AI Settings, the voice chooser and the dashboard tile.</summary>
public class WpfVoiceTests
{
    private static void EnsureApp()
    {
        if (System.Windows.Application.Current is null)
            _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
    }

    private static AiVoice V(string name, string gender) => new()
    {
        Name = name, LanguageCode = GoogleVoiceNames.Language(name), LanguageCodes = [GoogleVoiceNames.Language(name)],
        Gender = gender, Family = GoogleVoiceNames.Family(name), PricePerMillionChars = AiHub.SpeechPricePerMillion(name)
    };

    private static readonly AiVoice[] Voices =
    [
        V("en-GB-Neural2-B", "Male"), V("en-US-Chirp3-HD-Charon", "Male"), V("en-US-Chirp3-HD-Kore", "Female"),
        V("en-US-Studio-O", "Female"), V("en-US-Standard-A", "Male"), V("de-DE-Standard-A", "Female")
    ];

    [Fact]
    public void Voices_section_saves_a_key_at_once_without_saving_unsaved_provider_keys()
    {
        using var t = new TempRoot();
        WinFormsTestSupport.Sta(() =>
        {
            EnsureApp();
            var hub = WinFormsTestSupport.Hub(t, app: "Author+");
            var view = new AiSettingsView(hub);
            Assert.Contains("No key yet", view.VoiceStatusText);
            Assert.False(view.VoiceTestEnabled);

            hub.Credentials.SetKey(AiProviderType.Claude, "typed-but-not-saved", "test");   // as Test does with a typed key
            view.SaveVoiceKeyForTests("  AIzaVoiceKey  ");

            var back = new CredentialStore(t.Path);
            Assert.Equal("AIzaVoiceKey", back.GoogleTts!.Secret);
            Assert.Equal("Author+", back.GoogleTts.UpdatedBy);
            Assert.False(back.HasKey(AiProviderType.Claude));
            Assert.True(view.VoiceTestEnabled);
            Assert.Contains("Saved", view.VoiceStatusText);

            view.SaveVoiceKeyForTests("   ");
            Assert.Contains("Paste the Google Cloud key", view.VoiceStatusText);

            hub.Credentials.Reload();
            hub.Credentials.SetKey(AiProviderType.Gemini, "AIzaGeminiKey", "test");
            hub.Credentials.Save();
            view.SaveVoiceKeyForTests("AIzaGeminiKey");                                      // the Gemini key pasted by mistake
            Assert.Contains("This is your Gemini key", view.VoiceStatusText);
            Assert.Equal("AIzaVoiceKey", new CredentialStore(t.Path).GoogleTts!.Secret);
            view.SaveVoiceKeyForTests("   ");
            Assert.Contains("Paste the Google Cloud key", view.VoiceStatusText);
            Assert.NotNull(new CredentialStore(t.Path).GoogleTts);                            // a blank box does not clear it
        });
    }

    [Fact]
    public void Chooser_starts_on_the_callers_language_filters_by_kind_and_search_and_returns_the_selection()
    {
        using var t = new TempRoot();
        WinFormsTestSupport.Sta(() =>
        {
            EnsureApp();
            var hub = WinFormsTestSupport.Hub(t, app: "Author+");
            var w = new VoiceChooserWindow(hub, null, "en-US");
            w.ShowVoices(Voices);
            Assert.Equal(4, w.ShownVoiceNames.Count);
            Assert.All(w.ShownVoiceNames, n => Assert.StartsWith("en-US-", n));
            Assert.False(w.OkEnabled);

            w.PickKind("Chirp 3: HD");
            Assert.Equal(["en-US-Chirp3-HD-Charon", "en-US-Chirp3-HD-Kore"], w.ShownVoiceNames);
            w.SetSearch("kore");
            Assert.Equal(["en-US-Chirp3-HD-Kore"], w.ShownVoiceNames);
            w.SetSearch("nobody");
            Assert.Empty(w.ShownVoiceNames);
            Assert.Contains("No voice matches", w.StatusText);

            w.SetSearch("");
            w.PickLanguage("All languages");
            Assert.Equal(2, w.ShownVoiceNames.Count);                                          // still Chirp 3: HD only
            w.Close();
        });
    }

    [Fact]
    public void Chooser_opens_on_the_current_voice_and_its_language()
    {
        using var t = new TempRoot();
        WinFormsTestSupport.Sta(() =>
        {
            EnsureApp();
            var w = new VoiceChooserWindow(WinFormsTestSupport.Hub(t, app: "Author+"), "en-GB-Neural2-B", "en-US");
            w.ShowVoices(Voices);
            Assert.Equal("en-GB-Neural2-B", w.SelectedVoiceName);
            Assert.Equal(["en-GB-Neural2-B"], w.ShownVoiceNames);
            Assert.True(w.OkEnabled);
            Assert.True(w.OkVisible);
            w.Close();

            var browse = new VoiceChooserWindow(WinFormsTestSupport.Hub(t, app: "Author+"), null, browseOnly: true);
            Assert.False(browse.OkVisible);                                                    // Try the voices…: listening only
            browse.Close();
        });
    }

    [Fact]
    public void Dashboard_shows_characters_spoken()
    {
        using var t = new TempRoot();
        WinFormsTestSupport.Sta(() =>
        {
            EnsureApp();
            var hub = WinFormsTestSupport.Hub(t, app: "Author+");
            hub.Usage.Append(new UsageRecord { App = "Author+", Provider = AiProviderType.GoogleTts, Model = "en-US-Chirp3-HD-Charon", Chars = 22000, CostUsd = 0.66, Ok = true });
            _ = new UsageDashboardView(hub, "Author+");                                       // builds with a speech line
            Assert.Equal(22000, hub.Usage.Summarize(DateTime.Now, "Author+").Totals.Characters);
            var report = Eaglin.AiManager.WinForms.UsageReport.Build(hub, DateTime.Now.ToString("yyyy-MM"), "Author+");
            Assert.Equal(22000.ToString("N0"), report.CharactersText);
        });
    }
}
