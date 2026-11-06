# AiManager

One shared library, key store, activity log and prompt library for Ron Eaglin's AI-enabled
Windows apps. Apps reference the `Eaglin.AiManager` NuGet package (local feed); users enter
their API keys once, in any app or in the standalone AI Manager, and every app uses them.

What it does: prompts to Claude, Gemini, OpenAI, Mistral and xAI (whole or streamed); speech to text (1.5.0);
pictures (1.7.0); **text to speech with Google's voices (1.8.0)** — an API key or service account, the voice list,
any length of text to one MP3 or WAV, and a voice chooser with Hear it for WPF programs.

See `docs/PLAN.md` for the design and `docs/DEVELOPMENT-PLAN.md` for the work breakdown.
