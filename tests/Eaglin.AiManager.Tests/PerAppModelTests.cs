using System.Text.Json;
using Eaglin.AiManager.Store;

namespace Eaglin.AiManager.Tests;

/// <summary>1.4.1: a program's own model per provider ("Model for &lt;app&gt;").</summary>
public class PerAppModelTests
{
    private static AiHub FakeHub(TempRoot t, string app = "PreseMaker")
    {
        var hub = AiHub.Open(app, t.Path);
        hub.ProviderFactory = (type, key, model) => new FakeProvider(type, model);
        hub.Credentials.SetKey(AiProviderType.Claude, "sk-ant-fake", "test");
        return hub;
    }

    private static Task<AiResponse> Ask(AiHub hub, string? model = null) =>
        hub.CompleteAsync(new AiRequest { Provider = AiProviderType.Claude, Model = model, User = "hi", Purpose = "unit" });

    [Fact]
    public async Task A_request_without_a_model_uses_the_apps_own_then_all_programs_then_the_built_in_default()
    {
        using var t = new TempRoot();
        var hub = FakeHub(t);

        // 3. nothing chosen: the built-in default
        Assert.Equal(AiProviderRouter.DefaultModels[AiProviderType.Claude], (await Ask(hub)).Model);
        Assert.Equal(ModelSource.BuiltIn, hub.DefaultModelSource(AiProviderType.Claude));

        // 2. a model for all programs
        hub.Settings.SetModel(AiProviderType.Claude, "claude-all-programs");
        Assert.Equal("claude-all-programs", (await Ask(hub)).Model);
        Assert.Equal(ModelSource.AllPrograms, hub.DefaultModelSource(AiProviderType.Claude));

        // 1. the program's own model wins
        hub.Settings.SetAppModel("PreseMaker", AiProviderType.Claude, "claude-sonnet-4-6");
        Assert.Equal("claude-sonnet-4-6", (await Ask(hub)).Model);
        Assert.Equal(ModelSource.App, hub.DefaultModelSource(AiProviderType.Claude));
        Assert.Equal("claude-sonnet-4-6", hub.AppModel(AiProviderType.Claude));

        // another program follows all programs
        var other = AiHub.Open("AuthorPlus", t.Path);
        other.Settings.SetModel(AiProviderType.Claude, "claude-all-programs");
        other.Settings.SetAppModel("PreseMaker", AiProviderType.Claude, "claude-sonnet-4-6");
        Assert.Equal("claude-all-programs", other.DefaultModel(AiProviderType.Claude));
        Assert.Null(other.AppModel(AiProviderType.Claude));
    }

    [Fact]
    public async Task A_request_that_names_its_model_keeps_it()
    {
        using var t = new TempRoot();
        var hub = FakeHub(t);
        hub.Settings.SetModel(AiProviderType.Claude, "claude-all-programs");
        hub.Settings.SetAppModel("PreseMaker", AiProviderType.Claude, "claude-sonnet-4-6");
        Assert.Equal("claude-explicit", (await Ask(hub, "claude-explicit")).Model);
    }

    [Fact]
    public void SetAppModel_saves_beside_the_apps_provider_and_changes_nothing_else()
    {
        using var t = new TempRoot();
        var store = new AiSettingsStore(t.Path);
        var s = new AiSettings { DefaultProvider = AiProviderType.Gemini, MonthlyBudgetUsd = 7 };
        s.SetModel(AiProviderType.Claude, "claude-opus-5");
        s.ForApp("PreseMaker").DefaultProvider = AiProviderType.Claude;
        s.SetAppModel("AuthorPlus", AiProviderType.Claude, "claude-fable-5-1");
        store.Save(s);

        var hub = AiHub.Open("PreseMaker", t.Path);
        Assert.True(hub.SetAppModel(AiProviderType.Claude, "claude-sonnet-4-6"));

        using (var doc = JsonDocument.Parse(File.ReadAllText(store.Path)))
        {
            var app = doc.RootElement.GetProperty("Apps").GetProperty("PreseMaker");
            Assert.Equal("Claude", app.GetProperty("DefaultProvider").GetString());
            Assert.Equal("claude-sonnet-4-6", app.GetProperty("Models").GetProperty("Claude").GetString());
        }
        var back = store.Load();
        Assert.Equal(AiProviderType.Gemini, back.DefaultProvider);
        Assert.Equal(7, back.MonthlyBudgetUsd);
        Assert.Equal("claude-opus-5", back.ModelFor(AiProviderType.Claude));
        Assert.Equal("claude-fable-5-1", back.AppModelFor("AuthorPlus", AiProviderType.Claude));
        Assert.Equal("claude-sonnet-4-6", back.AppModelFor("PreseMaker", AiProviderType.Claude));
        Assert.Equal("claude-sonnet-4-6", hub.DefaultModel(AiProviderType.Claude));   // the hub sees it at once
    }

    [Fact]
    public void SetAppModel_keeps_a_change_another_program_saved_after_this_one_opened()
    {
        using var t = new TempRoot();
        var hub = AiHub.Open("PreseMaker", t.Path);
        var other = AiHub.Open("AuthorPlus", t.Path);
        other.Settings.MonthlyBudgetUsd = 12;
        other.SaveSettings();

        Assert.True(hub.SetAppModel(AiProviderType.Gemini, "models/gemini-2.5-pro"));

        var back = new AiSettingsStore(t.Path).Load();
        Assert.Equal(12, back.MonthlyBudgetUsd);
        Assert.Equal("models/gemini-2.5-pro", back.AppModelFor("PreseMaker", AiProviderType.Gemini));
    }

    [Fact]
    public void An_explicit_app_model_sticks_even_when_it_is_the_built_in_default()
    {
        using var t = new TempRoot();
        var hub = AiHub.Open("PreseMaker", t.Path);
        var builtIn = AiProviderRouter.DefaultModels[AiProviderType.Claude];
        Assert.True(hub.SetAppModel(AiProviderType.Claude, builtIn));
        Assert.Equal(builtIn, AiHub.Open("PreseMaker", t.Path).AppModel(AiProviderType.Claude));
        Assert.Equal(ModelSource.App, AiHub.Open("PreseMaker", t.Path).DefaultModelSource(AiProviderType.Claude));
    }

    [Fact]
    public void Clearing_the_app_model_removes_only_that_and_an_entry_left_empty()
    {
        using var t = new TempRoot();
        var hub = AiHub.Open("PreseMaker", t.Path);
        hub.SetAppModel(AiProviderType.Claude, "claude-sonnet-4-6");
        hub.SetAppModel(AiProviderType.Gemini, "models/gemini-2.5-pro");

        Assert.True(hub.SetAppModel(AiProviderType.Claude, null));
        Assert.Null(hub.AppModel(AiProviderType.Claude));
        Assert.Equal("models/gemini-2.5-pro", hub.AppModel(AiProviderType.Gemini));

        hub.SetAppModel(AiProviderType.Gemini, "  ");
        Assert.False(new AiSettingsStore(t.Path).Load().Apps.ContainsKey("PreseMaker"));

        // an entry that still holds a provider is kept
        var s = new AiSettings();
        s.ForApp("X").DefaultProvider = AiProviderType.Mistral;
        s.SetAppModel("X", AiProviderType.Mistral, "m");
        s.SetAppModel("X", AiProviderType.Mistral, null);
        Assert.Equal(AiProviderType.Mistral, s.Apps["X"].DefaultProvider);
    }

    [Fact]
    public void SetAppModel_never_writes_over_a_settings_file_it_cannot_read()
    {
        using var t = new TempRoot();
        var hub = AiHub.Open("PreseMaker", t.Path);
        var path = AiPaths.SettingsFile(t.Path);
        File.WriteAllText(path, "{ not json");

        Assert.False(hub.SetAppModel(AiProviderType.Claude, "claude-sonnet-4-6"));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void Settings_a_newer_version_wrote_survive_a_save()
    {
        using var t = new TempRoot();
        var path = AiPaths.SettingsFile(t.Path);
        File.WriteAllText(path, """
            {
              "SchemaVersion": 1,
              "DefaultProvider": "Claude",
              "FutureGlobal": { "a": 1 },
              "Apps": { "PreseMaker": { "DefaultProvider": "Gemini", "Tts": { "Voice": "en-US-Neural2-D" } } }
            }
            """);

        var hub = AiHub.Open("PreseMaker", t.Path);
        Assert.True(hub.SetAppModel(AiProviderType.Claude, "claude-sonnet-4-6"));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, doc.RootElement.GetProperty("FutureGlobal").GetProperty("a").GetInt32());
        var app = doc.RootElement.GetProperty("Apps").GetProperty("PreseMaker");
        Assert.Equal("en-US-Neural2-D", app.GetProperty("Tts").GetProperty("Voice").GetString());
        Assert.Equal("Gemini", app.GetProperty("DefaultProvider").GetString());
        Assert.Equal("claude-sonnet-4-6", app.GetProperty("Models").GetProperty("Claude").GetString());

        // an entry holding only a newer version's settings is not "empty"
        hub.SetAppModel(AiProviderType.Claude, null);
        var back = new AiSettingsStore(t.Path).Load();
        back.Apps["PreseMaker"].DefaultProvider = null;
        back.SetAppModel("PreseMaker", AiProviderType.Claude, null);
        Assert.True(back.Apps.ContainsKey("PreseMaker"));
    }

    [Fact]
    public void The_standalone_program_is_not_opened_for_an_app()
    {
        using var t = new TempRoot();
        Assert.False(AiHub.Open(AiHub.ManagerAppName, t.Path).IsOpenedForAnApp);
        Assert.True(AiHub.Open("PreseMaker", t.Path).IsOpenedForAnApp);
    }
}
