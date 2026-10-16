using Eaglin.AiManager.Usage;

namespace Eaglin.AiManager.Tests;

/// <summary>1.4.0 additions to the library, used by both settings UIs: key pages, ledger months, CSV.</summary>
public class LedgerCsvAndKeyPageTests
{
    [Fact]
    public void Every_provider_has_a_web_page_for_getting_a_key()
    {
        foreach (var p in AiProviderRouter.AllProviders)
        {
            Assert.True(AiProviderRouter.KeyPages.TryGetValue(p, out var url), $"{p} has no key page");
            Assert.StartsWith("https://", url);
        }
    }

    [Fact]
    public void Months_lists_ledger_files_newest_first_and_always_the_current_month()
    {
        using var t = new TempRoot();
        var ledger = new UsageLedger(t.Path);
        Assert.Equal(new[] { DateTime.Now.ToString("yyyy-MM") }, ledger.Months());

        ledger.Append(new UsageRecord { T = new DateTime(2025, 11, 3, 12, 0, 0, DateTimeKind.Utc), App = "A" });
        ledger.Append(new UsageRecord { T = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc), App = "A" });
        File.WriteAllText(Path.Combine(AiPaths.UsageDir(t.Path), "usage-20xx-99.jsonl"), "");   // not a month

        var months = ledger.Months();
        Assert.Equal(DateTime.Now.ToString("yyyy-MM"), months[0]);
        Assert.Equal(new[] { "2026-01", "2025-11" }, months.Skip(1).Where(m => m != DateTime.Now.ToString("yyyy-MM")));
        Assert.DoesNotContain("20xx-99", months);
    }

    [Fact]
    public void Csv_has_a_header_and_quotes_commas_quotes_and_newlines()
    {
        var csv = UsageLedger.ToCsv(new[]
        {
            new UsageRecord { T = DateTime.UtcNow, App = "Pres,eMaker", Provider = AiProviderType.Claude, Model = "claude-opus-5",
                              Purpose = "say \"hi\"\nthen stop", InTok = 1200, OutTok = 34, CostUsd = 0.0125, Ms = 1500, Ok = true },
            new UsageRecord { T = DateTime.UtcNow, App = "X", Provider = AiProviderType.Gemini, Model = "m", Ok = false, Error = "Auth" },
            new UsageRecord { T = DateTime.UtcNow, App = "X", Provider = AiProviderType.Gemini, Model = "m", Cancelled = true }
        });

        var lines = csv.Split('\n');
        Assert.Equal(UsageLedger.CsvHeader, lines[0]);
        Assert.Contains("\"Pres,eMaker\"", csv);
        Assert.Contains("\"say \"\"hi\"\"\nthen stop\"", csv);
        Assert.Contains(",1200,34,0.012500,1500,no,ok", csv);
        Assert.Contains(",Auth,\n", csv);           // the last column, Pictures (1.7.0), is empty for a prompt
        Assert.EndsWith(",cancelled,\n", csv);
    }
}
