using System.Net;
using System.Text;

namespace Eaglin.AiManager.Help;

/// <summary>
/// How to get a Google text-to-speech key (1.8.0) — the one place the steps are kept, shown by
/// How? in the Voices section of AI Settings and on the full help page. Dated like the provider
/// keys; re-check with them before each release (docs/PROVIDER-KEY-HELP.md).
/// </summary>
public static class VoiceKeyHelp
{
    public const string CheckedOn = "2026-11-06";

    public const string Name = "Google text-to-speech";

    public const string ConsolePage = "https://console.cloud.google.com/";

    public const string EnableApiPage = "https://console.cloud.google.com/apis/library/texttospeech.googleapis.com";

    public const string CredentialsPage = "https://console.cloud.google.com/apis/credentials";

    public const string BillingPage = "https://console.cloud.google.com/billing";

    public const string Summary =
        "Voices come from Google Cloud Text-to-Speech, which is separate from Gemini: a Gemini key from AI Studio usually does not work for it " +
        "(it is limited to Gemini and its project has no billing). " +
        "You need a Google Cloud project with billing turned on and the Cloud Text-to-Speech API enabled, then an API key from that project. " +
        "The key is stored encrypted on this computer and shared by every program that uses AI Manager.";

    public static IReadOnlyList<string> Steps { get; } =
    [
        "Open the Google Cloud console (console.cloud.google.com) and sign in with a Google account.",
        "Create a project, or choose one you have: the project list is at the top left of the page. Any name will do, for example \"My voices\".",
        "Turn on billing for the project (Billing in the menu ▸ link a billing account). Google asks for a card even for the free monthly characters.",
        "Turn on the API: open APIs & Services ▸ Library, search for \"Cloud Text-to-Speech API\", open it and click Enable.",
        "Make the key: open APIs & Services ▸ Credentials, click Create credentials ▸ API key. Google shows the new key.",
        "Restrict the key so it can only make speech: on the key's page, under API restrictions choose \"Restrict key\", tick \"Cloud Text-to-Speech API\", and Save.",
        "Copy the key and paste it into the \"Google Cloud API key\" box in AI Settings ▸ Voices. Click Save key, then Test (it speaks one word)."
    ];

    public static IReadOnlyList<KeyChoice> Choices { get; } =
    [
        new("Application restrictions", "None. The key is used by programs on this computer, not by a website."),
        new("API restrictions", "Restrict key ▸ Cloud Text-to-Speech API only. Then a leaked key cannot be spent on anything else."),
        new("Service account instead", "Optional, for people who already have one (CourseBuilder+ used to need it): IAM & Admin ▸ Service accounts ▸ Keys ▸ Add key ▸ JSON. Then choose \"Use a service-account file…\" in AI Settings.")
    ];

    public const string Billing =
        "Google charges by the character spoken. Every month the first characters are free: 4 million with Standard and WaveNet voices, " +
        "1 million with Neural2, Polyglot, Studio and Chirp 3: HD voices. After that, per million characters: Standard and WaveNet about $4, " +
        "Neural2 and Polyglot about $16, Chirp 3: HD about $30, Studio about $160. A 4,000-word chapter is about 22,000 characters — " +
        "about 66 cents with a Chirp 3: HD voice, or nothing while it is inside the free characters. The costs AI Manager shows ignore the free characters.";

    public static IReadOnlyList<string> Watch { get; } =
    [
        "\"Cloud Text-to-Speech API has not been used in project … or it is disabled\": step 4 was not done for the key's project, or was done less than a few minutes ago.",
        "\"Billing is not enabled\": step 3. The project needs a billing account even for the free characters.",
        "A Gemini key (from aistudio.google.com) is a different key and does not make speech here."
    ];

    public static IReadOnlyList<string> Sources { get; } =
    [
        "https://cloud.google.com/text-to-speech/docs/before-you-begin",
        "https://cloud.google.com/docs/authentication/api-keys",
        "https://cloud.google.com/text-to-speech/pricing"
    ];

    /// <summary>The steps as plain text, for How? beside the key box.</summary>
    public static string ShortText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary).AppendLine();
        for (var i = 0; i < Steps.Count; i++) sb.Append(i + 1).Append(". ").AppendLine(Steps[i]);
        sb.AppendLine().AppendLine("What to choose:");
        foreach (var c in Choices) sb.Append("• ").Append(c.Option).Append(": ").AppendLine(c.Choose);
        sb.AppendLine().Append("Paying: ").AppendLine(Billing);
        sb.AppendLine().AppendLine("If Test says something is wrong:");
        foreach (var w in Watch) sb.Append("• ").AppendLine(w);
        sb.AppendLine().Append("Checked ").Append(CheckedOn).Append(". Google changes its pages; if yours looks different, choose the option closest to the one described here.");
        return sb.ToString();
    }

    /// <summary>The section of the full help page (HTML).</summary>
    internal static string HtmlSection()
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<h2 id=\"voices\">").Append(E(Name)).Append(" (voices for reading aloud)</h2>");
        sb.Append("<p class=\"date\">Checked ").Append(E(CheckedOn)).Append(" · Console: <a href=\"").Append(E(CredentialsPage)).Append("\">")
          .Append(E(CredentialsPage)).Append("</a></p>");
        sb.Append("<p>").Append(E(Summary)).Append("</p><h3>Steps</h3><ol>");
        foreach (var s in Steps) sb.Append("<li>").Append(E(s)).Append("</li>");
        sb.Append("</ol><h3>What to choose</h3><table>");
        foreach (var c in Choices) sb.Append("<tr><th scope=\"row\">").Append(E(c.Option)).Append("</th><td>").Append(E(c.Choose)).Append("</td></tr>");
        sb.Append("</table><h3>Paying</h3><p>").Append(E(Billing)).Append("</p><h3>Watch out for</h3><ul>");
        foreach (var w in Watch) sb.Append("<li>").Append(E(w)).Append("</li>");
        sb.Append("</ul><p class=\"muted\">Google's own instructions: ");
        sb.Append(string.Join(" · ", Sources.Select(u => $"<a href=\"{E(u)}\">{E(u)}</a>")));
        sb.Append("</p>");
        return sb.ToString();
    }
}
