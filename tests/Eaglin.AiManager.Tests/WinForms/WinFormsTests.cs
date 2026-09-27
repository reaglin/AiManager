using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Eaglin.AiManager.Legacy;
using Eaglin.AiManager.Usage;
using Eaglin.AiManager.WinForms;

namespace Eaglin.AiManager.Tests.WinForms;

/// <summary>Helpers for the WinForms tests: an STA thread, a hub with fake providers, legacy files.</summary>
internal static class WinFormsTestSupport
{
    /// <summary>
    /// Runs on an STA thread (WinForms controls need one) without installing the WinForms
    /// synchronisation context, so an awaited call resumes without a message loop.
    /// </summary>
    public static void Sta(Func<Task> body)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            WindowsFormsSynchronizationContext.AutoInstall = false;
            try { body().GetAwaiter().GetResult(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    public static void Sta(Action body) => Sta(() => { body(); return Task.CompletedTask; });

    /// <summary>A hub whose providers are fakes: answer, or throw the given error. Never the network.</summary>
    public static AiHub Hub(TempRoot t, AiException? fails = null, string app = "PreseMaker")
    {
        var hub = AiHub.Open(app, t.Path);
        hub.ProviderFactory = (type, key, model) => new FakeProvider(type, model) { Answer = "OK", Throws = fails };
        return hub;
    }

    public static string Dpapi(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static LegacyPaths LegacyIn(TempRoot t, string? preseMakerJson = null)
    {
        var paths = new LegacyPaths
        {
            PreseMaker = Path.Combine(t.Path, "legacy", "PreseMaker", "app_settings.json"),
            AuthorPlus = Path.Combine(t.Path, "legacy", "AuthorPlus", "ai_settings.json"),
            CiatleFce  = Path.Combine(t.Path, "legacy", "CIATLE-FCE", "prefs.json")
        };
        if (preseMakerJson is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.PreseMaker)!);
            File.WriteAllText(paths.PreseMaker, preseMakerJson);
        }
        return paths;
    }

    public static void Ledger(AiHub hub, DateTime utc, string app, AiProviderType p, string model, int inTok, int outTok, double? cost, bool ok = true, bool cancelled = false, string purpose = "unit") =>
        hub.Usage.Append(new UsageRecord { T = utc, App = app, Provider = p, Model = model, InTok = inTok, OutTok = outTok, CostUsd = cost, Ms = 1200, Ok = ok, Cancelled = cancelled, Error = ok || cancelled ? null : "Auth", Purpose = purpose });
}

public class AiSettingsSessionTests
{
    [Theory]
    [InlineData("", true, null)]
    [InlineData("   ", true, null)]
    [InlineData("0", true, null)]
    [InlineData("$0", true, null)]
    [InlineData("5", true, 5.0)]
    [InlineData("$12.50", true, 12.5)]
    [InlineData(" 7.25 ", true, 7.25)]
    [InlineData("-3", false, null)]
    [InlineData("five", false, null)]
    public void Budget_box_accepts_dollars_or_blank(string text, bool ok, double? expected)
    {
        Assert.Equal(ok, AiSettingsSession.TryParseBudget(text, out var budget));
        Assert.Equal(expected, budget);
    }

    [Fact]
    public void Save_writes_keys_and_choices_to_the_shared_store_for_every_program()
    {
        using var t = new TempRoot();
        var s = new AiSettingsSession(WinFormsTestSupport.Hub(t));
        var input = new SettingsInput { DefaultProvider = AiProviderType.Gemini, AppDefaultProvider = AiProviderType.Claude, MonthlyBudgetUsd = 10 };
        input.Keys[AiProviderType.Claude] = "  sk-ant-typed  ";
        input.Keys[AiProviderType.Gemini] = "AIza-typed";
        input.Models[AiProviderType.Claude] = "claude-sonnet-5";
        input.Models[AiProviderType.Gemini] = AiProviderRouter.DefaultModels[AiProviderType.Gemini];
        s.Save(input);

        var other = AiHub.Open("AuthorPlus", t.Path);                 // another program, same store
        Assert.Equal("sk-ant-typed", other.Credentials.KeyFor(AiProviderType.Claude));
        Assert.Equal("AIza-typed", other.Credentials.KeyFor(AiProviderType.Gemini));
        Assert.Equal(AiProviderType.Gemini, other.DefaultProvider);  // AuthorPlus follows the global default
        Assert.Equal("claude-sonnet-5", other.Settings.ModelFor(AiProviderType.Claude));
        Assert.Null(other.Settings.ModelFor(AiProviderType.Gemini)); // the built-in default is not pinned
        Assert.Equal(10, other.Settings.MonthlyBudgetUsd);
        Assert.Equal(AiProviderType.Claude, AiHub.Open("PreseMaker", t.Path).DefaultProvider);
        Assert.Equal("PreseMaker", other.Credentials.EntryFor(AiProviderType.Claude)!.UpdatedBy);
    }

    [Fact]
    public void Same_as_all_programs_removes_the_app_override()
    {
        using var t = new TempRoot();
        var hub = WinFormsTestSupport.Hub(t);
        hub.Settings.ForApp("PreseMaker").DefaultProvider = AiProviderType.OpenAi;
        hub.SaveSettings();
        var s = new AiSettingsSession(hub);
        Assert.Equal(AiProviderType.OpenAi, s.AppDefaultProvider);

        s.Save(new SettingsInput { DefaultProvider = AiProviderType.Claude, AppDefaultProvider = null });

        Assert.False(AiHub.Open("X", t.Path).Settings.Apps.ContainsKey("PreseMaker"));
        Assert.Null(s.AppDefaultProvider);
    }

    [Fact]
    public void Discard_forgets_a_key_that_was_typed_and_tested_but_not_saved()
    {
        using var t = new TempRoot();
        var hub = WinFormsTestSupport.Hub(t);
        var s = new AiSettingsSession(hub);
        s.ApplyKeys(new Dictionary<AiProviderType, string> { [AiProviderType.Mistral] = "typed" });
        Assert.True(hub.Credentials.HasKey(AiProviderType.Mistral));

        s.Discard();

        Assert.False(hub.Credentials.HasKey(AiProviderType.Mistral));
        Assert.False(File.Exists(AiPaths.CredentialsFile(t.Path)));
    }

    [Fact]
    public async Task Test_uses_the_typed_key_and_says_what_happened_in_words()
    {
        using var t = new TempRoot();
        var hub = WinFormsTestSupport.Hub(t);
        var s = new AiSettingsSession(hub);

        var empty = await s.TestAsync(AiProviderType.Claude, "  ", "claude-opus-5");
        Assert.False(empty.Ok);
        Assert.Contains("Paste a key", empty.Message);
        Assert.Empty(hub.Usage.Read(DateTime.Now));                    // nothing was sent

        var ok = await s.TestAsync(AiProviderType.Claude, "sk-ant-unsaved", "claude-opus-5");
        Assert.True(ok.Ok);
        Assert.Contains("Works", ok.Message);
        Assert.Contains("Save", ok.Message);
        var call = Assert.Single(hub.Usage.Read(DateTime.Now));
        Assert.Equal("connection-test", call.Purpose);
        Assert.Equal("PreseMaker", call.App);
        Assert.False(File.Exists(AiPaths.CredentialsFile(t.Path)));  // tested, not saved
    }

    [Theory]
    [InlineData(AiErrorKind.Auth, "did not accept this key")]
    [InlineData(AiErrorKind.RateLimit, "billing")]
    [InlineData(AiErrorKind.Network, "internet connection")]
    [InlineData(AiErrorKind.Timeout, "too long")]
    public async Task A_failed_test_says_what_to_do_next(AiErrorKind kind, string words)
    {
        using var t = new TempRoot();
        var s = new AiSettingsSession(WinFormsTestSupport.Hub(t, new AiException(kind, AiProviderType.OpenAi, "vendor text")));
        var r = await s.TestAsync(AiProviderType.OpenAi, "sk-bad", "gpt-4o");
        Assert.False(r.Ok);
        Assert.Contains(words, r.Message);
    }

    [Fact]
    public void Model_choices_are_the_built_in_names_then_the_fetched_ones_without_repeats()
    {
        using var t = new TempRoot();
        var s = new AiSettingsSession(WinFormsTestSupport.Hub(t));
        var ids = s.ModelChoices(AiProviderType.Claude, new[] { "CLAUDE-OPUS-5", "claude-new-6", "" });
        Assert.Equal(AiProviderRouter.FallbackModels[AiProviderType.Claude].Concat(new[] { "claude-new-6" }), ids);
        Assert.Equal(AiProviderRouter.DefaultModels[AiProviderType.XAi], s.ModelFor(AiProviderType.XAi));
    }

    [Fact]
    public void Import_copies_other_programs_keys_but_never_replaces_one_already_here()
    {
        using var t = new TempRoot();
        var paths = WinFormsTestSupport.LegacyIn(t,
            "{ \"ClaudeApiKeyEncrypted\": \"" + WinFormsTestSupport.Dpapi("pm-claude") + "\", \"GeminiApiKeyEncrypted\": \"" + WinFormsTestSupport.Dpapi("pm-gemini") + "\" }");
        var hub = WinFormsTestSupport.Hub(t);
        var s = new AiSettingsSession(hub);

        var found = AiSettingsSession.Discover(paths);
        var plan = AiSettingsSession.DescribeImport(found, p => p == AiProviderType.Claude);
        Assert.True(plan.AnyToCopy);
        Assert.Contains("Claude (Anthropic): already has a key here, kept", plan.Text);
        Assert.Contains("Gemini (Google): will be copied", plan.Text);
        Assert.Contains(paths.PreseMaker, plan.Text);
        Assert.False(AiSettingsSession.DescribeImport(found, _ => true).AnyToCopy);

        var added = s.Import(new Dictionary<AiProviderType, string> { [AiProviderType.Claude] = "typed-here" }, found);

        Assert.Equal(new[] { AiProviderType.Gemini }, added);
        Assert.Equal("typed-here", hub.Credentials.KeyFor(AiProviderType.Claude));
        Assert.Equal("pm-gemini", hub.Credentials.KeyFor(AiProviderType.Gemini));
        Assert.Contains("import from PreseMaker", s.SavedNote(AiProviderType.Gemini));
        Assert.True(File.Exists(paths.PreseMaker));                    // the old file is untouched
    }
}

public class UsageReportTests
{
    [Fact]
    public void Report_figures_match_the_ledger_summary_and_are_filtered_by_program()
    {
        using var t = new TempRoot();
        var hub = WinFormsTestSupport.Hub(t);
        var month = DateTime.Now.ToString("yyyy-MM");
        var day = DateTime.UtcNow;
        WinFormsTestSupport.Ledger(hub, day, "PreseMaker", AiProviderType.Claude, "claude-opus-5", 1000, 200, 0.02);
        WinFormsTestSupport.Ledger(hub, day, "PreseMaker", AiProviderType.Claude, "claude-opus-5", 500, 100, 0.01, ok: false);
        WinFormsTestSupport.Ledger(hub, day, "AuthorPlus", AiProviderType.Gemini, "models/gemini-2.5-flash", 3000, 900, null, cancelled: true);

        var all = UsageReport.Build(hub, month, null);
        var summary = hub.Usage.Summarize(UsageReport.ParseMonth(month));
        Assert.Equal(summary.Totals.Prompts, all.Totals.Prompts);
        Assert.Equal("3", all.PromptsText);
        Assert.Equal(UsageReport.Number(4500), all.TokensInText);
        Assert.Equal("$0.03", all.CostText);
        Assert.Equal("1 failed, 1 stopped", all.ProblemsText);
        Assert.Equal(new[] { "PreseMaker", "AuthorPlus" }, all.ByApp.Select(r => r.Name));
        Assert.Contains(all.ByModel, r => r.Name == "Claude (Anthropic) · claude-opus-5" && r.Prompts == "2" && r.Cost == "$0.0300");
        Assert.Single(all.ByDay);
        Assert.Equal(3, all.Recent.Count);
        Assert.Contains(all.Recent, c => c.Result == "stopped by the user" && c.Cost == "unknown");
        Assert.Contains(all.Recent, c => c.Result == "failed: key not accepted");
        Assert.Equal(4, all.Csv().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        var one = UsageReport.Build(hub, month, "authorplus");
        Assert.Equal("1", one.PromptsText);
        Assert.Single(one.Recent);
        Assert.Equal("–", UsageReport.Build(hub, "2001-01", null).AverageText);
    }

    [Fact]
    public void Budget_line_appears_for_the_current_month_only_and_says_when_it_is_passed()
    {
        using var t = new TempRoot();
        var hub = WinFormsTestSupport.Hub(t);
        var month = DateTime.Now.ToString("yyyy-MM");
        Assert.Null(UsageReport.Build(hub, month, null).BudgetLine);

        hub.Settings.MonthlyBudgetUsd = 0.05;
        hub.SaveSettings();
        WinFormsTestSupport.Ledger(hub, DateTime.UtcNow, "A", AiProviderType.Claude, "claude-opus-5", 1, 1, 0.04);
        var under = UsageReport.Build(hub, month, null);
        Assert.Contains("$0.05", under.BudgetLine);
        Assert.False(under.OverBudget);

        WinFormsTestSupport.Ledger(hub, DateTime.UtcNow, "A", AiProviderType.Claude, "claude-opus-5", 1, 1, 0.04);
        var over = UsageReport.Build(hub, month, null);
        Assert.True(over.OverBudget);
        Assert.Contains("over the warning", over.BudgetLine);
        Assert.Null(UsageReport.Build(hub, "2001-01", null).BudgetLine);
    }

    [Fact]
    public void Apps_lists_every_program_in_any_month_plus_the_calling_one()
    {
        using var t = new TempRoot();
        var hub = WinFormsTestSupport.Hub(t);
        WinFormsTestSupport.Ledger(hub, new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc), "Zeta", AiProviderType.Claude, "m", 1, 1, 0);
        WinFormsTestSupport.Ledger(hub, DateTime.UtcNow, "alpha", AiProviderType.Claude, "m", 1, 1, 0);
        Assert.Equal(new[] { "alpha", "PreseMaker", "Zeta" }, UsageReport.Apps(hub.Usage, "PreseMaker"));
    }

    [Fact]
    public void Purposes_errors_and_small_costs_read_in_plain_words()
    {
        Assert.Equal("Test button in AI Settings", UsageReport.PurposeWords("connection-test"));
        Assert.Equal("draft-quiz", UsageReport.PurposeWords("draft-quiz"));
        Assert.Equal("(not named by the program)", UsageReport.PurposeWords("(unnamed)"));
        Assert.Equal("over the limit or out of credit", UsageReport.ErrorWords("RateLimit"));
        Assert.Equal("reason not recorded", UsageReport.ErrorWords(null));

        using var t = new TempRoot();
        var hub = WinFormsTestSupport.Hub(t);
        WinFormsTestSupport.Ledger(hub, DateTime.UtcNow, "A", AiProviderType.Claude, "claude-opus-5", 10, 5, 0.0042);
        Assert.Equal("$0.0042", UsageReport.Build(hub, DateTime.Now.ToString("yyyy-MM"), null).CostText);   // not "$0.00"
    }

    [Fact]
    public void An_unknown_model_is_explained_as_one()
    {
        var ex = new AiException(AiErrorKind.ProviderError, AiProviderType.Claude, "model: claude-nope not found", statusCode: 404);
        Assert.Contains("Press Models", AiSettingsSession.Explain(ex));
    }

    [Theory]
    [InlineData("Claude/claude-opus-5", "Claude (Anthropic) · claude-opus-5")]
    [InlineData("Gemini/models/gemini-2.5-flash", "Gemini (Google) · models/gemini-2.5-flash")]
    [InlineData("Unknown/x", "Unknown/x")]
    [InlineData("noslash", "noslash")]
    public void Model_keys_read_as_provider_and_model(string key, string expected) =>
        Assert.Equal(expected, UsageReport.ModelName(key));
}

public class WinFormsFormTests
{
    [Fact]
    public void Settings_form_shows_a_row_per_provider_with_the_stored_keys_masked()
    {
        using var t = new TempRoot();
        WinFormsTestSupport.Sta(() =>
        {
            var hub = WinFormsTestSupport.Hub(t);
            hub.Credentials.SetKey(AiProviderType.Claude, "sk-ant-stored", "AuthorPlus");
            hub.Credentials.Save();

            using var form = new AiSettingsForm(hub);
            Assert.Equal(AiProviderRouter.AllProviders.Count, form.ProviderRowCount);
            Assert.Equal("sk-ant-stored", form.KeyText(AiProviderType.Claude));
            Assert.Contains("by AuthorPlus", form.StatusText(AiProviderType.Claude));
            Assert.Contains("No key yet", form.StatusText(AiProviderType.OpenAi));
            Assert.True(form.KeyMasked(AiProviderType.Claude));
            form.SetShow(AiProviderType.Claude, true);
            Assert.False(form.KeyMasked(AiProviderType.Claude));
            Assert.False(form.Dirty);
            form.SetKeyText(AiProviderType.Mistral, "new");
            Assert.True(form.Dirty);
            Assert.Contains("Key changed", form.StatusText(AiProviderType.Mistral));
            Assert.Contains("PreseMaker", form.Text);
        });
    }

    [Fact]
    public void Settings_form_Test_marks_the_row_and_Import_fills_empty_rows()
    {
        using var t = new TempRoot();
        var paths = WinFormsTestSupport.LegacyIn(t, "{ \"OpenAiApiKeyEncrypted\": \"" + WinFormsTestSupport.Dpapi("pm-openai") + "\" }");
        WinFormsTestSupport.Sta(async () =>
        {
            using var form = new AiSettingsForm(WinFormsTestSupport.Hub(t)) { LegacyPaths = paths };
            form.SetKeyText(AiProviderType.Claude, "sk-ant-typed");
            await form.TestForTests(AiProviderType.Claude);
            Assert.StartsWith("Works.", form.StatusText(AiProviderType.Claude));
            Assert.Equal(Color.FromArgb(16, 124, 16), form.DotColor(AiProviderType.Claude));
            form.SetKeyText(AiProviderType.Claude, "sk-ant-edited");          // an edit un-proves the test
            Assert.DoesNotContain("Works", form.StatusText(AiProviderType.Claude));
            Assert.NotEqual(Color.FromArgb(16, 124, 16), form.DotColor(AiProviderType.Claude));

            var added = form.ImportForTests();
            Assert.Equal(new[] { AiProviderType.OpenAi }, added);
            Assert.Equal("pm-openai", form.KeyText(AiProviderType.OpenAi));
            Assert.Contains("Press Save", form.FooterText);
        });
    }

    [Fact]
    public void Settings_form_failed_Test_turns_the_row_red_with_advice()
    {
        using var t = new TempRoot();
        WinFormsTestSupport.Sta(async () =>
        {
            using var form = new AiSettingsForm(WinFormsTestSupport.Hub(t, new AiException(AiErrorKind.Auth, AiProviderType.Gemini, "401")));
            form.SetKeyText(AiProviderType.Gemini, "bad");
            await form.TestForTests(AiProviderType.Gemini);
            Assert.Contains("did not accept this key", form.StatusText(AiProviderType.Gemini));
            Assert.Equal(Color.FromArgb(180, 30, 30), form.DotColor(AiProviderType.Gemini));
        });
    }

    [Fact]
    public void Usage_form_shows_the_months_figures_and_can_start_on_one_program()
    {
        using var t = new TempRoot();
        WinFormsTestSupport.Sta(() =>
        {
            var hub = WinFormsTestSupport.Hub(t);
            WinFormsTestSupport.Ledger(hub, DateTime.UtcNow, "PreseMaker", AiProviderType.Claude, "claude-opus-5", 1000, 200, 0.02);
            WinFormsTestSupport.Ledger(hub, DateTime.UtcNow, "AuthorPlus", AiProviderType.Gemini, "models/gemini-2.5-flash", 10, 10, 0.5);

            using (var all = new UsageDashboardForm(hub))
            {
                Assert.Null(all.AppFilter);
                Assert.Equal("2", all.TileText("prompts"));
                Assert.Equal("$0.52", all.TileText("cost"));
                Assert.Equal(2, all.RecentRowCount);
            }

            using var one = new UsageDashboardForm(hub, "PreseMaker");
            Assert.Equal("PreseMaker", one.AppFilter);
            Assert.Equal("1", one.TileText("prompts"));
            Assert.Equal(1, one.RecentRowCount);
        });
    }
}
