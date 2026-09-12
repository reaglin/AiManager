# CLAUDE.md — AiManager

Shared AI access layer for every AI-enabled app in `C:\Users\ronal\source\repos\`
(AuthorPlus, EasyPeasyRetirement, PreseMaker, CIATLE, and whatever comes next). One place
to enter API keys, one library that sends prompts and returns answers, one activity log and
usage ledger, one prompt-template store. Decided with Ron 2026-09-12; see `docs/PLAN.md`.

**Read first:** `docs/PLAN.md` (design and decisions) then `docs/DEVELOPMENT-PLAN.md`
(numbered tasks with status).

## Shape

```
AiManager.slnx
├── src/Eaglin.AiManager/           net10.0          the library every app references (NuGet)
├── src/Eaglin.AiManager.Wpf/       net10.0-windows  AI Settings / Prompt Library / Usage windows for WPF apps
├── src/Eaglin.AiManager.WinForms/  net10.0-windows  the same three as WinForms forms (CIATLE, PreseMaker)
├── src/AiManagerApp/               net10.0-windows  standalone "AI Manager" exe (WPF) over the same library
├── tests/Eaglin.AiManager.Tests/   net10.0          xUnit — store, router, templates, ledger, legacy import
└── build/pack.ps1                                   dotnet pack → C:\nuget-local (the local feed)
```

`Directory.Build.props`: version (`AiManagerVersion`), nullable, implicit usings,
TreatWarningsAsErrors. Fix warnings, never suppress them.

## Rules that carry the design

1. **The shared store is a folder under Documents:** `Documents\AiManager\`. Not AppData —
   MSIX-packaged Store apps get a private, virtualized AppData, and Documents is the one
   location every app (packaged or not) sees the same way. PreseMaker proved this with
   `Documents\PreseMaker\app_settings.json`.
2. **Keys are DPAPI CurrentUser, no entropy, Base64** — byte-compatible with PreseMaker and
   CIATLE so their existing blobs import without re-typing. Plaintext keys exist only in memory.
3. **The library never has UI dependencies.** `Eaglin.AiManager` is plain net10.0 so tests run
   anywhere; DPAPI calls are guarded by `OperatingSystem.IsWindows()`. UI lives in the two
   toolkit packages and the standalone app.
4. **Every call is tagged with the app name** (`AiHub.Open("AuthorPlus")`). The log, the usage
   ledger, per-app defaults and per-app prompt templates all key on it.
5. **Apps consume a versioned package, not a path.** `build/pack.ps1` builds into
   `C:\nuget-local`; each app pins `<PackageReference Include="Eaglin.AiManager" Version="x.y.z" />`.
   Bump the version for any public API change; apps upgrade deliberately.
6. **Model ids and prices live here, once.** `ModelCatalog` refreshes from each provider's
   list-models endpoint and caches; `Pricing` holds per-model prices with a user override.
   No app hard-codes a model id.

## Build, test, pack

```powershell
dotnet build AiManager.slnx
dotnet test tests/Eaglin.AiManager.Tests
.\build\pack.ps1          # → C:\nuget-local\Eaglin.AiManager.<ver>.nupkg (+ Wpf, WinForms)
```

## Consumers (and where their old AI code is)

| App | UI | Old AI layer | Status |
|---|---|---|---|
| AuthorPlus | WPF | `src/AuthorPlus.AI` (seed of this repo; deleted once adopted) | first consumer |
| EasyPeasyRetirement | WPF | none yet | consumes from day 1 |
| PreseMaker | WinForms | `Services/AiProviderRouter.cs`, `Forms/AiProviderSettingsForm.cs`, keys in `Documents\PreseMaker\app_settings.json` | migrate; keep reading its legacy file as a fallback |
| CIATLE | WinForms | `CIATLE.AICore` (+ `IAiManager` in CIATLE.Core) | migrate last; `IAiManager` becomes a thin adapter |
