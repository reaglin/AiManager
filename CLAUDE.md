# CLAUDE.md — AiManager

Shared AI access layer for every AI-enabled app in `C:\Users\ronal\source\repos\`
(AuthorPlus, EasyPeasyRetirement, PreseMaker, CIATLE, and whatever comes next). One place
to enter API keys, one library that sends prompts and returns answers, one activity log and
usage ledger, one prompt-template store. **Since 1.5.0, speech to text** (`AiHub.TranscribeAsync`:
OpenAI whisper-1 or Mistral Voxtral, times per segment and word; first used by VideoStories+). **Since 1.7.0, pictures**
(`AiHub.GenerateImagesAsync`: OpenAI, Gemini or xAI — the user picks "Provider for pictures" in AI Settings; Claude and
Mistral cannot draw; every result says which provider drew and why; prices per picture in `Pricing.PerImage`; first used by
CourseBuilder+ — `docs/PLAN.md` §15, `docs/DEVELOPMENT-PLAN.md` phase 8). **Since 1.8.0, text to speech**
(`AiHub.SpeakAsync` / `ListVoicesAsync`: Google Cloud Text-to-Speech with an API key or a service-account file, stored
encrypted under `credentials.json` `Services`; long text split under Google's 5,000-byte limit and joined into one MP3 or
WAV; voices cached in `speech\voices.json`; prices per million characters in `Pricing.PerMillionCharacters`; the WPF
**Voices** section in AI Settings and `VoiceChooserWindow`; first used by Author+'s audiobooks — `docs/PLAN.md` §14,
`docs/DEVELOPMENT-PLAN.md` phase 5. The pronunciation dictionary is still to move out of PreseMaker). Decided with Ron 2026-09-12; see `docs/PLAN.md`.

**Getting a key** (how to make a key at each provider, dated, shown by How? and the help page) lives in
`src/Eaglin.AiManager/Help/KeyHelp.cs` (the Google text-to-speech key in `Help/VoiceKeyHelp.cs`); re-check it before each release — `docs/PROVIDER-KEY-HELP.md`.

**Read first:** `docs/PLAN.md` (design and decisions) then `docs/DEVELOPMENT-PLAN.md`
(numbered tasks with status).

## Shape

```
AiManager.slnx
├── src/Eaglin.AiManager/           net10.0          the library every app references (NuGet)
├── src/Eaglin.AiManager.Wpf/       net10.0-windows  AI Settings / Prompt Library / Usage windows for WPF apps
├── src/Eaglin.AiManager.WinForms/  net10.0-windows  AI Settings + AI Usage forms for WinForms apps (PreseMaker /
│                                                    CourseBuilder+, CIATLE), opened with one call:
│                                                    AiManagerWinForms.ShowSettings(owner, "PreseMaker") / ShowUsage(…).
│                                                    Since 1.4.0. No Prompt Library form yet (no WinForms app needs one)
├── src/AiManagerApp/               net10.0-windows  standalone "AI Manager" exe (WPF) over the same library
├── tests/Eaglin.AiManager.Tests/   net10.0 and      xUnit — store, router, templates, ledger, legacy import;
│                                   net10.0-windows  the -windows target adds the WinForms tests (WinForms/ folder)
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
   ledger, per-app defaults and per-app prompt templates all key on it. Since **1.4.1** a program
   can have its **own model** per provider ("Model for <app>" in both settings windows;
   `hub.SetAppModel(p, model)` in code): a request without a model uses the program's own, else
   the model for all programs, else the built-in default. The standalone exe opens as
   `AiHub.ManagerAppName` ("AiManager") and shows no per-program column.
5. **Apps consume a versioned package, not a path.** `build/pack.ps1` builds into
   `C:\nuget-local`; each app pins `<PackageReference Include="Eaglin.AiManager" Version="x.y.z" />`.
   Bump the version for any public API change; apps upgrade deliberately.
6. **Model ids and prices live here, once.** `ModelCatalog` refreshes from each provider's
   list-models endpoint and caches; `Pricing` holds per-model prices with a user override.
   No app hard-codes a model id.

## Build, test, pack

```powershell
dotnet build AiManager.slnx
dotnet test tests/Eaglin.AiManager.Tests    # runs both targets: net10.0, net10.0-windows
.\build\pack.ps1          # → C:\nuget-local\Eaglin.AiManager.<ver>.nupkg (+ Wpf, WinForms)
```

## Consumers (and where their old AI code is)

**Every release of every consumer moves it to the latest AI Manager version first** (Ron,
2026-10-11), then a hand-test exe goes in that repo's `manual-test\`. So a new AI Manager version
reaches each program with its next release; the pinned versions are tracked in `..\CLAUDE.md`,
"Every AI program: the latest AI Manager with every release". Keep each version's changes
readable in `docs/DEVELOPMENT-PLAN.md` — the consumers read it when they move.

| App | UI | Old AI layer | Status |
|---|---|---|---|
| AuthorPlus | WPF | `src/AuthorPlus.AI` (seed of this repo; deleted once adopted) | first consumer |
| EasyPeasyRetirement | WPF | none yet | consumes from day 1 |
| PreseMaker | WinForms | `Services/AiProviderRouter.cs`, `Forms/AiProviderSettingsForm.cs`, keys in `Documents\PreseMaker\app_settings.json` | migrate to `Eaglin.AiManager.WinForms` (built in 1.4.0; plan task 4.2); keep reading its legacy file as a fallback |
| CIATLE | WinForms | `CIATLE.AICore` (+ `IAiManager` in CIATLE.Core) | migrate last; `IAiManager` becomes a thin adapter |
