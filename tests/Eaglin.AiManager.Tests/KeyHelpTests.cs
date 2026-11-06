using System.Globalization;
using Eaglin.AiManager.Help;

namespace Eaglin.AiManager.Tests;

/// <summary>1.6.0: the dated instructions for getting a key from each provider.</summary>
public class KeyHelpTests
{
    [Fact]
    public void Every_provider_has_steps_choices_sources_and_a_valid_date()
    {
        foreach (var p in AiProviderRouter.AllProviders)
        {
            var h = KeyHelp.For(p);
            Assert.NotEmpty(h.Steps);
            Assert.NotEmpty(h.Choices);
            Assert.NotEmpty(h.Sources);
            Assert.False(string.IsNullOrWhiteSpace(h.Billing));
            Assert.All(h.Sources, u => Assert.StartsWith("https://", u));
            Assert.True(DateOnly.TryParseExact(h.CheckedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), $"{p}: {h.CheckedOn}");
            Assert.Equal(AiProviderRouter.KeyPages[p], h.KeyPage);
        }
        Assert.Equal(AiProviderRouter.AllProviders.Count, KeyHelp.All.Count);
    }

    [Fact]
    public void The_page_date_is_the_newest_provider_date_and_the_change_log_is_dated_newest_first()
    {
        Assert.Equal(KeyHelp.CheckedOn, KeyHelp.All.Max(h => h.CheckedOn));
        Assert.NotEmpty(KeyHelp.Changes);
        var dates = KeyHelp.Changes.Select(c => c.Date).ToList();
        Assert.Equal(dates.OrderByDescending(d => d, StringComparer.Ordinal), dates);
        // 1.8.0: the voices section (Google text-to-speech) carries its own date
        var newest = string.CompareOrdinal(VoiceKeyHelp.CheckedOn, KeyHelp.CheckedOn) > 0 ? VoiceKeyHelp.CheckedOn : KeyHelp.CheckedOn;
        Assert.All(dates, d => Assert.True(string.CompareOrdinal(d, newest) <= 0));
        Assert.Contains("id=\"voices\"", KeyHelp.Html());
        Assert.Contains("Cloud Text-to-Speech API", VoiceKeyHelp.ShortText());
    }

    [Fact]
    public void Claude_help_says_to_choose_one_workspace()
    {
        var text = KeyHelp.ShortText(AiProviderType.Claude);
        Assert.Contains("Default Workspace", text);
        Assert.Contains("shown only once", KeyHelp.Html());
        Assert.Contains("platform.claude.com", AiProviderRouter.KeyPages[AiProviderType.Claude]);
    }

    [Fact]
    public void Short_text_carries_the_date_and_says_pages_change()
    {
        foreach (var p in AiProviderRouter.AllProviders)
        {
            var text = KeyHelp.ShortText(p);
            Assert.Contains("Checked " + KeyHelp.For(p).CheckedOn, text);
            Assert.Contains("change", text);
        }
    }

    [Fact]
    public void Html_page_is_dated_says_it_may_change_covers_every_provider_and_is_written_under_the_root()
    {
        using var t = new TempRoot();
        var path = KeyHelp.WriteHtml(t.Path);
        Assert.Equal(Path.Combine(t.Path, "help", "api-keys.html"), path);
        var html = File.ReadAllText(path);
        Assert.Contains("checked on <strong>" + KeyHelp.CheckedOn, html);
        Assert.Contains("subject to change", html);
        foreach (var h in KeyHelp.All)
        {
            Assert.Contains($"id=\"{h.Provider}\"", html);
            Assert.Contains(h.KeyPage, html);
        }
        Assert.Contains("id=\"changes\"", html);
        Assert.DoesNotContain("<script", html);

        KeyHelp.WriteHtml(t.Path);   // a second write replaces the first without complaint
    }

}
