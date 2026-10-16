using Eaglin.AiManager.WinForms;

namespace Eaglin.AiManager.Tests.WinForms;

/// <summary>AI Settings (WinForms): the provider for pictures (1.7.0) offers only the providers that can draw, and says who will draw.</summary>
public class WinFormsPictureTests
{
    [Fact]
    public void The_picture_box_offers_Automatic_and_only_the_providers_that_can_draw_and_says_who_draws()
    {
        using var t = new TempRoot();
        WinFormsTestSupport.Sta(() =>
        {
            var hub = WinFormsTestSupport.Hub(t);
            hub.Credentials.SetKey(AiProviderType.Claude, "sk-ant", "test");
            hub.Credentials.SetKey(AiProviderType.Gemini, "AIza", "test");
            hub.Credentials.Save();

            using var form = new AiSettingsForm(hub);
            Assert.Equal(new[] { "Automatic", "OpenAI", "Gemini (Google)", "xAI (Grok)" }.Length, form.PictureChoices.Count);
            Assert.Equal("Automatic", form.PictureChoices[0]);
            Assert.DoesNotContain(form.PictureChoices, c => c.Contains("Claude") || c.Contains("Mistral"));
            Assert.Equal("Same as all programs", form.AppPictureChoices![0]);
            Assert.Contains("Gemini (Google)", form.PictureNowText);   // automatic: Claude cannot draw, Gemini has a key

            form.SelectPictureProvider(1);                              // OpenAI, which has no key
            Assert.Contains("no key", form.PictureNowText);
            Assert.True(form.Dirty);
        });
    }

    [Fact]
    public void Save_writes_the_picture_providers_and_Automatic_clears_them()
    {
        using var t = new TempRoot();
        var s = new AiSettingsSession(WinFormsTestSupport.Hub(t));
        s.Save(new SettingsInput { ImageProvider = AiProviderType.XAi, AppImageProvider = AiProviderType.OpenAi });
        var again = AiHub.Open("PreseMaker", t.Path);
        Assert.Equal(AiProviderType.XAi, again.Settings.ImageProvider);
        Assert.Equal(AiProviderType.OpenAi, again.Settings.AppImageProvider("PreseMaker"));

        s.Save(new SettingsInput { ImageProvider = null, AppImageProvider = null });
        Assert.Null(AiHub.Open("X", t.Path).Settings.ImageProvider);
        Assert.Null(s.AppImageProvider);
    }
}
