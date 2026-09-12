using Eaglin.AiManager;
using Eaglin.AiManager.Legacy;
using Eaglin.AiManager.Prompts;

// AiManager self-test — the command-line way to manage the shared store and prove the keys
// work, until the settings windows exist (Phase 2).
//
//   dotnet run --project tools/AiManager.SelfTest -- [--root <folder>] <command>
//
//   list                         show the store: which providers have keys, defaults, paths
//   set <Provider> <key>         store a key   (Claude | Gemini | OpenAi | Mistral | XAi)
//   clear <Provider>             remove a key
//   default <Provider>           set the global default provider
//   model <Provider> <model-id>  set the global model for a provider
//   budget <usd|off>             set or clear the monthly budget warning
//   discover                     list keys found in PreseMaker / AuthorPlus / CIATLE-FCE files
//   import [--overwrite]         copy those keys into the shared store
//   test [Provider ...]          send one short prompt to each provider with a key (or the named ones)
//   stream [Provider]            stream a longer answer to the console; Ctrl-C cancels
//   models <Provider> [--refresh]  list the vendor's models (cached unless --refresh)
//   usage [YYYY-MM] [app]        usage summary for a month (default: this month, every app)
//   prompts                      list this tool's prompt templates (shows the library at work)
//
// Nothing here prints a key.

var args_ = args.ToList();
string? root = null;
var ri = args_.IndexOf("--root");
if (ri >= 0 && ri + 1 < args_.Count) { root = args_[ri + 1]; args_.RemoveRange(ri, 2); }

var hub = AiHub.Open("SelfTest", root);
hub.Prompts.RegisterDefaults(new[]
{
    new PromptTemplate("connection-test",
        "You are a connection test. Reply with exactly the word OK and nothing else.",
        "Connection test.", "One-word reply used by the `test` command.", 16),
    new PromptTemplate("stream-sample",
        "You are a novelist's writing assistant. Write vivid, concrete prose.",
        "Write about {words} words describing {subject}. No preamble.",
        "A longer answer used by the `stream` command.", 1200)
});
hub.BudgetExceeded += s => Console.Error.WriteLine($"! Monthly budget exceeded: ${s.SpentUsd:F2} of ${s.BudgetUsd:F2} ({s.Month})");

var cmd = args_.Count == 0 ? "list" : args_[0].ToLowerInvariant();

static AiProviderType Provider(string s) =>
    Enum.TryParse<AiProviderType>(s, ignoreCase: true, out var p) && p != AiProviderType.None
        ? p : throw new ArgumentException($"Unknown provider '{s}'. Use Claude, Gemini, OpenAi, Mistral or XAi.");

static string Money(double? v) => v is null ? "  ?   " : $"${v:F4}";

try
{
    switch (cmd)
    {
        case "list":
            Console.WriteLine($"Store:    {hub.Root}");
            Console.WriteLine($"Log:      {ActivityLog.CurrentLogPath}");
            Console.WriteLine($"Default:  {hub.DefaultProvider} ({AiHub.DisplayName(hub.DefaultProvider)})");
            Console.WriteLine($"Budget:   {(hub.Settings.MonthlyBudgetUsd is { } b ? $"${b:F2}/month" : "none")}   spent this month: ${hub.Usage.SpentThisMonth():F4} (estimated)");
            Console.WriteLine();
            foreach (var p in AiProviderRouter.AllProviders)
            {
                var e = hub.Credentials.EntryFor(p);
                var status = e is null ? "no key" : $"key set {e.UpdatedUtc:yyyy-MM-dd} by {e.UpdatedBy}";
                Console.WriteLine($"  {p,-8} {AiHub.DisplayName(p),-20} model={hub.DefaultModel(p),-26} {status}");
            }
            return 0;

        case "set" when args_.Count == 3:
            hub.Credentials.SetKey(Provider(args_[1]), args_[2], "SelfTest");
            hub.Credentials.Save();
            Console.WriteLine($"Stored the {Provider(args_[1])} key in {hub.Credentials.Path}");
            return 0;

        case "clear" when args_.Count == 2:
            hub.Credentials.SetKey(Provider(args_[1]), "", "SelfTest");
            hub.Credentials.Save();
            Console.WriteLine($"Removed the {Provider(args_[1])} key.");
            return 0;

        case "default" when args_.Count == 2:
            hub.Settings.DefaultProvider = Provider(args_[1]);
            hub.SaveSettings();
            Console.WriteLine($"Default provider is now {hub.Settings.DefaultProvider}.");
            return 0;

        case "model" when args_.Count == 3:
            hub.Settings.SetModel(Provider(args_[1]), args_[2]);
            hub.SaveSettings();
            Console.WriteLine($"{Provider(args_[1])} model is now {args_[2]}.");
            return 0;

        case "budget" when args_.Count == 2:
            hub.Settings.MonthlyBudgetUsd = args_[1].Equals("off", StringComparison.OrdinalIgnoreCase) ? null
                : double.Parse(args_[1], System.Globalization.CultureInfo.InvariantCulture);
            hub.SaveSettings();
            Console.WriteLine(hub.Settings.MonthlyBudgetUsd is { } nb ? $"Monthly budget warning at ${nb:F2}." : "Monthly budget warning off.");
            return 0;

        case "discover":
        {
            var found = LegacyCredentials.Discover();
            if (found.Count == 0) { Console.WriteLine("No legacy key files with keys were found."); return 0; }
            foreach (var s in found)
                Console.WriteLine($"  {s.App,-12} {string.Join(", ", s.Providers),-32} {s.Path}");
            return 0;
        }

        case "import":
        {
            var overwrite = args_.Contains("--overwrite");
            var found = LegacyCredentials.Discover();
            int total = 0;
            foreach (var s in found)
            {
                var n = LegacyCredentials.Import(hub.Credentials, s, overwrite);
                Console.WriteLine($"  {s.App,-12} {n} key(s) imported");
                total += n;
            }
            if (total > 0) hub.Credentials.Save();
            Console.WriteLine(total == 0 ? "Nothing imported (no legacy keys, or every slot already had a key; use --overwrite)." : $"Saved {total} key(s).");
            return 0;
        }

        case "test":
        {
            var targets = args_.Count > 1 ? args_.Skip(1).Select(Provider).ToList() : hub.ProvidersWithKeys.ToList();
            if (targets.Count == 0) { Console.WriteLine("No provider has a key. Use: set <Provider> <key>"); return 1; }

            var req = hub.Prompts.Get("connection-test").Bind();
            var outcomes = await hub.CompleteOnAllAsync(req, targets);
            int failures = 0;
            foreach (var o in outcomes)
            {
                if (o.Succeeded)
                    Console.WriteLine($"  {o.Provider,-8} OK   {o.Response!.Model,-26} in={o.Response.InputTokens} out={o.Response.OutputTokens} {Money(o.Response.EstimatedCostUsd)} {o.Response.Elapsed.TotalSeconds:0.0}s  \"{o.Response.Text.Trim()}\"");
                else
                {
                    failures++;
                    Console.WriteLine($"  {o.Provider,-8} FAIL {o.Error!.Kind}: {o.Error.Message}");
                }
            }
            return failures == 0 ? 0 : 1;
        }

        case "stream":
        {
            var provider = args_.Count > 1 ? Provider(args_[1]) : hub.DefaultProvider;
            if (!hub.IsAvailable(provider)) { Console.WriteLine($"No key for {provider}."); return 1; }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var req = hub.Prompts.Get("stream-sample").Bind(new { words = 300, subject = "a lighthouse keeper's last night on duty" }, provider);
            Console.WriteLine($"Streaming from {provider}/{hub.DefaultModel(provider)} — press Ctrl-C to cancel.\n");

            try
            {
                await foreach (var chunk in hub.StreamAsync(req, cts.Token))
                {
                    if (chunk.IsFinal)
                        Console.WriteLine($"\n\n[done: in={chunk.InputTokens} out={chunk.OutputTokens} stop={chunk.StopReason}]");
                    else
                        Console.Write(chunk.Text);
                }
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("\n\n[cancelled — the partial call is in the usage ledger]");
                return 130;
            }
        }

        case "models" when args_.Count >= 2:
        {
            var provider = Provider(args_[1]);
            var refresh  = args_.Contains("--refresh");
            var list = await hub.Models.ListAsync(provider, refresh);
            Console.WriteLine(list.FromFallback
                ? $"{provider}: built-in fallback list (no key, or fetch failed)"
                : $"{provider}: {list.Models.Count} models, fetched {list.FetchedUtc:yyyy-MM-dd HH:mm} UTC{(refresh ? " (refreshed)" : " (cached)")}");
            foreach (var m in list.Models)
                Console.WriteLine($"  {m.Id,-40} {m.DisplayName,-30} {(m.Created is { } c ? c.ToString("yyyy-MM-dd") : "")}");
            return 0;
        }

        case "usage":
        {
            var month = args_.Count > 1 && DateTime.TryParseExact(args_[1] + "-01", "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var m) ? m : DateTime.Now;
            var app   = args_.Count > 2 ? args_[2] : null;
            var s = hub.Usage.Summarize(month, app);
            var tt = s.Totals;
            Console.WriteLine($"Usage {s.Month}{(app is null ? "" : $" for {app}")}: {tt.Prompts} prompts ({tt.Succeeded} ok, {tt.Failed} failed, {tt.Cancelled} cancelled), " +
                              $"in={tt.InputTokens:N0} out={tt.OutputTokens:N0} tokens, est. cost ${tt.CostUsd:F4}, avg {tt.AverageMs:0} ms");
            Console.WriteLine("\n  By app:");
            foreach (var g in s.ByApp)   Console.WriteLine($"    {g.Key,-24} {g.Prompts,5} prompts  in={g.InputTokens,8:N0} out={g.OutputTokens,8:N0}  ${g.CostUsd:F4}");
            Console.WriteLine("  By model:");
            foreach (var g in s.ByModel) Console.WriteLine($"    {g.Key,-40} {g.Prompts,5} prompts  in={g.InputTokens,8:N0} out={g.OutputTokens,8:N0}  ${g.CostUsd:F4}");
            Console.WriteLine("  By day:");
            foreach (var g in s.ByDay)   Console.WriteLine($"    {g.Key,-12} {g.Prompts,5} prompts  ${g.CostUsd:F4}");
            Console.WriteLine("\n  Recent:");
            foreach (var r in hub.Usage.Recent(10, app))
                Console.WriteLine($"    {r.T.ToLocalTime():MM-dd HH:mm}  {r.App,-12} {r.Provider,-8} {r.Model,-26} {r.Purpose,-18} in={r.InTok,6} out={r.OutTok,5} {Money(r.CostUsd)} {r.Ms,6} ms  {(r.Cancelled ? "cancelled" : r.Ok ? "ok" : r.Error)}");
            return 0;
        }

        case "prompts":
            foreach (var p in hub.Prompts.List())
                Console.WriteLine($"  {p.Name,-20} {(p.UserEdited ? "edited " : "default")}  placeholders: {string.Join(", ", p.Placeholders)}  — {p.Description}\n    {hub.Prompts.PathFor(p.Name)}");
            return 0;

        default:
            Console.WriteLine("Usage: [--root <folder>] list | set <Provider> <key> | clear <Provider> | default <Provider> | model <Provider> <id> | budget <usd|off> | discover | import [--overwrite] | test [Provider ...] | stream [Provider] | models <Provider> [--refresh] | usage [YYYY-MM] [app] | prompts");
            return 2;
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (AiException ex)
{
    Console.Error.WriteLine($"{ex.Kind}: {ex.Message}");
    return 1;
}
