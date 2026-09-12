using Eaglin.AiManager.Prompts;
using Eaglin.AiManager.Usage;

namespace Eaglin.AiManager.Tests;

public class UsageLedgerTests
{
    private static UsageRecord Rec(DateTime t, string app, AiProviderType p, string model, int inTok, int outTok, double? cost, bool ok = true, bool cancelled = false, long ms = 100) =>
        new() { T = t, App = app, Provider = p, Model = model, Purpose = "x", InTok = inTok, OutTok = outTok, CostUsd = cost, Ok = ok, Cancelled = cancelled, Ms = ms, Error = ok || cancelled ? null : "Auth" };

    [Fact]
    public void Appends_to_the_month_file_of_the_record_and_reads_it_back()
    {
        using var t = new TempRoot();
        var ledger = new UsageLedger(t.Path);
        var june = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var july = new DateTime(2026, 7, 1, 0, 30, 0, DateTimeKind.Local).ToUniversalTime();

        ledger.Append(Rec(june, "AuthorPlus", AiProviderType.Claude, "claude-opus-5", 100, 50, 0.002));
        ledger.Append(Rec(july, "AuthorPlus", AiProviderType.Claude, "claude-opus-5", 10, 5, 0.0002));

        Assert.True(File.Exists(ledger.PathFor(new DateTime(2026, 6, 1))));
        Assert.True(File.Exists(ledger.PathFor(new DateTime(2026, 7, 1))));
        var j = Assert.Single(ledger.Read(new DateTime(2026, 6, 1)));
        Assert.Equal(100, j.InTok);
        Assert.Equal(AiProviderType.Claude, j.Provider);
        Assert.Contains("\"Provider\":\"Claude\"", File.ReadAllText(ledger.PathFor(new DateTime(2026, 6, 1))));
    }

    [Fact]
    public void Summarize_groups_by_app_model_and_day_and_filters_by_app()
    {
        using var t = new TempRoot();
        var ledger = new UsageLedger(t.Path);
        var d1 = new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var d2 = new DateTime(2026, 8, 4, 9, 0, 0, DateTimeKind.Local).ToUniversalTime();

        ledger.Append(Rec(d1, "AuthorPlus", AiProviderType.Claude, "claude-opus-5", 100, 50, 0.5, ms: 200));
        ledger.Append(Rec(d1, "AuthorPlus", AiProviderType.Gemini, "gemini-2.5-flash", 10, 5, 0.1, ms: 100));
        ledger.Append(Rec(d2, "CIATLE", AiProviderType.Claude, "claude-opus-5", 30, 20, 0.25, ms: 300));
        ledger.Append(Rec(d2, "CIATLE", AiProviderType.OpenAi, "gpt-4o", 0, 0, null, ok: false));
        ledger.Append(Rec(d2, "CIATLE", AiProviderType.OpenAi, "gpt-4o", 5, 0, null, ok: false, cancelled: true));
        File.AppendAllText(ledger.PathFor(new DateTime(2026, 8, 1)), "{ torn line\n");

        var s = ledger.Summarize(new DateTime(2026, 8, 1));
        Assert.Equal("2026-08", s.Month);
        Assert.Equal(5, s.Totals.Prompts);
        Assert.Equal(3, s.Totals.Succeeded);
        Assert.Equal(1, s.Totals.Failed);
        Assert.Equal(1, s.Totals.Cancelled);
        Assert.Equal(145, s.Totals.InputTokens);
        Assert.Equal(75, s.Totals.OutputTokens);
        Assert.Equal(0.85, s.Totals.CostUsd, 10);
        Assert.Equal((200 + 100 + 300 + 100 + 100) / 5.0, s.Totals.AverageMs);

        Assert.Equal(new[] { "AuthorPlus", "CIATLE" }, s.ByApp.Select(g => g.Key));          // by cost desc
        Assert.Equal("Claude/claude-opus-5", s.ByModel[0].Key);
        Assert.Equal(0.75, s.ByModel[0].CostUsd, 10);
        Assert.Equal(new[] { "2026-08-03", "2026-08-04" }, s.ByDay.Select(g => g.Key));
        Assert.Equal(2, s.ByDay[0].Prompts);

        var only = ledger.Summarize(new DateTime(2026, 8, 1), "ciatle");
        Assert.Equal(3, only.Totals.Prompts);
        Assert.Equal("CIATLE", only.ByApp.Single().Key);
    }

    [Fact]
    public void Recent_returns_newest_first_from_this_and_last_month()
    {
        using var t = new TempRoot();
        var ledger = new UsageLedger(t.Path);
        var now = DateTime.UtcNow;
        ledger.Append(Rec(now.AddMinutes(-5), "A", AiProviderType.Claude, "m", 1, 1, 0));
        ledger.Append(Rec(now.AddMinutes(-1), "B", AiProviderType.Claude, "m", 2, 2, 0));
        ledger.Append(Rec(now.AddMonths(-1), "A", AiProviderType.Claude, "m", 3, 3, 0));
        ledger.Append(Rec(now.AddMonths(-3), "A", AiProviderType.Claude, "m", 4, 4, 0));   // too old

        var recent = ledger.Recent(10);
        Assert.Equal(new[] { 2, 1, 3 }, recent.Select(r => r.InTok));
        Assert.Equal(new[] { 1, 3 }, ledger.Recent(10, "A").Select(r => r.InTok));
    }
}

public class PromptTemplateTests
{
    [Fact]
    public void Placeholders_are_listed_once_in_order_and_bound_from_objects_or_dictionaries()
    {
        var t = new PromptTemplate("chapter-summary", "You summarise {genre} fiction.", "Title: {title}\n\n{text}\n\nAgain: {title}", maxTokens: 800);
        Assert.Equal(new[] { "genre", "title", "text" }, t.Placeholders);

        var r = t.Bind(new { genre = "thriller", title = "The Message", text = "Once..." }, AiProviderType.Claude);
        Assert.Equal("You summarise thriller fiction.", r.System);
        Assert.Equal("Title: The Message\n\nOnce...\n\nAgain: The Message", r.User);
        Assert.Equal(800, r.MaxTokens);
        Assert.Equal("chapter-summary", r.Purpose);
        Assert.Equal(AiProviderType.Claude, r.Provider);

        var d = t.Bind(new Dictionary<string, string> { ["genre"] = "g", ["title"] = "t", ["text"] = "x" }, maxTokens: 10);
        Assert.Equal(10, d.MaxTokens);
    }

    [Fact]
    public void Missing_values_throw_naming_the_placeholders()
    {
        var t = new PromptTemplate("x", "", "{a} and {b}");
        var ex = Assert.Throws<ArgumentException>(() => t.Bind(new { a = 1 }));
        Assert.Contains("b", ex.Message);
        Assert.DoesNotContain("a,", ex.Message);
    }
}

public class PromptLibraryTests
{
    private static PromptTemplate Default(string user = "Summarise: {text}") =>
        new("chapter-summary", "You are an editor.", user, "One-paragraph chapter summary.", 600);

    [Fact]
    public void RegisterDefaults_writes_missing_files_and_Get_reads_them_back()
    {
        using var t = new TempRoot();
        var lib = new PromptLibrary(t.Path, "AuthorPlus");
        lib.RegisterDefaults(new[] { Default() });

        Assert.True(File.Exists(Path.Combine(t.Path, "prompts", "AuthorPlus", "chapter-summary.json")));
        var got = lib.Get("chapter-summary");
        Assert.Equal("You are an editor.", got.System);
        Assert.False(got.UserEdited);
        Assert.Single(lib.List());
        Assert.Throws<KeyNotFoundException>(() => lib.Get("nope"));
    }

    [Fact]
    public void User_edits_survive_new_defaults_but_unedited_files_follow_them()
    {
        using var t = new TempRoot();
        var lib = new PromptLibrary(t.Path, "AuthorPlus");
        lib.RegisterDefaults(new[] { Default(), new PromptTemplate("other", "s", "u") });

        var mine = lib.Get("chapter-summary");
        mine.User = "My own wording: {text}";
        lib.Save(mine);

        var lib2 = new PromptLibrary(t.Path, "AuthorPlus");
        lib2.RegisterDefaults(new[] { Default("Version 2: {text}"), new PromptTemplate("other", "s", "u v2") });

        Assert.Equal("My own wording: {text}", lib2.Get("chapter-summary").User);   // edited → kept
        Assert.True(lib2.Get("chapter-summary").UserEdited);
        Assert.Equal("u v2", lib2.Get("other").User);                                // never edited → updated

        var reset = lib2.ResetToDefault("chapter-summary");
        Assert.Equal("Version 2: {text}", reset.User);
        Assert.False(lib2.Get("chapter-summary").UserEdited);
        Assert.Throws<KeyNotFoundException>(() => lib2.ResetToDefault("user-made"));
    }

    [Fact]
    public void List_includes_user_created_templates_and_Delete_removes_only_those()
    {
        using var t = new TempRoot();
        var lib = new PromptLibrary(t.Path, "AuthorPlus");
        lib.RegisterDefaults(new[] { Default() });
        lib.Save(new PromptTemplate("my-own", "s", "u {x}"));

        Assert.Equal(new[] { "chapter-summary", "my-own" }, lib.List().Select(x => x.Name));
        lib.Delete("my-own");
        lib.Delete("chapter-summary");     // a default is reset, not removed
        Assert.Equal(new[] { "chapter-summary" }, lib.List().Select(x => x.Name));
    }

    [Fact]
    public void Corrupt_file_is_ignored_and_the_default_is_served()
    {
        using var t = new TempRoot();
        var lib = new PromptLibrary(t.Path, "AuthorPlus");
        lib.RegisterDefaults(new[] { Default() });
        File.WriteAllText(lib.PathFor("chapter-summary"), "{ nope");
        Assert.Equal("You are an editor.", lib.Get("chapter-summary").System);
    }
}
