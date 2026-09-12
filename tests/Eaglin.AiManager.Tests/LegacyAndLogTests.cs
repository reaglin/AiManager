using System.Security.Cryptography;
using System.Text;
using Eaglin.AiManager.Legacy;
using Eaglin.AiManager.Store;

namespace Eaglin.AiManager.Tests;

public class LegacyCredentialsTests
{
    private static string Encrypt(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static LegacyPaths PathsIn(TempRoot t) => new()
    {
        PreseMaker = Path.Combine(t.Path, "pm", "app_settings.json"),
        AuthorPlus = Path.Combine(t.Path, "ap", "ai_settings.json"),
        CiatleFce  = Path.Combine(t.Path, "fce", "prefs.json")
    };

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Discovers_keys_from_every_legacy_format_and_skips_files_without_keys()
    {
        using var t = new TempRoot();
        var paths = PathsIn(t);
        WriteFile(paths.PreseMaker,
            "{ \"ClaudeApiKeyEncrypted\": \"" + Encrypt("pm-claude") + "\", \"OpenAiApiKeyEncrypted\": \"" + Encrypt("pm-openai") + "\", \"GeminiApiKeyEncrypted\": \"\", \"ClaudeModel\": \"x\" }");
        WriteFile(paths.AuthorPlus,
            "{ \"SelectedProvider\": \"Claude\", \"MistralApiKeyEncrypted\": \"" + Encrypt("ap-mistral") + "\" }");
        WriteFile(paths.CiatleFce,
            "{ \"recentFiles\": [], \"aiSettings\": { \"selectedProvider\": 1, \"claudeApiKey\": \"fce-claude\", \"geminiApiKey\": \"  \" } }");

        var found = LegacyCredentials.Discover(paths);

        Assert.Equal(new[] { "PreseMaker", "AuthorPlus", "CIATLE-FCE" }, found.Select(f => f.App));
        Assert.Equal(new[] { AiProviderType.Claude, AiProviderType.OpenAi }, found[0].Providers);
        Assert.Equal("pm-claude", found[0].Keys[AiProviderType.Claude]);
        Assert.Equal(new[] { AiProviderType.Mistral }, found[1].Providers);
        Assert.Equal(new[] { AiProviderType.Claude }, found[2].Providers);
        Assert.Equal("fce-claude", found[2].Keys[AiProviderType.Claude]);
    }

    [Fact]
    public void Missing_corrupt_or_keyless_files_are_simply_not_listed()
    {
        using var t = new TempRoot();
        var paths = PathsIn(t);
        WriteFile(paths.PreseMaker, "not json");
        WriteFile(paths.CiatleFce, "{ \"AiSettings\": { \"ClaudeApiKey\": \"\" } }");
        Assert.Empty(LegacyCredentials.Discover(paths));
    }

    [Fact]
    public void Import_fills_blank_slots_only_unless_overwrite_and_leaves_the_source_alone()
    {
        using var t = new TempRoot();
        var paths = PathsIn(t);
        WriteFile(paths.PreseMaker,
            "{ \"ClaudeApiKeyEncrypted\": \"" + Encrypt("pm-claude") + "\", \"OpenAiApiKeyEncrypted\": \"" + Encrypt("pm-openai") + "\" }");
        var before = File.ReadAllText(paths.PreseMaker);

        var store = new CredentialStore(t.Path);
        store.SetKey(AiProviderType.Claude, "mine", "me");
        var src = LegacyCredentials.Discover(paths).Single();

        Assert.Equal(1, LegacyCredentials.Import(store, src));
        Assert.Equal("mine", store.KeyFor(AiProviderType.Claude));
        Assert.Equal("pm-openai", store.KeyFor(AiProviderType.OpenAi));
        Assert.Equal("import:PreseMaker", store.EntryFor(AiProviderType.OpenAi)!.UpdatedBy);

        Assert.Equal(2, LegacyCredentials.Import(store, src, overwrite: true));
        Assert.Equal("pm-claude", store.KeyFor(AiProviderType.Claude));
        Assert.Equal(before, File.ReadAllText(paths.PreseMaker));
    }
}

public class ActivityLogTests
{
    [Fact]
    public void Writes_app_tagged_lines_to_the_monthly_file_and_offloads_long_bodies()
    {
        using var t = new TempRoot();
        ActivityLog.Initialize(t.Path, "TestApp");
        ActivityLog.Info("Ctx", "hello");
        ActivityLog.AiCall("Claude/m", "REQUEST  chars=999>>\n" + new string('x', 500));

        var log = File.ReadAllText(ActivityLog.CurrentLogPath);
        Assert.Contains("INFO   TestApp", log);
        Assert.Contains("hello", log);
        Assert.Contains("REQUEST  chars=999 →", log);
        Assert.EndsWith($"activity-{DateTime.Now:yyyy-MM}.log", ActivityLog.CurrentLogPath);

        var prompts = Directory.GetFiles(Path.Combine(t.Path, "logs", "prompts", "TestApp"), "*_REQUEST.prompt");
        Assert.Single(prompts);
        Assert.Contains("App:       TestApp", File.ReadAllText(prompts[0]));
    }

    [Fact]
    public void Concurrent_writers_never_throw_and_lose_nothing()
    {
        using var t = new TempRoot();
        ActivityLog.Initialize(t.Path, "Concurrent");
        Parallel.For(0, 200, i => ActivityLog.Info("T", $"line {i}"));
        var lines = File.ReadAllLines(ActivityLog.CurrentLogPath);
        Assert.Equal(200, lines.Count(l => l.Contains("line ")));
    }
}
