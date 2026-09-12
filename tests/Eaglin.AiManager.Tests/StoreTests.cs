using Eaglin.AiManager.Store;

namespace Eaglin.AiManager.Tests;

public class CredentialStoreTests
{
    [Fact]
    public void Keys_round_trip_and_are_never_on_disk_in_plain_text()
    {
        using var t = new TempRoot();
        var store = new CredentialStore(t.Path);
        Assert.False(store.FileExists);
        Assert.Empty(store.ProvidersWithKeys);

        store.SetKey(AiProviderType.Claude, "sk-ant-secret-123", "AuthorPlus");
        store.SetKey(AiProviderType.XAi, "xai-secret", "SelfTest");
        store.Save();

        var raw = File.ReadAllText(store.Path);
        Assert.DoesNotContain("sk-ant-secret-123", raw);
        Assert.DoesNotContain("xai-secret", raw);
        Assert.Contains("\"Claude\"", raw);
        Assert.Contains("ApiKeyEncrypted", raw);

        var back = new CredentialStore(t.Path);
        Assert.Equal("sk-ant-secret-123", back.KeyFor(AiProviderType.Claude));
        Assert.Equal("xai-secret", back.KeyFor(AiProviderType.XAi));
        Assert.Equal(string.Empty, back.KeyFor(AiProviderType.Gemini));
        Assert.Equal(new[] { AiProviderType.Claude, AiProviderType.XAi }, back.ProvidersWithKeys);
        Assert.Equal("AuthorPlus", back.EntryFor(AiProviderType.Claude)!.UpdatedBy);
    }

    [Fact]
    public void Blank_key_removes_the_entry()
    {
        using var t = new TempRoot();
        var store = new CredentialStore(t.Path);
        store.SetKey(AiProviderType.OpenAi, "k", "x");
        store.SetKey(AiProviderType.OpenAi, "   ", "x");
        store.Save();
        Assert.Empty(new CredentialStore(t.Path).ProvidersWithKeys);
    }

    [Fact]
    public void Corrupt_or_foreign_file_is_an_empty_store_not_an_exception()
    {
        using var t = new TempRoot();
        File.WriteAllText(AiPaths.CredentialsFile(t.Path), "{ nope");
        Assert.Empty(new CredentialStore(t.Path).ProvidersWithKeys);

        File.WriteAllText(AiPaths.CredentialsFile(t.Path),
            "{ \"SchemaVersion\": 1, \"Providers\": { \"Claude\": { \"ApiKeyEncrypted\": \"not-base64!!\" }, \"Bogus\": { \"ApiKeyEncrypted\": \"\" } } }");
        Assert.Empty(new CredentialStore(t.Path).ProvidersWithKeys);
    }

    [Fact]
    public void Reload_sees_a_key_saved_by_another_instance()
    {
        using var t = new TempRoot();
        var a = new CredentialStore(t.Path);
        var b = new CredentialStore(t.Path);
        b.SetKey(AiProviderType.Mistral, "m", "other-app");
        b.Save();
        Assert.False(a.HasKey(AiProviderType.Mistral));
        a.Reload();
        Assert.True(a.HasKey(AiProviderType.Mistral));
    }
}

public class AiSettingsStoreTests
{
    [Fact]
    public void Resolution_falls_through_app_then_global_then_builtin()
    {
        var s = new AiSettings { DefaultProvider = AiProviderType.Gemini };
        s.SetModel(AiProviderType.Claude, "claude-sonnet-5");
        s.ForApp("AuthorPlus").DefaultProvider = AiProviderType.Claude;
        s.ForApp("AuthorPlus").SetModel(AiProviderType.Claude, "claude-fable-5-1");

        Assert.Equal(AiProviderType.Claude, AiSettingsStore.ResolveProvider(s, "AuthorPlus"));
        Assert.Equal(AiProviderType.Gemini, AiSettingsStore.ResolveProvider(s, "CIATLE"));

        Assert.Equal("claude-fable-5-1", AiSettingsStore.ResolveModel(s, "AuthorPlus", AiProviderType.Claude));
        Assert.Equal("claude-sonnet-5",  AiSettingsStore.ResolveModel(s, "CIATLE",     AiProviderType.Claude));
        Assert.Equal(AiProviderRouter.DefaultModels[AiProviderType.OpenAi], AiSettingsStore.ResolveModel(s, "CIATLE", AiProviderType.OpenAi));
    }

    [Fact]
    public void Settings_round_trip_with_enum_names_not_numbers()
    {
        using var t = new TempRoot();
        var store = new AiSettingsStore(t.Path);
        var s = new AiSettings { DefaultProvider = AiProviderType.XAi, MonthlyBudgetUsd = 25 };
        s.SetModel(AiProviderType.XAi, "grok-4");
        s.PriceOverrides["grok-4"] = new ModelPrice(3, 15);
        s.ForApp("Statistle").DefaultProvider = AiProviderType.Mistral;
        store.Save(s);

        var raw = File.ReadAllText(store.Path);
        Assert.Contains("\"XAi\"", raw);
        Assert.DoesNotContain("\"DefaultProvider\": 5", raw);

        var back = store.Load();
        Assert.Equal(AiProviderType.XAi, back.DefaultProvider);
        Assert.Equal(25, back.MonthlyBudgetUsd);
        Assert.Equal("grok-4", back.ModelFor(AiProviderType.XAi));
        Assert.Equal(15, back.PriceOverrides["grok-4"].OutputPerMTok);
        Assert.Equal(AiProviderType.Mistral, back.Apps["statistle"].DefaultProvider);   // app names are case-insensitive
    }

    [Fact]
    public void Missing_or_corrupt_settings_yield_defaults()
    {
        using var t = new TempRoot();
        var store = new AiSettingsStore(t.Path);
        Assert.Equal(AiProviderType.Claude, store.Load().DefaultProvider);
        File.WriteAllText(store.Path, "{ nope");
        Assert.Equal(AiProviderType.Claude, store.Load().DefaultProvider);
    }
}
