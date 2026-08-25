# Automata

Record-once, replay-many browser automation for MindAttic. A WPF host drives a WebView2 pane
through Chrome DevTools Protocol; a generic, provider-neutral LLM tool-calling engine drives generic DOM actions — click, set field, check checkbox, upload file, read page status — by fuzzy text/role matching rather than
brittle fixed selectors.

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
