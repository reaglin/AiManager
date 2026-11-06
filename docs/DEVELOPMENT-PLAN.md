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
| 2.7 | **A program's own model** (Ron, 2026-09-27: "add the per-program model to AI Manager as an option"). **1.4.1.** Settings: `Apps.<App>.Models` (already in the file, so older versions honour it); `AiSettings.AppModelFor/SetAppModel`, `AiHub.AppModel/SetAppModel/DefaultModelSource/IsOpenedForAnApp`, `ModelSource`; unknown keys kept on save. Both windows: **"Model for <app>"** beside each provider's model ("Same as all programs" + the catalog), shown only when opened for a program, with a one-line explanation; Test checks the model the program will use. Fix: an explicit choice of the built-in default sticks (per-app always; for all programs in WinForms when the box was picked or typed in) | `Store/AiSettingsStore.cs`, `AiHub.cs`, `AiSettingsForm.cs`, `AiSettingsSession.cs`, `AiSettingsView.xaml(.cs)` | 89 + 129 tests green (+9 library: resolution in all three cases and an explicit model kept, storage beside the app's provider, re-read before save, never writing over an unreadable file, newer keys kept, clearing; +7 WinForms: session, form, Test uses the program's model, standalone shows no column; +1 WPF view). Screenshots of both windows opened for a program. UX: walkthrough + heuristics, 0 critical, 3 major (Test used the all-programs model; WPF said "apps"/"global default" where WinForms says "programs": both fixed; a price hint per model left for later, as the catalog has no tiers yet) and 3 minor (hint and tooltip wording, fixed). **Ron:** open AI Settings from CourseBuilder+ and from Gamify+ or Author+, set a model for that program, Save, check the program uses it and another keeps the model for all programs; put it back with "Same as all programs" | ⚠️ |

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

## Phase 5 — Text to speech (Ron, 2026-09-12; see PLAN.md §14) — **1.8.0 built 2026-11-06**, **1.9.0 (audiobooks) 2026-11-06**

Exit: PreseMaker's narration and AuthorPlus v2's chapter audio both synthesize through the
library with one stored Google Cloud credential.

Ron, 2026-11-06 (for Author+'s audiobooks — a narrator voice, a voice per speaking character, a Listen mode, later
audiobook export): *connect with a Google **API key** first (simplest for Microsoft Store buyers), and also accept a
**service-account JSON file*** (what CourseBuilder+ uses today). Built as **1.8.0** the same day; Author+ is written
against it in parallel.

| # | Task | Targets | Acceptance | Status |
|---|---|---|---|---|
| 5.1 | **Credential.** `credentials.json` gains a `Services` section: `"GoogleTts": { ApiKeyEncrypted \| ServiceAccountJsonEncrypted, ProjectId, ClientEmail, UpdatedUtc, UpdatedBy }` — DPAPI like the keys; the file's *content* is kept, not its path. `CredentialStore.GoogleTts` (`GoogleTtsCredential`: Kind ApiKey / ServiceAccount, `Describe()`, never prints the secret), `SetGoogleTtsApiKey`, `SetGoogleTtsServiceAccount` (refuses a file that is not a service-account key, in words), `ClearGoogleTts`. `AiProviderType.GoogleTts` exists for the ledger and errors only — not in `AllProviders`, `SetKey` refuses it. **Legacy:** `LegacyCredentials.FindGoogleTtsServiceAccount` / `ImportGoogleTts` read PreseMaker's `TtsCredentialsFilePath`; `AiHub.Open` (real store only) copies it in once when nothing is stored | `Store/CredentialStore.cs`, `Legacy/`, `AiHub.cs` | round trip (both kinds, provider keys kept), refusal, import only when empty — tests | ⚠️ |
| 5.2 | **Speak.** `GoogleTextToSpeech` (REST `v1/text:synthesize`, MP3 or LINEAR16, speakingRate) signed in with `X-Goog-Api-Key` or a service-account token (JWT RS256 with .NET's RSA, scope cloud-platform, token cached until 5 min before expiry — no Google packages). `AiSpeechRequest` → `AiHub.SpeakAsync(request, progress, ct)` → `AiSpeech` (one playable file, characters, requests, cost, elapsed); `Spoke` event. `SpeechText.Split` / `SplitSsml`: under 4,500 UTF-8 bytes per request (Google's limit is 5,000), at sentences, then commas/semicolons/dashes, then spaces, then between characters (never inside one); SSML only between top-level elements, each piece its own `<speak>`. `AudioJoin`: MP3 concatenated (a later piece's ID3 tag dropped), WAV samples merged under one RIFF header. SSML sent to a Chirp/Journey voice is refused before sending | `Speech/AiSpeech.cs`, `Speech/GoogleTextToSpeech.cs`, `Speech/SpeechText.cs` | fake-handler tests: byte limit, sentence breaks, multibyte text, emoji, SSML pieces, MP3/WAV joins, request body, progress, cancel part way (characters sent are ledgered), JWT shape and signature | ⚠️ |
| 5.3 | **Voices.** `AiHub.ListVoicesAsync(languageCode, refresh)` → `AiVoice` (Name, LanguageCode(s), Gender, **Family** parsed from the name — Chirp 3: HD, Chirp HD, Studio, Neural2, WaveNet, Standard, Polyglot, News, Casual, Journey, Other — price, `Speaker`, `Label`, `AcceptsSsml`, `AcceptsSub`). Fetched once, cached 7 days in `Documents\AiManager\speech\voices.json` (shared by every program; a stale list is used if Google cannot be reached). **Gemini-TTS voices** (names with no language, "Achernar", "Kore") are left out: they need a Gemini model name and are billed by the token. ~~Per-app default voice in settings~~ — the program keeps its own voice choices (Author+ stores them in the book) | `Speech/VoiceCache.cs`, `AiHub.cs` | parse, family/price per kind, cache shared by two hubs, refresh — tests | ⚠️ |
| 5.4 | Pronunciation dictionary + SSML markup moved from PreseMaker into `Speech/`; shared file under `Documents\AiManager\speech\` | `Speech/` | PreseMaker's narration tests pass against the library | [ ] (with 5.7, PreseMaker's move) |
| 5.5 | **Ledger and prices.** `UsageRecord.Chars` (one line per `SpeakAsync`, provider GoogleTts, model = the voice, also on failure/cancel with the characters already sent), `UsageGroup.Characters`, CSV's last column **CharactersSpoken**, a **characters spoken** tile in both AI Usage windows (shown once anything was spoken). `Pricing.PerMillionCharacters` by kind (Chirp 3: HD / Chirp HD / Journey $30, Studio $160, Neural2 / Polyglot / News / Casual $16, WaveNet / Standard $4), `SpeechPricePerMillion`, `EstimateSpeech`, `AiHub.SpeechCharacters(request)` to show the cost before sending; free monthly characters noted, not subtracted. ⚠️ Google's pricing page could not be read automatically on 2026-11-06 — **check the table against https://cloud.google.com/text-to-speech/pricing** | `Usage/UsageLedger.cs`, `Catalog/Pricing.cs`, both dashboards | ledger chars + cost, CSV, totals — tests | ⚠️ |
| 5.6 | **WPF AI Settings: "Voices — Google text-to-speech"** — paste an API key (masked; Save key / Clear, saved at once, Clear asks first), *Use a service-account file…*, **Test** (lists Google's voices, then speaks one 5-character word), *Try the voices…*, **How?** (`Help/VoiceKeyHelp.cs`: Cloud console → project → billing → enable Cloud Text-to-Speech API → Credentials → API key → restrict it; the free characters; dated 2026-11-06, also on the full help page). **`VoiceChooserWindow(hub, currentVoice, languageCode, sample)`**: language / kind (with price) / gender filters, search, list, sample sentence, **Hear it** (speaks and plays it with MediaPlayer, shows the cost; a repeat of the same sample is free), Stop, OK → `SelectedVoice`. WinForms: not built (no WinForms program needs it yet; PreseMaker's move, 5.7, adds it) | `AiSettingsView.xaml` + `.Voices.cs`, `VoiceChooserWindow.cs`, `Windows.cs` | 4 WPF tests (key saved at once without saving typed provider keys; chooser filters, current voice). UX: walkthrough + heuristics 2026-11-06 — 4 major fixed (Test now also speaks one word, since the voice list may answer on a project without billing; the saved key is no longer put back in the hidden box, so a pasted key cannot land after it; a pasted Gemini key is caught and Google's refusals mention it; "Try the voices…" opens the chooser to listen only, no OK) and 5 minor fixed (untested key shows a grey dot, not green; words follow key vs. service-account file; buttons off while testing; "billing" errors matched exactly; a sample that cannot play says so; Open Billing button in How?). Not judged from source: the rendered layout. **Ron:** look at the section and the chooser | ⚠️ |
| 5.7 | PreseMaker's `AudioGenerator` becomes a thin caller of `AiHub.SpeakAsync`; Author+ audiobooks (its plan) | both repos | | [ ] (Author+ in progress 2026-11-06) |
| 5.8 | **Version 1.8.0**, packed to `C:\nuget-local`; tests green on both targets (170 + 219, from 138 + 183). Real check 2026-11-06 on Ron's machine: PreseMaker's service account was imported on first open, 2,066 voices listed (99 en-US after Gemini-TTS voices were left out), "Hello from Author plus." spoken by en-US-Chirp3-HD-Charon — 23 characters, a valid 8.5 KB MP3, $0.0007 estimated | `Directory.Build.props` | ⚠️ **Ron:** make a Google Cloud **API key** (AI Settings ▸ Voices ▸ How?), save it, Test, hear a voice — the API-key path has not run against Google yet | ⚠️ |
| 5.9 | **1.9.0 — for Author+'s audiobook export** (its plan phase 10). `AiSpeechRequest.SampleRateHertz` → `audioConfig.sampleRateHertz` (MP3 and WAV; a WAV comes back as one 16-bit mono PCM file at that rate — 44100 for the store standard, which Author+ turns into 192 kbps MP3). `AiSpeechRequest.Pronunciations` (`AiPronunciation(Phrase, Ipa)`) → Google **custom pronunciations** on `input`: `customPronunciations: { pronunciations: [ { phrase, phoneticEncoding: "PHONETIC_ENCODING_IPA", pronunciation } ] }` (the v1 REST shape; the Chirp 3: HD guide's snake_case samples are the RPC form). Sent only when `GoogleVoiceNames.AcceptsPronunciations(voice)` (Chirp 3: HD) and the language is en-US (Google: "initial support is for en-us"), otherwise ignored silently (the program also respells); each piece carries only the phrases that occur in it, de-duplicated, empty IPA skipped. A 400 about a pronunciation sends that piece once more without them, logs it, and sets `AiSpeech.PronunciationsIgnored`. `AiVoice.AcceptsPronunciations`; `AudioJoin.Join/Wav(…, sampleRateHertz)` | `Speech/AiSpeech.cs`, `Speech/GoogleTextToSpeech.cs`, `Speech/SpeechText.cs`, `AiHub.cs` | 14 new tests (rate in the JSON and the WAV header, pronunciations per piece / per voice / per language, the 400 retry, a 1.8.0 request unchanged); 184 + 233 green. Real check 2026-11-06 (Ron's service account): "Dalgo met Olemelukwe." with en-US-Chirp3-HD-Charon, WAV 44100, Olemelukwe → oʊlɛmɛˈluːkweɪ — 200, accepted (not ignored), a 222 KB WAV, PCM 16-bit mono 44,100 Hz, 21 characters, $0.0006. **Ron:** listen to how a name in the pronunciation list is said in Author+ | ⚠️ |

## Phase 6 — Speech to text (VideoStories+, 2026-10-10)

Exit: VideoStories+ transcribes a story's recording in the cloud through the library, with the
key already stored in AI Manager, as the option beside its local Whisper.

| # | Task | Targets | Acceptance | Status |
|---|---|---|---|---|
| 6.1 | **1.5.0.** `AiHub.TranscribeAsync(AiTranscriptionRequest)` → `AiTranscript` (text, segments and words with times in seconds, language, audio seconds, estimated cost); `TranscriptionProviders` (OpenAI, Mistral), `CanTranscribe`, `TranscriptionProvidersWithKeys`, `DefaultTranscriptionModel` (OpenAI **whisper-1**, the model with word and segment times through `verbose_json`; Mistral **voxtral-mini-latest**, segment times only, and no `language` with them as Mistral's docs require); provider: the request's, else the program's default when it can transcribe and has a key, else the first with a key; plain-language errors for no key, a file over 25 MB (`MaxTranscriptionBytes`, refused before sending) and a provider that cannot transcribe; `ISpeechToText` with `OpenAiSpeechToText` / `MistralSpeechToText` (multipart, no SDK); `Pricing.PerMinute` + `EstimateAudio`; the ledger's optional `AudioSec`; `Transcribed` event | `Speech/`, `AiHub.cs`, `Catalog/Pricing.cs`, `Usage/UsageLedger.cs` | 10 new tests (`SpeechTests`: both request shapes, parsing, seconds fallback, 401 → Auth, too-large refused unsent, provider resolution, NoKey wording, ledger seconds and cost, cancel recorded, per-minute prices); 99 + 139 green. **Ron:** a real cloud transcription from VideoStories+ (step 3) with your OpenAI or Mistral key; the call shows in AI Usage | ⚠️ |

---

## Phase 7 — Getting a key: dated help for every provider (Ron, 2026-10-11)

Ron, testing a new CourseBuilder+ install: Anthropic's create-key form offered Organization,
Default Workspace and "CIATLE and Presemaker", with no way to make a key for one program, and
nothing told the user what to choose. *"We will need to keep track of changes and specific
instructions for all the different providers and options as they change. A specific full help
page that is dated … It also needs to note that instructions for different providers are
subject to change."* How the instructions are kept current: `docs/PROVIDER-KEY-HELP.md`.

Exit: a first-time user opening AI Settings from any program can find, for the provider they
chose, what to pick in that provider's create-key form, and a dated page covering all of them.

| # | Task | Targets | Acceptance | Status |
|---|---|---|---|---|
| 7.1 | **1.6.0.** `Help.KeyHelp` — the one place the instructions live: per provider the summary, numbered steps, **what to choose for each option** in its create-key form, paying, things to watch for, the official sources and the **date checked**; `KeyHelp.CheckedOn` (page date), `MayChange` (the "subject to change" notice), `Changes` (dated change log); `ShortText(p)`; `WriteHtml(root)` writes `Documents\AiManager\help\api-keys.html` (dated, notice at top, every provider, "What changed"). Content checked 2026-10-11 against each provider's documentation. Key pages updated: Claude → `platform.claude.com/settings/keys` (the Console moved), Gemini → `aistudio.google.com/apikey`, xAI → `console.x.ai/team/default/api-keys` | `src/Eaglin.AiManager/Help/KeyHelp.cs`, `AiProviderRouter.cs` | 5 new tests (`KeyHelpTests`: every provider complete and dated, key pages agree, page date = newest provider date, change log newest first, Claude says Default Workspace, page dated + "subject to change" + every provider + written under the root); 104 + 147 green. **Ron:** read the help page and the How? text for the provider you use | ⚠️ |
| 7.2 | Both settings windows: **How?** beside each **Get a key** (WinForms: a task dialog with the steps and choices, *Open the key page*, *Full help for every provider*; WPF: `KeyHelpWindow` with the same buttons) and a line under the introduction, **"Step-by-step help for getting a key from each provider (checked {date})…"**, that writes and opens the page. Errors point to it: a rejected key now says it may have expired and to see How?; a Claude 400 about a workspace says to make a key in Default Workspace | `AiSettingsForm.cs`, `AiSettingsSession.cs`, `AiSettingsView.xaml(.cs)`, `Windows.cs` | 3 new WinForms tests (`WinFormsKeyHelpTests`). Screenshots of both windows, the How? for Claude (WinForms task dialog, WPF window) and the page in Edge, 2026-10-11 — layout fine, nothing clipped. UX (heuristics, by Claude; no ux-reviewer agent on this computer): instructions say what to pick and why, the page is dated with a notice at the top (match with the real world, help and documentation), errors name the fix and point to How? (recognise and recover). **Ron:** open AI Settings from CourseBuilder+ (after 7.4), click How? beside Claude and follow it to make a key; open the full help page | ⚠️ |
| 7.3 | Keep it current: re-check every provider **before each AI Manager release and at least every three months**, and whenever a user reports a screen that differs; update `KeyHelp` (steps, date, a `Changes` entry) and `KeyPages` together | `docs/PROVIDER-KEY-HELP.md` | next check due by 2026-12-31 | ⬜ |
| 7.4 | Pack 1.6.0 to `C:\nuget-local` (done 2026-10-11). **CourseBuilder+ moved to 1.6.0 on 2026-10-11** (its plan 16.1; 794 tests green); the WPF programs (Gamify+, Author+, TrafficLab+, VideoStories+ …) show How? once they move, with their next update | `build/pack.ps1`, consumer repos | each program's AI Settings shows How? | ⬜ |

---

## Phase 8 — Making pictures (CourseBuilder+, Ron 2026-10-16)

Ron: *"Lets start Phase 1 of the image generation tool, the plan looks good. This will necessitate that the user
select from the subset of providers for generation … As far as other issues, simply make sure the user is
informed."* The plan, research and sources: `..\PreseMaker\docs\plans\AI_IMAGE_GENERATION_PLAN.md` (its phase 1
is this phase). Design: `docs/PLAN.md` §15.

Exit: a program asks AI Manager for pictures from a description and gets them back as bytes, from whichever of
OpenAI, Gemini or xAI the user chose, with the cost estimated before and recorded after, and every failure in words
the user can act on.

| # | Task | Targets | Acceptance | Status |
|---|---|---|---|---|
| 8.1 | **1.7.0.** `AiImageRequest` (prompt, style, `AiImageShape` Square / Wide16x9 / Landscape3x2 / Portrait, `AiImageQuality` Draft / Standard / Best, count 1–4, purpose) → `AiHub.GenerateImagesAsync` → `AiImageResult` (`AiGeneratedImage` bytes + type + size from the bytes + revised prompt; provider, model, estimated cost, watermark, **`ProviderReason`** — why that provider, in words); `ImagesGenerated` event | `Images/AiImageGeneration.cs`, `AiHub.cs` | fake-gateway call returns pictures; cancel throws and is recorded as cancelled | ⚠️ |
| 8.2 | **Providers** `IImageGenerator`: `OpenAiImageGenerator` (`/v1/images/generations`, size 1536×864 for 16:9, quality low/medium/high, base64), `GeminiImageGenerator` (`/v1beta/interactions` with `response_format` image + aspect ratio + 1K/2K, one picture per call; **falls back to `generateContent`** when interactions is not found; finds the picture in either answer), `XAiImageGenerator` (aspect ratio, 1k/2k, quality for 2.0, `b64_json`; a url answer is downloaded at once). Docs read 2026-10-16 (in the interface's comment) | `Images/ImageGenerators.cs` | request bodies and every answer shape tested with fake handlers | ⚠️ |
| 8.3 | **Which provider draws** (`ResolveImageProvider` → provider + reason): the request's; the program's "Pictures for <app>"; "Provider for pictures (all programs)"; the program's provider if it can draw and has a key; the first that can draw with a key. A choice without a key is an error that says so, never a quiet switch. Claude/Mistral asked for pictures → "cannot make pictures"; nothing can draw → *"Making pictures needs an OpenAI, Google Gemini or xAI key. Claude and Mistral AI cannot make pictures. Add a key in AI Settings."* | `AiHub.cs`, `Store/AiSettingsStore.cs` (`ImageProvider`, `Apps.*.ImageProvider`, `ImageModels`, `ImagePriceOverrides`) | order, reasons and errors tested; saved and read back | ⚠️ |
| 8.4 | **Prices per picture** `Pricing.PerImage` (prefix × quality; null = unknown, shown as "price unknown", never $0; a newer model never takes an older one's price), `EstimateImage`, `AiHub.EstimateImages(provider, model, quality, count)`; the user's corrections in `ImagePriceOverrides` | `Catalog/Pricing.cs` | each provider's standard price; unknown → null; override wins | ⚠️ |
| 8.5 | **Ledger:** `UsageRecord.Images`, `UsageGroup.Pictures`, cost per call; a **pictures** tile in both AI Usage windows; CSV gains a last column, Pictures (the WPF window now uses the library's CSV writer); the monthly budget counts picture spending (it sums cost) | `Usage/UsageLedger.cs`, WinForms `UsageDashboardForm` / `UsageReport`, WPF `UsageDashboardView` | ledger round trip with pictures; CSV test | ⚠️ |
| 8.6 | **Plain errors:** a refusal (OpenAI `moderation_blocked`, Gemini safety block, xAI moderation) → `AiErrorKind.Refused`, *"{vendor} would not make this picture: its safety rules refused the request. Try different words — for example, leave out people's names, brand names, or anything violent."*; OpenAI organisation not verified → how to verify; a free Gemini key → turn on billing; no picture → says so (with Gemini's text if it wrote one) | `Images/ImageGenerators.cs` | one test per error | ⚠️ |
| 8.7 | **AI Settings (both windows): "Provider for pictures (all programs)"** — Automatic, OpenAI, Gemini (Google), xAI (Grok) only — and **"Pictures for <app>"** (Same as all programs + the three); a line saying who will draw with what is on screen ("Automatic. Now: Gemini (Google), the first provider with a key that can make pictures (Claude (Anthropic) cannot make pictures).") or that the chosen provider has no key (red); Save warns when the picture provider has no key | WinForms `AiSettingsForm` / `AiSettingsSession`, WPF `AiSettingsView` | 2 WinForms tests (only drawing providers offered; the line; saved). **Ron:** look at the two boxes | ⚠️ |
| 8.8 | **Key help:** each provider's How? and the full page say whether its key can make pictures and what it needs (OpenAI: verify the organisation; Gemini: billing, SynthID; Claude: cannot; Mistral: not through AI Manager); change-log entry 2026-10-16 | `Help/KeyHelp.cs` | 5 tests | ⚠️ |
| 8.9 | Version **1.7.0**, packed to `C:\nuget-local`; tests green on both targets (138 + 183) | `Directory.Build.props`, `build/pack.ps1` | ⚠️ **Ron:** one real picture with your own key, then see it in AI Usage (CourseBuilder+ gets the button in its plan's next phase) | ⚠️ |

Not verified against a real call yet (no real AI call is ever made by the tests): the raw shape of Gemini's
interactions answer (the reader looks for the picture anywhere in it, and the fallback is the documented
generateContent shape), xAI's error wording for a refusal, and OpenAI's exact wording for an unverified organisation.
The first real picture with each key (8.9) settles them.

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
- **2026-09-27 — 2.7 built: a program's own model, 1.4.1 (⚠️ waiting for Ron).** Per-app
  models existed in `settings.json` since phase 0 but had no UI, and the WinForms window would not
  record a model equal to the built-in default, so CourseBuilder+ kept a marker file to stop its
  model switching on its own. Now both windows show **"Model for <app>"** and a program can set its
  own model in code (`hub.SetAppModel`), which CourseBuilder+ uses for its one-time move to it.
  `SetAppModel` re-reads the file before it writes and refuses to write when an existing file cannot
  be read, so a code path can never replace someone's settings with defaults. The WPF window's
  Defaults wording now matches WinForms ("programs", "Same as all programs"). Hosts for the
  screenshots used a temporary store and fake keys; Test was never pressed.
- **2026-10-10 — 6.1 built: speech to text, 1.5.0 (⚠️ waiting for Ron).** For VideoStories+'s
  cloud option beside local Whisper. OpenAI's whisper-1 is used rather than gpt-4o-transcribe
  because it is the model that returns word times, which VideoStories+ needs to put photos on
  words. Mistral returns segment times only and refuses `language` together with timestamps, so
  the language is detected there. The dashboard does not show audio minutes yet (the ledger has
  them in `AudioSec`); a tile can follow when a second program transcribes.
