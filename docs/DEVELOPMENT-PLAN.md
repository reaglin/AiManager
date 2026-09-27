# AiManager — development plan (executable breakdown)

Numbered tasks per phase with file targets and acceptance criteria. Task IDs are stable — do
not renumber. Status legend: `[ ]` not started · `[~]` in progress · `[x]` done · ⚠️ built and
tested, waiting for Ron to hand-test (the mark the portfolio's plans use; see `..\CLAUDE.md`).

Runtime: **.NET 10** (`net10.0` library, `net10.0-windows` UI). SDK 10.0.400 on this machine.
Seed code: `..\AuthorPlus\src\AuthorPlus.AI` (copied 2026-09-04 from CIATLE.AICore, current
model ids, DPAPI store, PreseMaker key borrowing, 150 lines of tests).

**Every phase ends with something Ron can run**, not just green tests.

---

## Phase 0 — Library core (from the AuthorPlus seed)

Exit: `Eaglin.AiManager` builds warnings-as-errors, a console self-test sends one prompt to a
configured provider using a key read from `Documents\AiManager\credentials.json`.

| # | Task | Targets | Acceptance | Status |
|---|---|---|---|---|
| 0.1 | Solution (`AiManager.slnx` — the SDK's new XML solution format), projects, `Directory.Build.props` (version, nullable, warnings-as-errors), `.gitignore`, initial commit, GitHub repo `reaglin/AiManager` (private) | root | `dotnet build` clean | [x] |
| 0.2 | Copy AuthorPlus.AI into `src/Eaglin.AiManager`, rename namespaces, drop `UsePreseMakerKeys` (replaced by import) | `src/Eaglin.AiManager/*` | existing AuthorPlus AI tests pass under the new names | [x] |
| 0.3 | `AiProviderType` gains `XAi`; `XAiProvider` over the OpenAI-shaped helper | `Providers/` | fake-handler test | [x] |
| 0.4 | `CredentialStore` — `Documents\AiManager\credentials.json`, DPAPI, temp-then-move, guarded for non-Windows | `Store/CredentialStore.cs` | round-trip test; corrupt file → empty store + log line, never throws | [x] |
| 0.5 | `AiSettingsStore` — `settings.json` with global + per-app defaults, price overrides, budget | `Store/AiSettingsStore.cs` | fall-through resolution tested (app → global → built-in) | [x] |
| 0.6 | `AiHub.Open(app)`, `AiRequest`/`AiResponse`/`AiException`, `CompleteAsync`, `CompleteOnAllAsync` | `AiHub.cs`, `Models/` | fake-provider tests: default resolution, explicit provider, no-key error kind | [x] |
| 0.7 | `ActivityLog` moved to `Documents\AiManager\logs\activity-YYYY-MM.log`, app-tagged, multi-process safe append | `ActivityLog.cs` | two writers interleave without exceptions | [x] |
| 0.8 | `LegacyCredentials` — discover + import from PreseMaker, CIATLE-FCE, CIATLE-PE, AuthorPlus files | `Legacy/` | synthetic-file tests per source; import never deletes the source | [x] |
| 0.9 | Console self-test `tools/AiManager.SelfTest` (`list`, `set`, `clear`, `default`, `model`, `discover`, `import`, `test`) | `tools/` | run 2026-09-12 against Ron's real keys — see notes below | [x] |

## Phase 1 — Streaming, catalog, usage, templates

Exit: a chapter-length request streams into a console with Ctrl-C cancel; the ledger shows it.

| # | Task | Targets | Acceptance | Status |
|---|---|---|---|---|
| 1.1 | `StreamAsync` on `IAiProvider` for all five providers (SSE parsing), `AiChunk`, final chunk carries usage; cancel closes the stream | `Providers/` | fake SSE tests incl. mid-stream cancel and error events | [x] |
| 1.2 | `ModelCatalog` — list-models call per provider, `models.json` cache with fetch time, `refresh` flag, built-in fallback list | `Catalog/` | fake responses parsed; cache used when offline | [x] |
| 1.3 | `Pricing` — embedded table + overrides; `EstimatedCostUsd` on every response | `Catalog/Pricing.cs` | known model → cost; unknown → null, tokens kept | [x] |
| 1.4 | `UsageLedger` — JSONL append per call (ok, error, cancelled), `Summarize(month, app?)` by app / provider+model / day, `Recent(n)` | `Usage/` | rollover at month boundary; summary numbers tested | [x] |
| 1.5 | `PromptTemplate` + `PromptLibrary` — per-app folder, `RegisterDefaults`, `Bind`, `ResetToDefault`, never overwrite user edits | `Prompts/` | placeholder tests; user-edited file survives re-register | [x] |
| 1.6 | Monthly budget: `AiHub` raises a `BudgetExceeded` warning event (soft) | `AiHub.cs` | test | [x] |

## Phase 2 — UIs

Exit: Ron enters his keys once in the standalone app; AuthorPlus (Phase 3) finds them.

| # | Task | Targets | Acceptance | Status |
|---|---|---|---|---|
| 2.1 | `Eaglin.AiManager.Wpf`: `AiSettingsWindow` (rows per provider, test, catalog refresh, default provider, per-app default, legacy import) | `src/Eaglin.AiManager.Wpf/` | hand-test; DPI-crisp | [x] |
| 2.2 | `PromptLibraryWindow` (list, edit, preview with sample values, reset) | same | | [x] |
| 2.3 | `UsageDashboardWindow` — month picker; totals (prompts, tokens in/out, cost, errors, avg ms); by app, by provider/model, by day; recent-calls grid; Copy as CSV; budget line | same | numbers match `UsageLedger.Summarize` on a fixture ledger | [x] |
| 2.4 | `AiRunPanel` control — streaming text area, Stop button, elapsed/tokens footer | same | used by AuthorPlus analysis item | [x] |
| 2.5 | `AiManagerApp` (WPF exe, assembly `AiManager.exe`) hosting 2.1–2.3 as tabs plus a **Try it** tab that streams through the AiRunPanel. No `--selftest` switch: the console tool `tools/AiManager.SelfTest` already covers that and a WPF exe has no console | `src/AiManagerApp/` | Ron's daily "what did I spend" view | [x] |
| 2.6 | `Eaglin.AiManager.WinForms` for PreseMaker (CourseBuilder+) and CIATLE: **`AiSettingsForm`** (row per provider: masked key + Show, status dot, Get a key link, model from the catalog + Models, Test; default provider for all programs and for the calling app; monthly spending warning; Import keys from other programs; Save/Cancel, Cancel forgets tested-but-unsaved keys), **`UsageDashboardForm`** (month + program filters, six tiles, by program / by provider and model / by day with bars, most recent requests, Copy as CSV, budget line), **`AiManagerWinForms.ShowSettings/ShowUsage`**. Built 2026-09-27, **1.4.0**. Prompt Library form and a streaming panel not built — no WinForms consumer needs them yet | `src/Eaglin.AiManager.WinForms/` | 32 WinForms tests green (session, report, forms driven headless on STA); screenshots at 100% and simulated 150%. UX: walkthrough + heuristics — 0 critical, 3 major and 5 minor fixed (Cancel closes without a second question; a changed key clears its old Test result; a link to AI Usage; import says per provider what will be copied or kept; buttons that say their action; plain-word errors, purposes and results; sub-cent totals shown to 4 places; a Close button). **Ron:** open both from a WinForms host (PreseMaker once 4.2 starts) at 100% and at 150%+ scale; press Test with a real key; Import from PreseMaker | ⚠️ |

## Phase 3 — Package and first consumer

Exit: AuthorPlus builds against `Eaglin.AiManager` from `C:\nuget-local` and its own AI project is gone.

| # | Task | Targets | Acceptance | Status |
|---|---|---|---|---|
| 3.1 | `build/pack.ps1` — version from props, pack three packages to `C:\nuget-local`, register feed idempotently; README section | `build/` | fresh clone: `pack.ps1` then AuthorPlus builds | [x] |
| 3.2 | AuthorPlus adoption (tracked in AuthorPlus `docs/DEVELOPMENT-PLAN.md` 1.15): package reference, delete `src/AuthorPlus.AI` and its tests, replace `AiSettingsWindow`, register its templates | AuthorPlus repo | AuthorPlus AI menu works with keys entered only in AiManagerApp | [x] |
| 3.3 | Tag `v1.0.0` | | | [x] |

## Phase 4 — Remaining consumers

| # | Task | Targets | Acceptance | Status |
|---|---|---|---|---|
| 4.1 | EasyPeasyRetirement references the package when its first AI feature starts | that repo | | [ ] |
| 4.2 | PreseMaker: WinForms package (2.6, built in 1.4.0 — `AiManagerWinForms.ShowSettings(this, "PreseMaker")`), replace router + settings form, legacy fallback for one release, per-task routing → `AiRequest.Provider` | PreseMaker repo | its 291 tests stay green | [ ] |
| 4.3 | CIATLE: delete `CIATLE.AICore`, adapter implementing `IAiManager` over `AiHub` | CIATLE repo | FCE narrative drafting unchanged for the user | [ ] |
| 4.4 | Remove legacy fallbacks; `LegacyCredentials` stays as an import button only | all | | [ ] |

## Phase 5 — Text to speech (Ron, 2026-09-12; see PLAN.md §14)

Exit: PreseMaker's narration and AuthorPlus v2's chapter audio both synthesize through the
library with one stored Google Cloud credential.

| # | Task | Targets | Acceptance | Status |
|---|---|---|---|---|
| 5.1 | `credentials.json` `Services` section: Google Cloud service-account JSON stored encrypted; `LegacyCredentials` imports PreseMaker's `TtsCredentialsFilePath` content | `Store/`, `Legacy/` | round-trip + import tests | [ ] |
| 5.2 | `ITtsProvider` + `GoogleTtsProvider` (REST; OAuth token from the stored JSON), `TtsRequest`/`TtsResponse`, `SynthesizeLongAsync` with sentence-boundary chunking, progress, cancel | `Speech/` | fake-handler tests incl. chunk boundaries | [ ] |
| 5.3 | `VoiceCatalog` (list voices, cache, refresh) + per-app default voice in settings | `Catalog/` | test | [ ] |
| 5.4 | Pronunciation dictionary + SSML markup moved from PreseMaker into `Speech/`; shared file under `Documents\AiManager\speech\` | `Speech/` | PreseMaker's narration tests pass against the library | [ ] |
| 5.5 | Ledger: `Chars` + character-based pricing; dashboard "characters spoken" tile | `Usage/`, WPF/WinForms | | [ ] |
| 5.6 | Settings UI "Services" block (choose JSON → encrypted; Test lists voices; default voice); `AudioPreviewPanel` | WPF + WinForms | | [ ] |
| 5.7 | PreseMaker's `AudioGenerator` becomes a thin caller of `AiHub.Speech`; AuthorPlus v2 Audio items (its plan) | both repos | | [ ] |

---

## Notes from hand-testing

- **2026-09-12 — Phase 0 done.** 31 unit tests green. `discover` found keys in PreseMaker
  (Claude, Gemini, OpenAI) and CIATLE-FCE (same three); `import` copied them into
  `Documents\AiManager\credentials.json` (note: Documents is under OneDrive on Ron's machine,
  which is fine — the blobs are DPAPI-bound to this user and useless elsewhere). `test`: Claude (claude-opus-5) OK in 1.5 s,
  Gemini (gemini-2.5-flash) OK in 1.3 s, OpenAI FAIL — the account has no API credits
  (billing, not code); CIATLE-FCE's keys were the same three so nothing more to import. Design notes: `AiRequest.Provider` is
  nullable (null = app default) rather than an `AiProviderType.Default` member, so the enum
  stays a pure vendor list; fan-out returns `AiOutcome` per provider so one failure never sinks
  the others; `AiSettings` dictionaries use `JsonObjectCreationHandling.Populate` so their
  case-insensitive comparers survive a load.
- **2026-09-12 — Phase 1 done.** 65 unit tests green (SSE reader, streaming for all five
  vendors incl. mid-stream cancel and error events, catalog parsing per vendor with cache and
  fallback, pricing prefix/override rules, ledger rollover and summaries, prompt library
  edit-preservation, hub accounting and the budget event). Live: `stream Claude` streamed a
  661-token answer from claude-opus-5 in 14 s (est. $0.0168, recorded in the ledger); `models
  Claude --refresh` fetched 11 models, `models Gemini --refresh` 40 (Gemini's list includes
  non-chat previews — the settings UI should show the fallback names first and the rest under
  "more"); `usage` and `prompts` show the ledger and the written template files. Design notes:
  usage is requested with `stream_options.include_usage` on OpenAI and xAI only (Mistral
  reports it unasked and rejects unknown fields); a cancelled or failed stream still writes a
  ledger line; `Pricing` matches by longest id prefix so dated ids price; the budget event
  fires once per process per month.
- **2026-09-12 — Phase 2 done (WPF).** `Eaglin.AiManager.Wpf`: `AiSettingsView`/`Window`
  (row per provider: masked key with Show, model drop-down seeded from the fallback list plus
  the cached catalog, Models button to refresh the list, Test button that sends a one-word
  prompt and ledgers it as `connection-test`, status dot; global default provider, per-app
  default provider, monthly budget; Import keys… from the legacy files; Save/Revert),
  `PromptLibraryView`/`Window` (list, editor, live placeholder list, preview with sample
  values, Save / Reset to default / New / Delete; an app chooser in standalone mode),
  `UsageDashboardView`/`Window` (month + app filters, six tiles, by app / by model / by day
  with bars, recent calls, Copy as CSV, budget line), `AiRunPanel` (streaming box with Stop,
  Copy, elapsed/tokens/cost footer). `AiManagerApp` hosts them in four tabs. Screenshots
  checked 2026-09-12 (Settings, Prompt Library, Usage). Lesson: a resource dictionary with two
  entries under one key (`Ai.Tile` brush + style) fails only when merged inside a control —
  "DeferrableContent threw an exception" — and the standalone app now writes any unhandled
  exception to `%TEMP%\AiManager-crash.txt`. Views take an `AiHub` in the constructor and edit
  in memory until Save; Test/Models push typed keys into the hub in memory first, so an
  unsaved key can be tried. Per-app *models* have no UI yet (only the per-app default
  provider); edit `settings.json` for that.
- **2026-09-27 — 2.6 built: `Eaglin.AiManager.WinForms` 1.4.0 (⚠️ waiting for Ron).** Two forms
  and a static entry point: `AiManagerWinForms.ShowSettings(owner, "PreseMaker")` (returns true on
  Save) and `ShowUsage(owner, "PreseMaker", onlyThisApp)`, each with an `AiHub` overload for an app
  that already holds one. The settings form explains, in plain words, what a provider and an API
  key are, where to get one (a **Get a key** link per provider — the URLs are now
  `AiProviderRouter.KeyPages` in the library), and that the key is shared with the publisher's
  other programs. Non-UI logic sits in `AiSettingsSession` and `UsageReport` so it is tested
  without a window; the library gained `UsageLedger.Months()` and `UsageLedger.ToCsv()` (the WPF
  dashboard still has its own copies). DPI follows CIATLE's `DpiAwareForm`: laid out at 96 DPI,
  `AutoScaleMode.Dpi` + `PerformAutoScale()` in `OnLoad` once every control exists (so PerMonitorV2
  rescaling on a monitor change works), no fixed-width grid columns, font-derived sizes (grid rows,
  drop-down widths, wrap widths) computed after scaling. Both of this machine's monitors are at
  100%, so 150% was *simulated* (fonts and layout scaled 1.5x in a scratch host) — Ron's hand-test
  at a real 150%/200% is the check that counts. The test project now targets `net10.0` (library
  tests, anywhere) and `net10.0-windows` (the same plus the WinForms tests). Left as they are, for
  Ron to decide: Enter does not Save in the WinForms form (WPF's Save is the default button);
  the WPF views still say "Import keys…" and "(all apps)" where WinForms says "Import keys from
  other programs…" and "All programs"; `LegacyCredentials` reads PreseMaker, AuthorPlus and
  CIATLE-FCE, not the CIATLE-PE file PLAN §11 lists.
