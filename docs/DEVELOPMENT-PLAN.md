# AiManager — development plan (executable breakdown)

Numbered tasks per phase with file targets and acceptance criteria. Task IDs are stable — do
not renumber. Status legend: `[ ]` not started · `[~]` in progress · `[x]` done.

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
| 0.1 | Solution, projects, `Directory.Build.props` (version, nullable, warnings-as-errors), `.gitignore`, initial commit, GitHub repo `reaglin/AiManager` (private) | root | `dotnet build` clean | [ ] |
| 0.2 | Copy AuthorPlus.AI into `src/Eaglin.AiManager`, rename namespaces, drop `UsePreseMakerKeys` (replaced by import) | `src/Eaglin.AiManager/*` | existing AuthorPlus AI tests pass under the new names | [ ] |
| 0.3 | `AiProviderType` gains `XAi`; `XAiProvider` over the OpenAI-shaped helper | `Providers/` | fake-handler test | [ ] |
| 0.4 | `CredentialStore` — `Documents\AiManager\credentials.json`, DPAPI, temp-then-move, guarded for non-Windows | `Store/CredentialStore.cs` | round-trip test; corrupt file → empty store + log line, never throws | [ ] |
| 0.5 | `AiSettingsStore` — `settings.json` with global + per-app defaults, price overrides, budget | `Store/AiSettingsStore.cs` | fall-through resolution tested (app → global → built-in) | [ ] |
| 0.6 | `AiHub.Open(app)`, `AiRequest`/`AiResponse`/`AiException`, `CompleteAsync`, `CompleteOnAllAsync` | `AiHub.cs`, `Models/` | fake-provider tests: default resolution, explicit provider, no-key error kind | [ ] |
| 0.7 | `ActivityLog` moved to `Documents\AiManager\logs\activity-YYYY-MM.log`, app-tagged, multi-process safe append | `ActivityLog.cs` | two writers interleave without exceptions | [ ] |
| 0.8 | `LegacyCredentials` — discover + import from PreseMaker, CIATLE-FCE, CIATLE-PE, AuthorPlus files | `Legacy/` | synthetic-file tests per source; import never deletes the source | [ ] |
| 0.9 | Console self-test project or `--selftest` in the (later) app: one call per provider with a key | `tools/` or `AiManagerApp` | Ron runs it with his real keys | [ ] |

## Phase 1 — Streaming, catalog, usage, templates

Exit: a chapter-length request streams into a console with Ctrl-C cancel; the ledger shows it.

| # | Task | Targets | Acceptance | Status |
|---|---|---|---|---|
| 1.1 | `StreamAsync` on `IAiProvider` for all five providers (SSE parsing), `AiChunk`, final chunk carries usage; cancel closes the stream | `Providers/` | fake SSE tests incl. mid-stream cancel and error events | [ ] |
| 1.2 | `ModelCatalog` — list-models call per provider, `models.json` cache with fetch time, `refresh` flag, built-in fallback list | `Catalog/` | fake responses parsed; cache used when offline | [ ] |
| 1.3 | `Pricing` — embedded table + overrides; `EstimatedCostUsd` on every response | `Catalog/Pricing.cs` | known model → cost; unknown → null, tokens kept | [ ] |
| 1.4 | `UsageLedger` — JSONL append per call (ok, error, cancelled), `Summarize(month, app?)` by app / provider+model / day, `Recent(n)` | `Usage/` | rollover at month boundary; summary numbers tested | [ ] |
| 1.5 | `PromptTemplate` + `PromptLibrary` — per-app folder, `RegisterDefaults`, `Bind`, `ResetToDefault`, never overwrite user edits | `Prompts/` | placeholder tests; user-edited file survives re-register | [ ] |
| 1.6 | Monthly budget: `AiHub` raises a `BudgetExceeded` warning event (soft) | `AiHub.cs` | test | [ ] |

## Phase 2 — UIs

Exit: Ron enters his keys once in the standalone app; AuthorPlus (Phase 3) finds them.

| # | Task | Targets | Acceptance | Status |
|---|---|---|---|---|
| 2.1 | `Eaglin.AiManager.Wpf`: `AiSettingsWindow` (rows per provider, test, catalog refresh, default provider, per-app default, legacy import) | `src/Eaglin.AiManager.Wpf/` | hand-test; DPI-crisp | [ ] |
| 2.2 | `PromptLibraryWindow` (list, edit, preview with sample values, reset) | same | | [ ] |
| 2.3 | `UsageDashboardWindow` — month picker; totals (prompts, tokens in/out, cost, errors, avg ms); by app, by provider/model, by day; recent-calls grid; Copy as CSV; budget line | same | numbers match `UsageLedger.Summarize` on a fixture ledger | [ ] |
| 2.4 | `AiRunPanel` control — streaming text area, Stop button, elapsed/tokens footer | same | used by AuthorPlus analysis item | [ ] |
| 2.5 | `AiManagerApp` (WPF exe) hosting 2.1–2.3 as tabs; `--selftest` | `src/AiManagerApp/` | Ron's daily "what did I spend" view | [ ] |
| 2.6 | `Eaglin.AiManager.WinForms`: the same three forms + panel for CIATLE/PreseMaker (built when PreseMaker migrates, Phase 4) | `src/Eaglin.AiManager.WinForms/` | | [ ] |

## Phase 3 — Package and first consumer

Exit: AuthorPlus builds against `Eaglin.AiManager` from `C:\nuget-local` and its own AI project is gone.

| # | Task | Targets | Acceptance | Status |
|---|---|---|---|---|
| 3.1 | `build/pack.ps1` — version from props, pack three packages to `C:\nuget-local`, register feed idempotently; README section | `build/` | fresh clone: `pack.ps1` then AuthorPlus builds | [ ] |
| 3.2 | AuthorPlus adoption (tracked in AuthorPlus `docs/DEVELOPMENT-PLAN.md` 1.15): package reference, delete `src/AuthorPlus.AI` and its tests, replace `AiSettingsWindow`, register its templates | AuthorPlus repo | AuthorPlus AI menu works with keys entered only in AiManagerApp | [ ] |
| 3.3 | Tag `v1.0.0` | | | [ ] |

## Phase 4 — Remaining consumers

| # | Task | Targets | Acceptance | Status |
|---|---|---|---|---|
| 4.1 | EasyPeasyRetirement references the package when its first AI feature starts | that repo | | [ ] |
| 4.2 | PreseMaker: WinForms package (2.6), replace router + settings form, legacy fallback for one release, per-task routing → `AiRequest.Provider` | PreseMaker repo | its 291 tests stay green | [ ] |
| 4.3 | CIATLE: delete `CIATLE.AICore`, adapter implementing `IAiManager` over `AiHub` | CIATLE repo | FCE narrative drafting unchanged for the user | [ ] |
| 4.4 | Remove legacy fallbacks; `LegacyCredentials` stays as an import button only | all | | [ ] |

---

## Notes from hand-testing

*(append dated notes here)*
