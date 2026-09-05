---
name: verify-automata-ui
description: Drive and inspect the Automata WPF app's two WebView2 panes (ControlPanel sidebar + TargetBrowser) over the Chrome DevTools Protocol — hover/click elements, read DOM and computed CSS, verify the step tree after a splice. Use instead of a plain smoke-test launch whenever a change needs interactive UI verification (hover states, click/picker flows, record-at-gap, step-tree splicing).
---

# Verify Automata's UI

The sidebar (`Automata.App/wwwroot/main.js` and its sibling modules) is a vanilla-JS SPA hosted in a WebView2 pane, not
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

Three groups of checks run, in this order:

1. **Accessibility baseline (WCAG 2.2 AA)**, against the untouched initial render so nothing
   later can mask a regression: an `axe-core` scan (injected from `tools/node_modules`, never
   shipped in `wwwroot`) failing on any serious/critical violation, plus targeted assertions for
   ARIA tree semantics, roving tabindex, accessible names on icon-only buttons, the 24x24 target
   minimum, that revealing row buttons causes no layout shift, a `:focus-visible` rule, live
   regions, and status-not-by-colour-alone.
2. **Interaction**, the original checklist: hover gap, record-at-gap, splice, pause/Continue,
   collection runs, Cancel, Settings.
3. **Keyboard operation**, last because it mutates the fixture and puts it back: arrow/Home
   navigation, a real focus ring on the keyboard-focused row, `Alt+ArrowDown`/`Alt+ArrowUp`
   reorder, the non-drag "Move task" path, dialog focus trap and focus restore, help entry point.
4. **Tab content** - bindings, control flow, the Data tab, the feature view and Gherkin
   authoring, the Schedule tab (a picker shape compiling to cron, a refusal coming back with its
   reason and the values intact, chain previews, several triggers on one entry, pause keeping its
   trigger, the tree chip), and the Runs tab, including a parked run and the live lane strip. These come last because they
   leave state behind: the schedule checks add real entries, which is what puts a `.chip.sched` on
   a tree row for the check that follows.

   The parked-run check seeds BOTH halves the app has to join — an open run manifest under
   `AUTOMATA_RUNS_ROOT` and a matching file under `AUTOMATA_PARKED_ROOT`. An open manifest alone
   is indistinguishable from a run that is still executing, which is exactly the bug the join
   exists to prevent, so seeding only one half would assert nothing. The app itself never parks
   (it has one un-pooled browser pane, so releasing it would free nothing); parking belongs to
   `automata-runner`, and the tab's job is only to explain what the runner parked.

   The lane-strip check seeds a live process using **node's own pid and real start time**. The
   reader checks liveness against the operating system rather than trusting the file, so a fixture
   with a made-up pid is correctly discarded before it ever renders. It also seeds a file for a
   pid that is definitely gone and asserts the phantom neither renders nor survives the read —
   a monitor that shows work which is not happening is worse than one that shows nothing.

Then a **second app launch** runs the **floor check** against an *empty* store on ports
9335/9336, because the first-run tutorial only fires when there are no collections. It asserts
the tutorial still walks Collection -> Task -> Steps and ends at Click Images, that the action
picker still offers only Record plus the original 14 actions, that no advanced affordance
(bindings, per-scope settings, tabs, lane strip) is on screen, and that the store gained no new
JSON fields. **This is the project's governing invariant** — a phase that breaks it is not
shippable, however much else it delivers.

Two gotchas worth knowing before you write assertions against computed style:

- Chromium snaps `outline-width` to whole device pixels, so a 2px ring reads as `1.6px` on a
  125%-scaled display. Assert that an indicator exists, not an exact CSS pixel count.
- `.node-btns` is `display:none` until its row is hovered or focused, and a `display:none`
  element measures 0x0 — focus or hover the row first, then measure. The Schedule tab's rows are
  the exception: `.sched-row .node-btns` is always visible (a handful of schedules, not four
  hundred steps), so those measure without hovering.
- `clickRowOp` retries hover-and-click as a PAIR. The tree re-renders from scratch on every host
  push, so a row can be replaced between revealing its buttons and clicking one, and CSS `:hover`
  does not reliably re-apply to the replacement without the pointer moving again — Playwright's own
  retry re-resolves the button but cannot re-hover. Without the pair retry, a run still emitting
  step events in the background makes row-button clicks time out at random.
- Schedule-editor controls are scoped by `data-trigger`, since an entry can carry several
  triggers. A bare `[data-input="time"]` matches the first block only — always pair it with
  `[data-trigger="<i>"]` when more than one block may be on screen. Entries are also picked by
  `targetId` rather than by position in `schedule.json`, so a group that adds an entry cannot
  silently retarget a later one.
- When asserting a row has not changed height, blur and move the pointer off the tree FIRST.
  `:hover` and `:focus-within` both reveal `.node-btns`, so a "before" measurement taken while
  either applies compares a grown row against itself — which is exactly how a 2px shift on every
  row survived unnoticed.
- The driver refuses to start when anything is already serving CDP on 9333-9336. A WebView2
  orphaned by an interrupted run keeps listening, and without that guard the whole run attaches
  to the STALE panel and reports mysteries like "the fixture task never appeared". If you see
  that refusal, kill the leftover `msedgewebview2.exe`.

## The env vars this depends on

`AUTOMATA_PANEL_CDP_PORT`, `AUTOMATA_TARGET_CDP_PORT`, `AUTOMATA_PANEL_PROFILE_DIR`,
`AUTOMATA_TARGET_PROFILE_DIR`, `AUTOMATA_COLLECTIONS_ROOT`, `AUTOMATA_DATASETS_ROOT`,
`AUTOMATA_RUNS_ROOT`, `AUTOMATA_SCHEDULE_PATH`, `AUTOMATA_PARKED_ROOT`, `AUTOMATA_LIVE_ROOT`,
`AUTOMATA_DEMOS_ROOT`, `AUTOMATA_SETTINGS_PATH`, `AUTOMATA_FILE_DIALOG_PATH` — all opt-in, all
no-ops when unset
(see `MainWindow.xaml.cs`'s `DebugOptions`/`ProfileDir` helpers and
`ServiceCollectionExtensions.cs`'s `CollectionStore` registration). Never set these when running
the app normally.

## What it never touches

The real `%LocalAppData%\MindAttic\Automata\{ControlPanelWebView2,WebView2}` profiles and the real
`Documents\Automata\{Collections,Datasets,Runs,Schedule,Parked,Live,Demos}` are never used — the
driver always points at a scratch directory it creates itself.

`AUTOMATA_SETTINGS_PATH` is the newest of these and the reason the harness can now open Settings at
all: `AutomataSettingsStore` (`%APPDATA%\MindAttic\Automata\settings.json`, the real LLM provider
and BYO keys) was the one store with no environment hook, so before it existed any check that
touched Settings would have been reading and writing the developer's own keys. `AUTOMATA_DEMOS_ROOT`
matters for a blunter reason: the app WRITES the generated example pages on first load, so without
the hook every test run would rewrite the developer's own `Documents\Automata\Demos`.

`AUTOMATA_FILE_DIALOG_PATH` is the odd one out: it is not a store root but the path the Export and
Import file dialogs return instead of opening. A WPF `SaveFileDialog` cannot be operated over CDP,
so without it the export/import half of the loop was unreachable from here. Both ends name the same
archive, which is what makes the round-trip check a round trip. Unset in any ordinary launch, and
then the real dialog opens.

## Where the sidebar code lives

`wwwroot/main.js` is the entry point, loaded as `<script type="module">`. The rest is split by
concern: `core.js` (the `state` object, the `post()` bridge, model lookups over the
collection/task/step tree, the `ui` object holding cross-module mutable flags), `modal.js` (all
dialogs plus the shared focus trap and focus restore), `tree.js` (tree markup, wiring,
drag-and-drop, and the whole keyboard model), `editor.js`, `render.js`, `tutorial.js`, `tabs.js`,
`settings.js`, `scoped-settings.js` (the global/collection/task/step settings dialog),
`binding-field.js` (the value-source picker), `flow-fields.js` (editors for the control-flow
steps), `data.js` (the Data tab), `runs.js` (the Runs tab), `lanes.js` (the live lane strip and its
poll), `schedule.js` (the Schedule tab, its trigger editor, and the chip tree rows carry),
`flow.js` (natural-language drafting and the feature view), and `bridge.js` (installs
`window.ssPanel`). There is no bundler and no build step — WebView2 serves
`wwwroot` over the virtual host, so module resolution just works.

Note that `node --check` does **not** validate these as ES modules and will happily accept a
misplaced `export`. To check the graph, copy the modules to a temp directory with a
`{"type":"module"}` package.json and `import('./main.js')` — Node links before it evaluates, so a
missing export throws a SyntaxError while `ReferenceError: document is not defined` means every
import resolved.

## The harvest checks, and where demo pages fit

`extractAll` is built by CLICKING, not typing, so its checks drive a real click in the real target
pane: navigate it to the generated `demos/shop/search.html`, click one product tile, and assert the
editor reports 12 matched items with a generalised `li.product` selector rather than the one tile's
own id. A second check picks a column off an `<a>` and asserts the pick did NOT follow the link —
a harvest pick has to consume the click, or picking a column inside a product tile navigates away
from the page being harvested. A third re-picks the rows and asserts the columns are cleared and
said to be cleared, because a column selector is relative to the row set and cannot survive a
different one.

The generated demo pages (`DemoPages`) are written by the app itself, so they are available to the
harness for free. `buttons.html` is the same three-button page as `tools/verify-ui-fixture.html`;
the harness still writes its own copy, so the two are duplicates for now rather than one shared
asset set.

## Extending the fixture

The fixture task/collection JSON and `tools/verify-ui-fixture.html` are written fresh by
`tools/verify-ui.mjs` itself (see `writeFixture`) — edit that function to add more steps/elements
for a new checklist, rather than hand-editing generated files.
