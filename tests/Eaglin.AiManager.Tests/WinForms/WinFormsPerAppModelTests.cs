using Eaglin.AiManager.WinForms;

namespace Eaglin.AiManager.Tests.WinForms;

/// <summary>1.4.1: "Model for &lt;app&gt;" in the WinForms AI Settings, and the default-model fix.</summary>
public class WinFormsPerAppModelTests
{
    [Fact]
    public void Session_saves_the_apps_own_model_and_Same_as_all_programs_clears_it()
    {
        using var t = new TempRoot();
        var s = new AiSettingsSession(WinFormsTestSupport.Hub(t));
        Assert.True(s.ShowsAppModels);
        var builtIn = AiProviderRouter.DefaultModels[AiProviderType.Claude];

        var input = new SettingsInput();
        input.Models[AiProviderType.Claude] = "claude-opus-5";
        input.AppModels[AiProviderType.Claude] = builtIn;                  // the built-in default, chosen on purpose
        input.AppModels[AiProviderType.Gemini] = "models/gemini-2.5-pro";
        s.Save(input);

        var pm = AiHub.Open("PreseMaker", t.Path);
        Assert.Equal(builtIn, pm.AppModel(AiProviderType.Claude));            // it sticks
        Assert.Equal(builtIn, pm.DefaultModel(AiProviderType.Claude));
        Assert.Equal("models/gemini-2.5-pro", pm.DefaultModel(AiProviderType.Gemini));
        Assert.Equal("claude-opus-5", AiHub.Open("AuthorPlus", t.Path).DefaultModel(AiProviderType.Claude));
        Assert.Equal(builtIn, s.AppModelFor(AiProviderType.Claude));

        var clear = new SettingsInput();
        clear.AppModels[AiProviderType.Claude] = null;                     // Same as all programs
        s.Save(clear);
        pm = AiHub.Open("PreseMaker", t.Path);
        Assert.Null(pm.AppModel(AiProviderType.Claude));
        Assert.Equal("claude-opus-5", pm.DefaultModel(AiProviderType.Claude));
        Assert.Equal("models/gemini-2.5-pro", pm.AppModel(AiProviderType.Gemini)); // a provider left out is not changed
    }

    [Fact]
    public void Picking_the_built_in_default_for_all_programs_sticks_but_an_untouched_box_does_not_pin_it()
    {
        using var t = new TempRoot();
        var s = new AiSettingsSession(WinFormsTestSupport.Hub(t));
        var builtIn = AiProviderRouter.DefaultModels[AiProviderType.Claude];
        var geminiBuiltIn = AiProviderRouter.DefaultModels[AiProviderType.Gemini];

        var input = new SettingsInput();
        input.Models[AiProviderType.Claude] = builtIn;
        input.Models[AiProviderType.Gemini] = geminiBuiltIn;
        input.ModelsChosen.Add(AiProviderType.Claude);                     // the user picked it; Gemini was left alone
        s.Save(input);

        var back = AiHub.Open("AuthorPlus", t.Path).Settings;
        Assert.Equal(builtIn, back.ModelFor(AiProviderType.Claude));
        Assert.Null(back.ModelFor(AiProviderType.Gemini));
    }

    [Fact]
    public void Standalone_session_offers_no_app_model()
    {
        using var t = new TempRoot();
        Assert.False(new AiSettingsSession(WinFormsTestSupport.Hub(t, app: AiHub.ManagerAppName)).ShowsAppModels);
    }

    [Fact]
    public void Form_shows_Model_for_the_app_with_Same_as_all_programs_first_and_saves_a_pick()
    {
        using var t = new TempRoot();
        WinFormsTestSupport.Sta(() =>
        {
            var hub = WinFormsTestSupport.Hub(t);
            hub.SetAppModel(AiProviderType.Gemini, "gemini-custom-typed-elsewhere");

            using (var form = new AiSettingsForm(hub))
            {
                Assert.True(form.ShowsAppModels);
                var choices = form.AppModelChoices(AiProviderType.Claude);
                Assert.Equal("Same as all programs", choices[0]);
                Assert.Contains(AiProviderRouter.DefaultModels[AiProviderType.Claude], choices);
                Assert.Null(form.AppModelText(AiProviderType.Claude));
                Assert.Equal("gemini-custom-typed-elsewhere", form.AppModelText(AiProviderType.Gemini)); // kept though not in the list
                Assert.False(form.Dirty);

                form.PickAppModel(AiProviderType.Claude, "claude-sonnet-4-6");
                form.PickAppModel(AiProviderType.Gemini, null);
                Assert.True(form.Dirty);
                Assert.True(form.SaveForTests());
            }

            var pm = AiHub.Open("PreseMaker", t.Path);
            Assert.Equal("claude-sonnet-4-6", pm.AppModel(AiProviderType.Claude));
            Assert.Null(pm.AppModel(AiProviderType.Gemini));
            Assert.Null(pm.Settings.ModelFor(AiProviderType.Claude));        // untouched: the built-in default is not pinned
        });
    }

    [Fact]
    public void Form_Test_checks_the_model_this_program_will_use()
    {
        using var t = new TempRoot();
        WinFormsTestSupport.Sta(async () =>
        {
            var hub = WinFormsTestSupport.Hub(t);
            using var form = new AiSettingsForm(hub);
            form.SetKeyText(AiProviderType.Claude, "sk-ant-typed");
            form.PickAppModel(AiProviderType.Claude, "claude-sonnet-4-6");
            await form.TestForTests(AiProviderType.Claude);
            Assert.Contains("using claude-sonnet-4-6, the model for PreseMaker.", form.StatusText(AiProviderType.Claude));
            Assert.Equal("claude-sonnet-4-6", Assert.Single(hub.Usage.Read(DateTime.Now)).Model);
        });
    }

    [Fact]
    public void Form_picking_the_model_already_shown_for_all_programs_sticks()
    {
        using var t = new TempRoot();
        WinFormsTestSupport.Sta(() =>
        {
            var builtIn = AiProviderRouter.DefaultModels[AiProviderType.Claude];
            using (var form = new AiSettingsForm(WinFormsTestSupport.Hub(t)))
            {
                form.PickModel(AiProviderType.Claude, builtIn);
                Assert.True(form.SaveForTests());
            }
            Assert.Equal(builtIn, AiHub.Open("X", t.Path).Settings.ModelFor(AiProviderType.Claude));
        });
    }

    [Fact]
    public void Form_opened_by_the_standalone_program_has_no_app_model_column()
    {
        using var t = new TempRoot();
        WinFormsTestSupport.Sta(() =>
        {
            using var form = new AiSettingsForm(WinFormsTestSupport.Hub(t, app: AiHub.ManagerAppName));
            Assert.False(form.ShowsAppModels);
            Assert.True(form.SaveForTests());
            Assert.False(AiHub.Open("X", t.Path).Settings.Apps.ContainsKey(AiHub.ManagerAppName));
        });
    }
}
