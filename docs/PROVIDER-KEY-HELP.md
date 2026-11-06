# Getting a key: keeping the instructions current

AI Manager tells a first-time user how to get an API key from each provider: **How?** beside
each **Get a key** in AI Settings, and a full dated help page (`Documents\AiManager\help\api-keys.html`,
written each time it is opened). Providers change their websites, their create-key forms and
their billing without notice, so these instructions go stale. This document says where they live,
when to re-check them, and what was found the last time. Plan: `DEVELOPMENT-PLAN.md`, phase 7.

## Where the instructions live

**One place: `src/Eaglin.AiManager/Help/KeyHelp.cs`.** Both settings windows and the help page
read from it, so a change there reaches every program with the next package version.

| Field | What it holds |
|---|---|
| `KeyHelp.All[p].CheckedOn` | the date this provider's steps were last checked against its own pages |
| `Steps`, `Choices` | numbered steps; for each option in the create-key form, what to choose and why |
| `Billing`, `Watch` | how the provider is paid; gotchas (key shown once, expiry, free-tier data use) |
| `Sources` | the provider's documentation pages the steps were checked against |
| `KeyHelp.CheckedOn` | the page date — must equal the newest provider date (a test checks it) |
| `KeyHelp.Changes` | the dated change log, newest first, shown as "What changed" on the page |
| `AiProviderRouter.KeyPages` | the Get a key URL — change it **together** with the steps (a test checks they agree) |

## When to re-check

- **Before every AI Manager release** that goes to a Store program.
- **At least every three months** (next: by 2026-12-31).
- **Whenever someone reports a screen that differs** from the steps, as Ron did with Anthropic's
  workspace choice on 2026-10-11.

## How to re-check

1. For each provider, open its key page and the `Sources`; walk the create-key form as a new user would.
2. Update `Steps` / `Choices` / `Billing` / `Watch`, set `CheckedOn` to today (even when nothing
   changed — the date says when it was last confirmed), and set `KeyHelp.CheckedOn`.
3. Add a `Changes` entry for anything a user would notice (a moved page, a new option, a new rule).
4. Update `KeyPages` if the address moved. Run the tests; bump the version; pack.
5. Add a line to the log below.

**Recommend the simplest safe choice for one person running several programs that share one key**:
a personal key, the default workspace/project, no expiry, full permissions on that key. Name the
key "AI Manager" — it is shared, so a program's name is misleading.

## Things the program depends on

- **Anthropic:** a key with **no single workspace** (the organization-wide choice) needs an
  `anthropic-workspace-id` header on every request; AI Manager sends only the key, so Claude answers
  400. The help says "choose Default Workspace", and `AiSettingsSession.Explain` turns that 400 into
  the same advice. Sending the header instead would need a workspace field — not planned.
- **xAI:** a key with no endpoints allowed fails every request — the help says allow all (at least chat).
- **OpenAI:** "Restricted" keys need the right scopes; the help says choose **All**.
- **Gemini:** older unrestricted keys are refused since 2026-05-28; the help says restrict to the Gemini API.
- **Anthropic, Mistral:** keys can expire; a 401 from a key that worked is explained as possibly expired.

## Log of checks

### 2026-11-06 — Google text-to-speech (voices), AI Manager 1.8.0

New: `src/Eaglin.AiManager/Help/VoiceKeyHelp.cs` — the steps for a Google Cloud API key for text-to-speech (project,
billing, enable Cloud Text-to-Speech API, Credentials ▸ API key, restrict it), the service-account alternative, the free
monthly characters and prices. Written from Google's documented flow; not yet walked through click by click — re-check it
when Ron makes his first key, and with the provider keys before each release.

### 2026-10-11 — first version (all five providers)

Checked against the providers' documentation by a research agent; points that could not be
confirmed from an official page were written cautiously in the help ("if shown", "if asked").

- **Anthropic** — Console moved to `platform.claude.com` (old address forwards). Create key asks:
  name, expiration (3 h … custom, or Never), linked account (yourself), workspace. No per-program or
  per-model scope; workspaces separate keys, spend and rate limits. Prepaid credits (about $5 minimum,
  expire after a year), no free credits. Key shown once. Sources: platform.claude.com/docs/en/get-api-key,
  …/manage-claude/authentication.
- **Gemini** — `aistudio.google.com/apikey`. New users get a project and key automatically; existing
  Cloud users import a project first. Restrict to Gemini API. Free tier: data may be used to improve
  products and read by people — the help warns teachers about students' information. Prepay from $5
  or postpay; prepay at $0 fails with 402. School/work accounts may have Create greyed out.
  *Unconfirmed:* whether the key can be named.
- **OpenAI** — `platform.openai.com/api-keys` unchanged (docs moved to developers.openai.com). Owned by
  You, Default project, Permissions All. Prepaid from $5, card hold of $5 possible. Organization
  verification for some models. *Unconfirmed:* the exact Restricted scope names (help-center pages
  returned 403; read from search results and the community forum).
- **Mistral** — console.mistral.ai ▸ API Keys. Workspace, name, expiration date, connector access
  (not relevant). Official pages disagree on whether a free-plan key works without a payment method;
  the help says "add a payment method if Mistral asks". *Unconfirmed:* phone verification, the delay
  before a new key works (kept as "can take a few minutes"), the `/api-keys` deep link.
- **xAI** — `console.x.ai/team/default/api-keys`; prepaid credits first; ACLs (endpoints, models) and
  optional rate limits. *Unconfirmed:* the Console's exact field labels and that the key is shown once
  (the help says "copy it as soon as it is shown").
