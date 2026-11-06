# AiManager — design and decisions

Decided with Ron 2026-09-12 during the AuthorPlus planning session. This document is the
"why and what"; `DEVELOPMENT-PLAN.md` is the "in what order".

## 1. Purpose

Ron writes several AI-enabled Windows apps (CIATLE, PreseMaker, AuthorPlus, EasyPeasyRetirement,
more planned). Each has grown its own copy of the same AI layer: four providers, a settings
form, DPAPI key storage, an activity log. CIATLE and AuthorPlus already *borrow* PreseMaker's
saved keys one-way, which is an AI Manager by accident. This repo makes it deliberate:

- **The user enters keys once**, in any app or in the standalone AI Manager, and every app uses them.
- **Apps send prompts and get responses** through one library; no app owns provider code.
- **One activity log, one usage ledger, one dashboard** across all apps.
- **Prompt templates** are stored per app and editable by the user, not hard-coded.

## 2. Decisions (2026-09-12)

| Question | Decision |
|---|---|
| Delivery | Shared class library + shared key store on disk, plus optional settings UIs for WPF and WinForms, plus a standalone AI Manager exe. No service process, no IPC. |
| Distribution | Versioned NuGet packages in a **local feed** (`C:\nuget-local`). Apps pin a version. |
| Providers | Several at once. Every request names a provider (or asks for the app's default). Add xAI (Grok) as a fifth provider. |
| Owned centrally | Key store; activity log; usage and cost tracking; streaming with cancel; prompt templates with variables; model catalog with refresh; **an AI dashboard** (usage, tokens, prompt counts). |
| Sequence | Build the manager first, seeded from `AuthorPlus\src\AuthorPlus.AI`; AuthorPlus adopts it and deletes its copy; then EasyPeasyRetirement, PreseMaker, CIATLE. |
| Store location | `Documents\AiManager\` (Ron may rename; see §4). |
| Naming | Repo `AiManager`; package ids and root namespace `Eaglin.AiManager`. CIATLE's existing `AiManager` class is unaffected (different namespace) and becomes an adapter later. |

## 3. Architecture

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ apps: AuthorPlus (WPF) · EasyPeasyRetirement (WPF) · PreseMaker · CIATLE      │
│                                                                              │
│   Eaglin.AiManager.Wpf          Eaglin.AiManager.WinForms                    │
│   AiSettingsWindow              AiSettingsForm                               │
│   PromptLibraryWindow           PromptLibraryForm                            │
│   UsageDashboardWindow          UsageDashboardForm                           │
│   AiRunPanel (stream + cancel)  AiRunPanel                                   │
│            └────────────────┬────────────────┘                               │
│                    Eaglin.AiManager  (net10.0, no UI)                        │
│   AiHub ── Providers (Claude, Gemini, OpenAI, Mistral, xAI)                  │
│         ── CredentialStore (DPAPI)     ── PromptLibrary + PromptTemplate     │
│         ── ModelCatalog + Pricing      ── UsageLedger + ActivityLog          │
│         ── LegacyCredentials (PreseMaker / CIATLE / AuthorPlus importers)    │
│                              │                                               │
│                Documents\AiManager\  (the shared store, §4)                  │
└──────────────────────────────────────────────────────────────────────────────┘
      AiManagerApp.exe — standalone WPF: keys, templates, dashboard, model refresh
```

Dependency rules: the library depends on nothing but the BCL (System.Text.Json,
System.Net.Http, System.Security.Cryptography.ProtectedData). UI packages depend on the
library only. Apps depend on the library and, optionally, one UI package. The standalone app
is just another consumer.

## 4. The shared store on disk

`Documents\AiManager\` — Documents, not AppData, because MSIX-packaged Store apps see a
private, virtualized AppData and would each get their own copy. Documents is the one folder
every app on the machine, packaged or not, reads and writes identically (PreseMaker has relied
on this since 2025). OneDrive may sync Documents; the keys are DPAPI-bound to this Windows
user on this machine, so a synced `credentials.json` is useless elsewhere, which is the
intended behaviour.

```
Documents\AiManager\
  credentials.json         keys (DPAPI, Base64) — one entry per provider
  settings.json            global + per-app defaults (provider, model, price overrides)
  models.json              cached model catalog per provider, with fetch time
  prompts\<App>\<name>.json     prompt templates, one file each
  usage\usage-YYYY-MM.jsonl     one line per call (the ledger; feeds the dashboard)
  logs\activity-YYYY-MM.log     human-readable activity log (all apps)
```

**credentials.json** (PascalCase, versioned):

```json
{
  "SchemaVersion": 1,
  "Providers": {
    "Claude":  { "ApiKeyEncrypted": "<base64 DPAPI>", "UpdatedUtc": "…", "UpdatedBy": "AuthorPlus" },
    "Gemini":  { "ApiKeyEncrypted": "" },
    "OpenAi":  { "ApiKeyEncrypted": "" },
    "Mistral": { "ApiKeyEncrypted": "" },
    "XAi":     { "ApiKeyEncrypted": "" }
  }
}
```

**settings.json**:

```json
{
  "SchemaVersion": 1,
  "DefaultProvider": "Claude",
  "Models":  { "Claude": "claude-opus-5", "Gemini": "models/gemini-2.5-flash", "OpenAi": "gpt-4o", "Mistral": "mistral-large-latest", "XAi": "grok-4" },
  "Apps": {
    "AuthorPlus": { "DefaultProvider": "Claude", "Models": { "Claude": "claude-sonnet-5" } }
  },
  "PriceOverrides": { "claude-opus-5": { "InputPerMTok": 15.0, "OutputPerMTok": 75.0 } },
  "MonthlyBudgetUsd": null
}
```

Per-app entries override global ones; anything missing falls through. Apps never write the
global section except through the settings UI.

**A program's own model (1.4.1, Ron 2026-09-27).** `Apps.<App>.Models.<Provider>` is that
program's own model for a provider, shown in both settings windows as **"Model for <app>"**
beside the model for all programs ("Same as all programs" = no entry). A request that names no
model uses: the program's own model, else the model for all programs, else the built-in default;
a request that names its model keeps it. The shape was already in the file (and read by 1.0–1.4),
so older versions honour an override written by 1.4.1. An explicit per-app model is stored as
given even when it equals the built-in default; in the WinForms window a model for all programs
that the user *picked* is stored too (an untouched box still leaves the built-in default unpinned,
so it follows AI Manager's updates). Keys a newer version writes (`[JsonExtensionData]`, at the
root and in each app entry) survive a save by an older one from 1.4.1 on. A program sets its own
model in code with `hub.SetAppModel(provider, model)` (see §5).

**Encryption:** `ProtectedData.Protect(utf8, null, DataProtectionScope.CurrentUser)` → Base64,
exactly PreseMaker's scheme, so its blobs (and CIATLE's, once CIATLE encrypts) import as-is.

**Concurrency:** several apps may run at once. All JSON writes are temp-then-move; the ledger
and log append with `FileShare.ReadWrite` and a short retry; a stale read is tolerated (the
settings UI re-reads before it saves; last writer wins per file, which is acceptable for a
single user).

## 5. Public API (the part apps see)

```csharp
using Eaglin.AiManager;

var ai = AiHub.Open("AuthorPlus");                     // app name tags everything

// availability, for menus and status bars
ai.IsAvailable()                                       // the app's default provider has a key
ai.ProvidersWithKeys                                   // for a "compare on…" menu
ai.DisplayName(AiProviderType.Claude)                  // "Claude (Anthropic)"

// one-shot
AiResponse r = await ai.CompleteAsync(new AiRequest
{
    Provider  = null,                                  // null = app default; or an explicit provider
    Model     = null,                                  // null = app/global default
    System    = "...",
    User      = "...",
    MaxTokens = 4000,
    Purpose   = "chapter-summary"                      // free text; lands in log + ledger
}, ct);
r.Text; r.Provider; r.Model; r.InputTokens; r.OutputTokens; r.EstimatedCostUsd; r.Elapsed;

// streaming
await foreach (AiChunk chunk in ai.StreamAsync(req, ct)) { append(chunk.Text); }
// the final chunk (IsFinal) carries usage; cancellation stops the HTTP stream and is still ledgered

// templates
PromptTemplate t = ai.Prompts.Get("chapter-summary");  // app-scoped; built-in default if the user has none
var req2 = t.Bind(new { title = ch.Title, text = prose });   // {placeholders} → values
// ai.Prompts.List(), Save(t), ResetToDefault(name); apps register defaults at startup:
ai.Prompts.RegisterDefaults(new[] { new PromptTemplate("chapter-summary", system, user) });

// fan-out (AuthorPlus "compare evaluations") — one outcome per provider, failures isolated
IReadOnlyList<AiOutcome> all = await ai.CompleteOnAllAsync(req, providers, ct);
AiResponse r3 = await ai.RunTemplateAsync("chapter-summary", new { title, text }, ct);

// usage
UsageSummary u = ai.Usage.Summarize(month: DateTime.Now, app: "AuthorPlus");  // totals, by app/model/day
ai.Usage.Recent(50)                                    // for the dashboard's call list
ai.BudgetExceeded += s => ...;                         // once per month when spend passes settings.MonthlyBudgetUsd

// a program's own model (1.4.1): "Model for <app>" in AI Settings
ai.DefaultModel(AiProviderType.Claude)                 // what a request without a model uses
ai.DefaultModelSource(AiProviderType.Claude)           // ModelSource.App | AllPrograms | BuiltIn
ai.AppModel(AiProviderType.Claude)                     // this program's own model, or null
bool ok = ai.SetAppModel(AiProviderType.Claude, "claude-sonnet-4-6");   // re-reads settings.json, sets, saves;
                                                       // null clears; false (nothing written) if the file cannot be read
ai.Settings.AppModelFor("AuthorPlus", p) / ai.Settings.SetAppModel("AuthorPlus", p, m)   // in memory, for a settings UI
ai.IsOpenedForAnApp                                    // false for the standalone program (AiHub.ManagerAppName)

// model catalog
ModelList models = await ai.Models.ListAsync(AiProviderType.Claude, refresh: false, ct);   // .Models, .FetchedUtc, .FromFallback
```

`AiRequest.Provider` is nullable (null = the app default) so `AiProviderType` stays a pure
vendor list. Multi-turn history and attachments are not in the request yet; they are additive
when a chat feature needs them.

Errors: `AiException` with `Kind` (NoKey, Auth, RateLimit, Network, ProviderError, Cancelled)
and the provider's message, so UIs can say "your OpenAI key was rejected" instead of dumping
JSON. Never throw a raw `HttpRequestException` to an app.

## 6. Providers

| Provider | Endpoint | Streaming | List models |
|---|---|---|---|
| Claude (Anthropic) | `POST /v1/messages` | SSE, `stream: true` | `GET /v1/models` |
| Gemini (Google) | `POST /v1beta/{model}:generateContent` | `:streamGenerateContent?alt=sse` | `GET /v1beta/models` |
| OpenAI | `POST /v1/chat/completions` | SSE, `stream: true` | `GET /v1/models` |
| Mistral | `POST /v1/chat/completions` (OpenAI-shaped) | same | `GET /v1/models` |
| xAI (Grok) | `POST https://api.x.ai/v1/chat/completions` (OpenAI-shaped) | same | `GET /v1/models` |

The existing AuthorPlus.AI providers (`Providers.cs`, 270 lines) are the seed: one shared
`HttpClient`, uniform error mapping, usage parsing. Streaming is added per provider as an
`IAsyncEnumerable<AiChunk>`; non-streaming remains for callers that want a single string.
`ChatCompletions` (the OpenAI-shaped helper) serves OpenAI, Mistral and xAI.

Default model ids on day one (from AuthorPlus.AI, updated 2026-09-04): Claude
`claude-opus-5`; Gemini `models/gemini-2.5-flash`; OpenAI `gpt-4o`; Mistral
`mistral-large-latest`; xAI `grok-4` (verify against the live catalog before release — the
catalog refresh exists precisely so this table stops mattering).

**Pricing:** an embedded table of USD per million input/output tokens for the models in the
default lists, plus `PriceOverrides` in settings for anything else. Cost is *estimated* and
labelled so everywhere. Unknown model → cost blank, tokens still recorded.

## 7. Prompt templates

A template is a named pair (system, user) with `{placeholders}`, an optional default
`MaxTokens`, and a description shown in the library window. Apps ship defaults in code and call
`RegisterDefaults` at startup; the library writes any that are missing to
`prompts\<App>\` on first use and never overwrites a user-edited file. The settings UI offers
"Reset to default" per template and a live preview with sample values. Placeholder values are
inserted verbatim; the template author owns the framing.

## 8. Activity log, usage ledger, dashboard

- **Activity log** (`logs\activity-YYYY-MM.log`): one line per event, `time | app | level |
  message`. Every request start/finish/error, key changes (never the key), catalog refreshes.
  Apps get `ai.Log.Info(...)` for their own notes so a single file tells the whole story.
- **Usage ledger** (`usage\usage-YYYY-MM.jsonl`): one JSON object per completed or failed call:
  `{ "T", "App", "Provider", "Model", "Purpose", "InTok", "OutTok", "CostUsd", "Ms",
  "Ok", "Error", "Streamed", "Cancelled" }`. Append-only; monthly files stay small.
- **Dashboard** (Ron's request 2026-09-12): a window in the standalone app and in both UI
  packages showing, for a chosen month (default: this one): total prompts, tokens in/out,
  estimated cost, error count, average latency; the same broken down **by app**, **by
  provider/model**, and **by day** (a small bar per day); and a list of the most recent calls
  with purpose, app, provider, tokens, cost, elapsed, status. A "Copy as CSV" button and an
  optional monthly budget line (from settings) that turns the total red when exceeded. All
  computed from the ledger; no separate database.

## 9. Settings UIs

Three surfaces, same behaviour, built once per toolkit:

- **AI Settings** — a row per provider: key box (masked, "Show"), model combo (from the
  catalog, refresh button, free-typed ids allowed), "Test" (a one-token call), status dot
  (green = key present and tested; grey = none). A "Default provider" combo and, when opened
  from an app, an "Also use as default for *AppName*" option. An "Import keys from PreseMaker /
  CIATLE / AuthorPlus" button runs `LegacyCredentials`.
- **Prompt Library** — templates for the calling app (or all apps in the standalone), edit,
  preview with sample values, reset.
- **Usage Dashboard** — §8.

The standalone **AI Manager** app (`AiManagerApp.exe`, WPF, no Store listing for now) opens
straight to Settings with tabs for the other two. It exists so a user can manage keys without
opening any particular app, and so Ron has one place to watch spend across everything.

## 10. Packaging and distribution

- `build/pack.ps1`: sets the version from `Directory.Build.props`, runs `dotnet pack -c Release`
  for the three library projects into `C:\nuget-local\`, and registers the feed once:
  `dotnet nuget add source C:\nuget-local --name eaglin-local` (idempotent check first).
- Consumers: `<PackageReference Include="Eaglin.AiManager" Version="1.0.0" />` (+ `.Wpf` or
  `.WinForms`). Upgrading is editing the version. Semantic versioning; bump minor for additive
  API, major for breaking.
- Because the feed is a folder, a clean clone on another machine needs the folder populated:
  `pack.ps1` is the documented first step. Publishing to GitHub Packages is a later option
  (decision 2026-09-12: local feed first).
- The standalone app is published as a framework-dependent folder for now (`dotnet publish`),
  installed by copying; an MSIX via SMADA's `packaging/pack.ps1` pattern if it ever ships.
- **Microsoft Store consumers (Ron, 2026-09-12):** the feed matters only at build time. NuGet
  restore copies the package DLLs next to the app, `dotnet publish` puts them in the publish
  folder, and the MSIX packs that folder — so a Store submission carries `Eaglin.AiManager.dll`
  and `Eaglin.AiManager.Wpf.dll` inside the package and resolves nothing at install or run
  time. The only third-party dependency underneath is Microsoft's
  `System.Security.Cryptography.ProtectedData` (MIT), which is Store-safe. Everything else is
  the .NET base library. DPAPI and `Documents\AiManager` both work inside an MSIX (full-trust
  desktop app); AppData would not have (virtualised), which is why the store lives in Documents.

## 11. Migration of the existing apps

Legacy key files, all DPAPI CurrentUser or plaintext, and their import path:

| App | File | Key fields | Import |
|---|---|---|---|
| PreseMaker | `Documents\PreseMaker\app_settings.json` | `ClaudeApiKeyEncrypted` … (DPAPI) | decrypt, re-encrypt into the shared store |
| CIATLE-FCE | `%AppData%\CIATLE-FCE\prefs.json` | `aiSettings.claudeApiKey` … (plaintext) | read, encrypt |
| CIATLE-PE | `%AppData%\CIATLE.PE\config.json` | `AnthropicApiKey` (plaintext) | read, encrypt |
| AuthorPlus | `%AppData%\AuthorPlus\ai_settings.json` | DPAPI | decrypt, re-encrypt |

`LegacyCredentials.Discover()` lists what it found per app and provider; the settings UI shows
it and imports on click. Nothing is imported silently, and legacy files are left untouched.

Per app:

1. **AuthorPlus** — replace `src/AuthorPlus.AI` with the package; `AiSettingsWindow` →
   `Eaglin.AiManager.Wpf.AiSettingsWindow`; the two existing AI actions become templates;
   delete the AI project and its tests (the library's tests cover them). Its
   `PreseMakerCredentials` becomes the library's `LegacyCredentials.PreseMaker`.
2. **EasyPeasyRetirement** — no AI code yet; references the package when its AI features start.
3. **PreseMaker** — `AiProviderRouter` and `AiProviderSettingsForm` replaced by the package and
   the WinForms form; per-task provider routing (`AiTaskAssignment`) maps onto
   `AiRequest.Provider`; it keeps reading `app_settings.json` keys as a fallback until the user
   imports, then the fallback is removed one release later. Narration (Google TTS) is not AI
   Manager's concern.
4. **CIATLE** — `CIATLE.AICore` deleted; `CIATLE.Core.Services.IAiManager` stays as the app's
   interface with a 40-line adapter over `AiHub` so FCE forms do not change.

## 12. Testing

xUnit in `tests/`, no network: providers are tested against a fake `HttpMessageHandler` with
recorded response shapes (including SSE streams and error bodies); `CredentialStore` round-trips
on Windows and is skipped elsewhere; `PromptTemplate.Bind` covers missing/extra placeholders;
`UsageLedger` covers append, monthly rollover, summarize by app/provider/day; `LegacyCredentials`
uses synthetic files in a temp folder (the AuthorPlus tests for PreseMaker keys move here).
A `--selftest` switch on the standalone app runs one real one-token call per configured
provider and prints the results, which is the manual "is the key good" check.

## 13. Open points for Ron

- Folder name `Documents\AiManager` (or `Documents\Eaglin\AiManager` to group future shared things).
- Whether the standalone app should be a Store listing eventually (free) or stay a side-loaded exe.
- Monthly budget: a soft warning only, or should apps refuse to send once it is hit?

## 14. Text to speech (Ron, 2026-09-12 — designed now, built in Phase 5)

Ron: *"The AI manager should also be able to manage credentials and text to speech (note
this is currently in PreseMaker). We are going to use TTS in AuthorPlus … audio versions of
the chapters in version 2 (not v1), but we should develop knowing where we are going."*

**What PreseMaker does today** (`Services/AudioGenerator.cs`, `Models/AppSettings.cs`):
Google Cloud Text-to-Speech over REST; the credential is a **service-account JSON file** whose
path is saved in `app_settings.json` (`TtsCredentialsFilePath`); an OAuth2 bearer token is
minted from it with `Google.Apis.Auth`; the default voice is `en-US-Studio-M`; SSML is built by
`SpeechMarkupService` with a pronunciation dictionary (`<sub alias>`), and WAV files are written
per slide. That pipeline moves here so PreseMaker and AuthorPlus share it.

**Design (additive to everything above):**

- **Credential kinds.** `credentials.json` grows a `Services` section beside `Providers`:
  `"GoogleCloud": { "ServiceAccountJsonEncrypted": "<DPAPI of the whole JSON>", "ProjectId": "…" }`.
  The JSON *content* is stored encrypted, not a path, so the credential survives the file being
  moved and is shared by every app. `LegacyCredentials` learns to read PreseMaker's
  `TtsCredentialsFilePath` and import the file's content. The settings UI gets a "Services"
  block: Google Cloud (choose JSON file → imported and encrypted; Test = list voices).
- **`ITtsProvider`** in the library, first implementation `GoogleTtsProvider` (REST, token from
  the stored service-account JSON — `Google.Apis.Auth` is Apache-2.0 and Store-safe; if a
  dependency-free JWT signing is preferred, RS256 with `System.Security.Cryptography` is ~40
  lines). Later: OpenAI TTS, Azure Speech, ElevenLabs — same interface.
  `TtsRequest { Text | Ssml, Voice, LanguageCode, SpeakingRate, Pitch, AudioFormat (Wav|Mp3|Ogg), Purpose }`
  → `TtsResponse { byte[] Audio, Format, Characters, EstimatedCostUsd, Elapsed }`, plus
  `SynthesizeLongAsync` that splits at sentence boundaries under the vendor's per-request limit
  (Google: 5,000 bytes) and concatenates, with progress and cancel.
- **Voice catalog** (`VoiceCatalog`, cached in `models.json` under a `Voices` key): the vendor's
  voice list with language, gender, and type (Studio/Neural2/WaveNet/Standard), refreshable like
  models. Per-app default voice in `settings.json` (`Apps.AuthorPlus.Tts.Voice`).
- **Pronunciation dictionary** and SSML helpers move from PreseMaker into the library
  (`Speech/PronunciationDictionary`, `Speech/SpeechMarkup`): shared, stored at
  `Documents\AiManager\speech\pronunciations.json`, editable in the settings UI.
- **Ledger and dashboard:** TTS calls are ledger lines too, with `Chars` instead of tokens and
  cost from a per-million-characters price table (Google Studio ≈ $160/M chars, Neural2 ≈
  $16/M, Standard ≈ $4/M — verified when built). The dashboard shows a "characters spoken" tile
  when any TTS call exists.
- **`AiHub`** exposes `Speech` (the TTS service) beside `Prompts`, `Models`, `Usage`; the WPF
  package gains a `TtsSettingsView` section and an `AudioPreviewPanel` (play/stop of a
  synthesized sample).

**What AuthorPlus v1 does about it now:** nothing in code except leaving room — no `Audio` item
kind yet, no voice field on `Character`. The v2 design is in AuthorPlus `docs/DEVELOPMENT-PLAN.md`
("Version 2 — Audio"): an `Audio` item kind whose file lives in `audio\{id}.mp3` beside the
book's other folders, a `Character.Voice` for per-character voices, and speaker attribution of
dialogue as the prerequisite for multi-voice narration.

**As built in 1.8.0 (2026-11-06)** — where it differs from the design above:

- The credential is an **API key or a service account** (Ron, 2026-11-06: the key first, as simplest for Store buyers);
  `credentials.json` → `Services.GoogleTts` with `ApiKeyEncrypted` or `ServiceAccountJsonEncrypted`. PreseMaker's
  file is imported automatically on the first open of the real store when nothing is stored.
- No `ITtsProvider` interface yet: one class, `Speech/GoogleTextToSpeech`, and the hub's `SpeakAsync` /
  `ListVoicesAsync` / `CanSpeak` / `EstimateSpeech` / `Spoke`. The interface comes with a second vendor.
- Token signing is .NET's own RSA (no `Google.Apis.Auth`).
- The voice list is cached in `speech\voices.json` (not `models.json`); Gemini-TTS voices are left out.
- No per-app default voice in `settings.json`: each program keeps its own (Author+: per book and per character).
- The pronunciation dictionary and SSML helpers have not moved yet (with PreseMaker's move, plan 5.4/5.7).
- Pitch is not sent (Chirp 3: HD voices refuse it); Ogg is not offered.

## 15. Making pictures (Ron, 2026-10-16 — built in Phase 8, version 1.7.0)

The plan, the research (providers, prices, terms, copyright) and its sources are in CourseBuilder+'s
`docs/plans/AI_IMAGE_GENERATION_PLAN.md`; this section is the library's part.

- **Shaped like speech to text (1.5.0):** `AiImageRequest` → `AiHub.GenerateImagesAsync` → `AiImageResult`;
  `IImageGenerator` per vendor (REST, no SDK, through `ProviderHttp`); `ImageProviders` (OpenAI, Gemini, xAI);
  `Pricing.PerImage`; the ledger's `Images`; an `ImagesGenerated` event. No UI in the library.
- **Words, not vendor terms:** the request says Shape and Quality; each generator maps them (OpenAI pixel sizes in
  multiples of 16, Gemini and xAI aspect ratios; low/medium/high, 1K/2K). The app never names a size or a model.
- **The user chooses who draws**, from the providers that can: "Provider for pictures" in AI Settings, for all
  programs and per program, beside the text provider. Automatic means: the program's own provider if it can draw and
  has a key, else the first that can. Every result carries **`ProviderReason`**, and `ResolveImageProvider()` gives it
  before sending, so a program can always tell the user which AI will draw and why (Ron: *"make sure the user is
  informed"*).
- **Money is shown, never hidden:** `EstimateImages` before sending (null = "price unknown", never $0), the real count
  and estimated cost in the ledger after, picture spending inside the monthly budget, a pictures tile in AI Usage.
- **Failures in plain words:** a refusal is `AiErrorKind.Refused` with what to change; a key that cannot draw yet
  says what to do (OpenAI organisation verification, Gemini billing).
- **Watermarks are passed on** (`AiImageResult.Watermark`: "SynthID" for Gemini) so a program can disclose them.
- Models and prices live here (rule 6): defaults gpt-image-2, gemini-nano-banana-2.1, grok-imagine-image-2.0;
  `settings.json` `ImageModels` and `ImagePriceOverrides` let a user correct them without a new version.
