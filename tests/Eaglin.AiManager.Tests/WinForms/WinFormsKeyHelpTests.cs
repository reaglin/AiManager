using Eaglin.AiManager.WinForms;

namespace Eaglin.AiManager.Tests.WinForms;

/// <summary>1.6.0: the How? help beside Get a key, and errors that point to it.</summary>
public class WinFormsKeyHelpTests
{
    [Fact]
    public void A_Claude_key_made_for_no_single_workspace_says_to_choose_Default_Workspace()
    {
        var ex = new AiException(AiErrorKind.ProviderError, AiProviderType.Claude,
            "Claude returned 400: invalid_request_error: anthropic-workspace-id header is required", statusCode: 400);
        Assert.Contains("Default Workspace", AiSettingsSession.Explain(ex));
    }

    [Fact]
    public void A_rejected_key_mentions_expiry_and_the_How_link()
    {
        var text = AiSettingsSession.Explain(new AiException(AiErrorKind.Auth, AiProviderType.Mistral, "401"));
        Assert.Contains("expired", text);
        Assert.Contains("How?", text);
    }

    [Fact]
    public void Every_provider_row_has_a_How_link() => WinFormsTestSupport.Sta(() =>
    {
        using var t = new TempRoot();
        using var form = new AiSettingsForm(WinFormsTestSupport.Hub(t));
        var hows = All(form).OfType<LinkLabel>().Where(l => l.Text == "How?").ToList();
        Assert.Equal(AiProviderRouter.AllProviders.Count, hows.Count);
        Assert.Contains(All(form).OfType<LinkLabel>(), l => l.Text.StartsWith("Step-by-step help", StringComparison.Ordinal));
    });

    private static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => All(x).Prepend(x));
}
