# Automata

Record-once, replay-many browser automation for MindAttic. A WPF host drives a WebView2 pane
through Chrome DevTools Protocol; a generic, provider-neutral LLM tool-calling engine (ported from
`Prose.KdpPublish`'s proven KDP-publishing automation) drives generic DOM actions — click, set
field, check checkbox, upload file, read page status — by fuzzy text/role matching rather than
brittle fixed selectors.

## Status

**Phase 0 + 1** (this commit): repo/launcher bootstrap, and the generic browser-automation
infrastructure ported from `Prose.KdpPublish`/`Prose.Core` — the WebView2/CDP host, the
provider-neutral tool-calling loop (Anthropic + OpenAI), and a stripped-down generic DOM toolkit
(no KDP-specific business logic). Recording, multi-strategy element fingerprinting/replay, and
parallel orchestration are follow-up phases — see the project plan for details.

## Build

```
dotnet build Automata.slnx
dotnet test Automata.Tests
```

## Run

```
dotnet run --project Automata.App
```

Launches a two-pane window: a sidebar (local `panel.html`) for driving a task via a plain-English
instruction, and a live browser pane that instruction acts on.
