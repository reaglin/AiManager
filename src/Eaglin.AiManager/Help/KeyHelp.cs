using System.Net;
using System.Text;

namespace Eaglin.AiManager.Help;

/// <summary>One choice in a provider's "create key" form, and what to pick.</summary>
public sealed record KeyChoice(string Option, string Choose);

/// <summary>A dated entry in the change log of the key instructions.</summary>
public sealed record KeyHelpChange(string Date, AiProviderType? Provider, string What);

/// <summary>How to get an API key from one provider, as checked on <see cref="CheckedOn"/>.</summary>
public sealed record ProviderKeyHelp(
    AiProviderType Provider,
    string CheckedOn,
    string Summary,
    IReadOnlyList<string> Steps,
    IReadOnlyList<KeyChoice> Choices,
    string Billing,
    IReadOnlyList<string> Watch,
    IReadOnlyList<string> Sources)
{
    public string Name => AiHub.DisplayName(Provider);
    public string KeyPage => AiProviderRouter.KeyPages[Provider];
}

/// <summary>
/// Step-by-step instructions for getting an API key from each provider — the one place they
/// are kept. The settings windows show <see cref="ShortText"/> beside each Get a key link and
/// open the full page from <see cref="WriteHtml"/>. Providers change their websites without
/// notice: every provider carries the date its steps were checked, and <see cref="Changes"/>
/// records what changed. How to re-check them: docs/PROVIDER-KEY-HELP.md.
/// </summary>
public static class KeyHelp
{
    /// <summary>The date the whole page was last checked (the newest provider date).</summary>
    public const string CheckedOn = "2026-10-11";

    public const string HelpFileName = "api-keys.html";

    public const string MayChange =
        "Providers change their websites, the options in their forms and their prices without notice. " +
        "These instructions were correct when they were checked; if what you see is different, follow the provider's own page " +
        "and choose the option closest to the one described here.";

    public static IReadOnlyList<ProviderKeyHelp> All { get; } = new[]
    {
        new ProviderKeyHelp(AiProviderType.Claude, "2026-10-11",
            "Claude is made by Anthropic. Keys are created in the Claude Console (platform.claude.com, formerly console.anthropic.com).",
            new[]
            {
                "Click Get a key. Sign in, or create an account.",
                "Buy credits first: Settings ▸ Billing ▸ Buy credits (the smallest purchase is about $5). A key does not work on an account with no credit.",
                "Go to Settings ▸ API keys and click Create key.",
                "Fill in the form as shown below, then click Create.",
                "Copy the key (it starts with sk-ant-) and paste it into the Claude box in AI Settings. Press Test, then Save."
            },
            new[]
            {
                new KeyChoice("Name", "Anything you will recognise, for example \"AI Manager\". The key is shared by all your programs that use AI Manager, so a program's name is not needed."),
                new KeyChoice("Workspace", "Default Workspace — or a workspace you made yourself, such as one for your teaching. Do not choose the organization-wide option (no single workspace): such a key needs extra information on every request that these programs do not send, and Claude refuses the request."),
                new KeyChoice("Expiration", "Never, so the key does not stop working in the middle of a term. If you choose a date, Anthropic emails you before it expires and you will need a new key then."),
                new KeyChoice("Linked account (if asked)", "Yourself — a personal key.")
            },
            "Prepaid: buy credits under Settings ▸ Billing. Credits expire after a year and are not refunded. There are no free credits.",
            new[]
            {
                "The key is shown only once. If you lose it, delete it and create a new one.",
                "There is no option to make a key for one program only. The workspace is the only way to keep keys and spending apart on Anthropic's side; AI Manager's own AI Usage window already shows what each program spent.",
                "\"Key not accepted\" from a key that worked before usually means it expired or was deleted: create a new one."
            },
            new[]
            {
                "https://platform.claude.com/docs/en/get-api-key",
                "https://platform.claude.com/docs/en/manage-claude/authentication"
            }),

        new ProviderKeyHelp(AiProviderType.Gemini, "2026-10-11",
            "Gemini is made by Google. Keys are created in Google AI Studio with a Google account.",
            new[]
            {
                "Click Get a key and sign in with a Google account. Accept the terms if asked.",
                "A new user is given a project and a key automatically. If you have used Google Cloud before, go to Dashboard ▸ Projects ▸ Import projects first, then come back to API Keys.",
                "Click Create API key and pick the project (the default one is fine).",
                "If the key shows as Unrestricted: point at Unrestricted ▸ Add restrictions ▸ Restrict to Gemini API only ▸ Restrict key.",
                "Copy the key and paste it into the Gemini box in AI Settings. Press Test, then Save."
            },
            new[]
            {
                new KeyChoice("Project", "The default project AI Studio made for you."),
                new KeyChoice("Restrictions", "Restrict to Gemini API only. Gemini refuses unrestricted keys of the older kind.")
            },
            "There is a free tier with no billing, with lower limits and not every model. For more, click Set up billing beside the project and choose Prepay (from $5) or Postpay.",
            new[]
            {
                "On the free tier Google may use what you send to improve its products, and people may read it. Do not send students' personal information on the free tier; set up billing if you need that.",
                "If Create API key is greyed out you are probably signed in with a school or work Google account that does not allow it. Use a personal Google account, or ask your IT department.",
                "With Prepay, requests stop when the balance reaches $0 — they do not fall back to the free tier."
            },
            new[]
            {
                "https://ai.google.dev/gemini-api/docs/api-key",
                "https://ai.google.dev/gemini-api/docs/billing",
                "https://ai.google.dev/gemini-api/terms"
            }),

        new ProviderKeyHelp(AiProviderType.OpenAi, "2026-10-11",
            "OpenAI makes ChatGPT's models. API keys are separate from a ChatGPT subscription and are created on the OpenAI Platform.",
            new[]
            {
                "Click Get a key and sign in (a ChatGPT login works, but the API is billed separately).",
                "Buy credits first: Settings ▸ Billing ▸ add a payment method and buy credits (from $5).",
                "On API keys, click Create new secret key.",
                "Fill in the form as shown below, then click Create secret key.",
                "Copy the key (it starts with sk-) and paste it into the OpenAI box in AI Settings. Press Test, then Save."
            },
            new[]
            {
                new KeyChoice("Owned by", "You."),
                new KeyChoice("Name", "Anything you will recognise, for example \"AI Manager\"."),
                new KeyChoice("Project", "Default project."),
                new KeyChoice("Permissions", "All. \"Restricted\" keys need the right permissions ticked one by one and fail with confusing \"missing scopes\" errors.")
            },
            "Prepaid credits, from $5; they expire after a year. Adding a card may put a temporary $5 hold on it.",
            new[]
            {
                "The key is shown only once. If you lose it, create a new one.",
                "A ChatGPT Plus or Pro subscription does not pay for the API.",
                "Some newer models ask you to verify your organization (with a photo ID) before they can be used. If Test says so, follow the link in OpenAI's message, or pick another model."
            },
            new[]
            {
                "https://developers.openai.com/api/docs/quickstart",
                "https://help.openai.com/en/articles/8264778",
                "https://help.openai.com/en/articles/10910291"
            }),

        new ProviderKeyHelp(AiProviderType.Mistral, "2026-10-11",
            "Mistral AI is a French company. Keys are created in Mistral's console (also called La Plateforme or Studio).",
            new[]
            {
                "Click Get a key and sign in, or create an account.",
                "If asked, choose a plan for your workspace. The free plan is enough to start; add a payment method if Mistral asks for one before keys work.",
                "Open API Keys in the left-hand menu and click Create new key.",
                "Fill in the form as shown below, then create the key.",
                "Copy the key and paste it into the Mistral box in AI Settings. Press Test, then Save."
            },
            new[]
            {
                new KeyChoice("Workspace", "Your own workspace (a personal account has only one)."),
                new KeyChoice("Name", "Anything you will recognise, for example \"AI Manager\"."),
                new KeyChoice("Expiration date", "None, or a date far away. When a key expires you will need a new one."),
                new KeyChoice("Connector access (if shown)", "Shared connectors only. It does not matter for these programs.")
            },
            "Mistral offers a free plan with limits; paid use needs a payment method on the workspace.",
            new[]
            {
                "The full key is shown only once.",
                "A brand-new key can take a few minutes to start working. If Test says the key was not accepted, wait a few minutes and try again, and check the workspace has an active plan.",
                "On the free plan Mistral may use what you send to train its models; this can be turned off in the console's admin settings."
            },
            new[]
            {
                "https://docs.mistral.ai/getting-started/quickstarts/studio/activate-and-generate-api-key",
                "https://docs.mistral.ai/getting-started/quickstarts/admin/manage-workspaces"
            }),

        new ProviderKeyHelp(AiProviderType.XAi, "2026-10-11",
            "xAI makes the Grok models. Keys are created in the xAI console and belong to a team (a new account has one team, \"default\").",
            new[]
            {
                "Click Get a key and sign in, or create an account.",
                "Buy credits first: Billing ▸ add credits. Requests are refused when there is no credit.",
                "On API Keys, click Create API key.",
                "Fill in the form as shown below, then save.",
                "Copy the key and paste it into the xAI box in AI Settings. Press Test, then Save."
            },
            new[]
            {
                new KeyChoice("Name", "Anything you will recognise, for example \"AI Manager\"."),
                new KeyChoice("Endpoints and models (ACLs)", "Allow all endpoints and all models — at the very least the chat endpoint. A key with nothing allowed fails every request."),
                new KeyChoice("Rate limits", "Leave empty (no limit of your own).")
            },
            "Prepaid credits; there is no general free tier.",
            new[]
            {
                "Copy the key as soon as it is shown; you may not be able to see it again.",
                "xAI's pages sometimes use the name SpaceXAI; it is the same company."
            },
            new[]
            {
                "https://docs.x.ai/docs/tutorial",
                "https://docs.x.ai/console/billing"
            })
    };

    /// <summary>What changed in these instructions, newest first. Add an entry whenever a provider's steps are changed.</summary>
    public static IReadOnlyList<KeyHelpChange> Changes { get; } = new[]
    {
        new KeyHelpChange("2026-10-11", null,
            "First version of this page: every provider checked against its own documentation."),
        new KeyHelpChange("2026-10-11", AiProviderType.Claude,
            "The Console moved to platform.claude.com (the old console.anthropic.com address forwards). The create-key form asks for a workspace: choose Default Workspace, not the organization-wide option, which these programs cannot use."),
        new KeyHelpChange("2026-10-11", AiProviderType.Gemini,
            "Since May 2026 Gemini refuses older unrestricted keys: restrict the key to the Gemini API."),
        new KeyHelpChange("2026-10-11", AiProviderType.XAi,
            "The key page is console.x.ai/team/default/api-keys; a key must allow at least the chat endpoint.")
    };

    public static ProviderKeyHelp For(AiProviderType provider) =>
        All.FirstOrDefault(h => h.Provider == provider)
        ?? throw new ArgumentOutOfRangeException(nameof(provider), provider, "No key help for this provider.");

    /// <summary>The short version shown beside a provider's Get a key link: steps, choices and the date.</summary>
    public static string ShortText(AiProviderType provider)
    {
        var h = For(provider);
        var sb = new StringBuilder();
        sb.AppendLine(h.Summary).AppendLine();
        for (var i = 0; i < h.Steps.Count; i++) sb.Append(i + 1).Append(". ").AppendLine(h.Steps[i]);
        if (h.Choices.Count > 0)
        {
            sb.AppendLine().AppendLine("What to choose in the form:");
            foreach (var c in h.Choices) sb.Append("• ").Append(c.Option).Append(": ").AppendLine(c.Choose);
        }
        if (h.Watch.Count > 0) sb.AppendLine().Append("Note: ").AppendLine(h.Watch[0]);
        sb.AppendLine().Append("Checked ").Append(h.CheckedOn).Append(". Providers change their pages; if yours looks different, see the full help page.");
        return sb.ToString();
    }

    /// <summary>Where <see cref="WriteHtml"/> puts the page under a store root.</summary>
    public static string HelpFile(string root) => Path.Combine(root, "help", HelpFileName);

    /// <summary>Writes the full help page under the store root (replacing an older copy) and returns its path.</summary>
    public static string WriteHtml(string root)
    {
        var path = HelpFile(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Html(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>The full help page: every provider's steps, choices, billing and sources, dated, with the change log.</summary>
    public static string Html()
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Getting an API key — AI Manager</title>
            <style>
            body{font-family:"Segoe UI",system-ui,sans-serif;color:#202020;background:#fff;max-width:880px;margin:0 auto;padding:24px 16px 48px;line-height:1.5}
            h1{font-size:1.7em;margin:0 0 4px}h2{font-size:1.3em;margin:36px 0 6px;padding-top:12px;border-top:1px solid #d6dbe2}
            h3{font-size:1.05em;margin:18px 0 6px}.muted{color:#545454}.date{color:#545454;font-size:.95em}
            .notice{background:#fff7e0;border:1px solid #e8c766;border-radius:6px;padding:10px 14px;margin:16px 0}
            table{border-collapse:collapse;width:100%}th,td{text-align:left;vertical-align:top;border-bottom:1px solid #d6dbe2;padding:6px 8px}
            th{background:#f3f6fa;width:30%}a{color:#005fb8}nav a{margin-right:14px}
            @media print{.notice{border-color:#999}}
            </style></head><body>
            """);
        sb.Append("<h1>Getting an API key</h1>");
        sb.Append("<p class=\"date\">Instructions checked on <strong>").Append(E(CheckedOn))
          .Append("</strong> · AI Manager ").Append(E(typeof(KeyHelp).Assembly.GetName().Version?.ToString(3) ?? "")).Append("</p>");
        sb.Append("<div class=\"notice\"><strong>These instructions are subject to change.</strong> ").Append(E(MayChange)).Append("</div>");
        sb.Append("<p>The programs that use AI Manager (CourseBuilder+, Gamify+, Author+, Assessment+ and others from the same publisher) send your requests to an AI provider. ")
          .Append("The provider needs an <strong>API key</strong> — a long code, like a password — to know the request comes from your account, and it bills that account. ")
          .Append("You need a key for <strong>only one</strong> provider. Enter it once in AI Settings and every one of these programs on this computer uses it.</p>");
        sb.Append("<p>Every provider works the same way: sign in on its website, add credit if it asks for it, create a key, copy it, paste it into AI Settings, press <strong>Test</strong>, then <strong>Save</strong>. ")
          .Append("Keep the key private: anyone who has it can spend your credit. If it leaks, delete it on the provider's website and create a new one.</p>");
        sb.Append("<nav>");
        foreach (var h in All) sb.Append("<a href=\"#").Append(h.Provider).Append("\">").Append(E(h.Name)).Append("</a>");
        sb.Append("<a href=\"#changes\">What changed</a></nav>");

        foreach (var h in All)
        {
            sb.Append("<h2 id=\"").Append(h.Provider).Append("\">").Append(E(h.Name)).Append("</h2>");
            sb.Append("<p class=\"date\">Checked ").Append(E(h.CheckedOn)).Append(" · Key page: <a href=\"").Append(E(h.KeyPage)).Append("\">").Append(E(h.KeyPage)).Append("</a></p>");
            sb.Append("<p>").Append(E(h.Summary)).Append("</p><h3>Steps</h3><ol>");
            foreach (var s in h.Steps) sb.Append("<li>").Append(E(s)).Append("</li>");
            sb.Append("</ol>");
            if (h.Choices.Count > 0)
            {
                sb.Append("<h3>What to choose in the form</h3><table>");
                foreach (var c in h.Choices) sb.Append("<tr><th scope=\"row\">").Append(E(c.Option)).Append("</th><td>").Append(E(c.Choose)).Append("</td></tr>");
                sb.Append("</table>");
            }
            sb.Append("<h3>Paying</h3><p>").Append(E(h.Billing)).Append("</p>");
            if (h.Watch.Count > 0)
            {
                sb.Append("<h3>Watch out for</h3><ul>");
                foreach (var w in h.Watch) sb.Append("<li>").Append(E(w)).Append("</li>");
                sb.Append("</ul>");
            }
            sb.Append("<p class=\"muted\">The provider's own instructions: ");
            sb.Append(string.Join(" · ", h.Sources.Select(u => $"<a href=\"{E(u)}\">{E(u)}</a>")));
            sb.Append("</p>");
        }

        sb.Append("<h2 id=\"changes\">What changed in these instructions</h2><table>");
        foreach (var c in Changes)
            sb.Append("<tr><th scope=\"row\">").Append(E(c.Date)).Append(c.Provider is { } p ? " · " + E(AiHub.DisplayName(p)) : "")
              .Append("</th><td>").Append(E(c.What)).Append("</td></tr>");
        sb.Append("</table><p class=\"muted\">").Append(E(MayChange)).Append("</p></body></html>");
        return sb.ToString();
    }
}
