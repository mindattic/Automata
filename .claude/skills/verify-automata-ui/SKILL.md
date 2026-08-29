---
name: verify-automata-ui
description: Drive and inspect the Automata WPF app's two WebView2 panes (ControlPanel sidebar + TargetBrowser) over the Chrome DevTools Protocol — hover/click elements, read DOM and computed CSS, verify the step tree after a splice. Use instead of a plain smoke-test launch whenever a change needs interactive UI verification (hover states, click/picker flows, record-at-gap, step-tree splicing).
---

# Verify Automata's UI

The sidebar (`Automata.App/wwwroot/panel.js`) is a vanilla-JS SPA hosted in a WebView2 pane, not
native WPF controls — Windows UI Automation only sees a coarse accessibility tree for it. This
harness instead attaches to the app's WebView2 panes over CDP (via Playwright's
`connectOverCDP`), the same way you'd test a web app: real DOM queries, computed styles, real
clicks, no screenshots required (though you can add `page.screenshot()` calls if useful).

## Run it

```
cd tools && npm install   # one-time, only if tools/node_modules is missing
node tools/verify-ui.mjs [--clean]
```

Builds `Automata.App` (Debug), launches the built exe with the env vars below pointed at a fresh
scratch directory, seeds a small fixture task with a deliberate gap between two steps, then runs
a checklist: hover the gap (height must not change, only recolor), click it and pick Record,
perform a real click in the target pane, Stop, and confirm the new step landed at the right index
and the run stayed paused. Prints `[PASS]`/`[FAIL]` per step and exits non-zero on any failure.

`--clean` removes the scratch directory afterward; by default it's left on disk (path printed in
the summary) for post-mortem inspection.

## The 5 env vars this depends on

`AUTOMATA_PANEL_CDP_PORT`, `AUTOMATA_TARGET_CDP_PORT`, `AUTOMATA_PANEL_PROFILE_DIR`,
`AUTOMATA_TARGET_PROFILE_DIR`, `AUTOMATA_COLLECTIONS_ROOT` — all opt-in, all no-ops when unset
(see `MainWindow.xaml.cs`'s `DebugOptions`/`ProfileDir` helpers and
`ServiceCollectionExtensions.cs`'s `CollectionStore` registration). Never set these when running
the app normally.

## What it never touches

The real `%LocalAppData%\MindAttic\Automata\{ControlPanelWebView2,WebView2}` profiles and the real
`Documents\Automata\Collections` are never used — the driver always points at a scratch directory
it creates itself. It also never opens Settings or posts `saveSettings` — `AutomataSettingsStore`
(`%APPDATA%\MindAttic\Automata\settings.json`, the real LLM provider/BYO keys) is **not** isolated
by any of the 5 hooks, so extending this harness to touch Settings would need a 6th hook first.

## Extending the fixture

The fixture task/collection JSON and `tools/verify-ui-fixture.html` are written fresh by
`tools/verify-ui.mjs` itself (see `writeFixture`) — edit that function to add more steps/elements
for a new checklist, rather than hand-editing generated files.
