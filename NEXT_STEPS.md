# Automata — status and remaining work

## Where this came from

Automata generalizes the browser-automation pattern proven out in `Prose.KdpPublish` (a WPF app
that drives KDP's publishing flow through a WebView2 pane) into a reusable engine: record a
browser session once, and replay it — recognizing the same DOM elements even after a site's
markup changes, by falling back across multiple identification strategies (id, CSS selector,
name, class, XPath, ARIA role/label, nearby label, visible text).

Chosen architecture: **WebView2 WPF host** (not Playwright), locator/recorder engine built
**from scratch, Playwright-inspired**.

## Done

**Phase 0/1** (commit `30bcdb2`): repo/launcher bootstrap; ported generic WebView2/CDP host
plumbing, provider-neutral LLM tool-calling engine, and generic DOM tools from Prose.KdpPublish.

**v2 — full record/edit/replay product** (commits `b64e7b8`…, 2026-08-25), all NUnit-covered
(64 tests green, build clean):

1. **Model** — Collection 1:M TaskDefinition 1:M Step (recursive substeps), typed `StepAction`
   enum, multi-strategy `ElementFingerprint`, shared `AutomataJson` conventions.
2. **CollectionStore** — `~\Automata\collections\<id>\collection.json` + `tasks\<id>.json`;
   CRUD/move/duplicate, TaskOrder sorting, drift healing, Default-collection auto-assign.
3. **ArchiveService** — `*.automata.zip` export/import with id regeneration, name-collision
   suffixes, "Imported" collection for orphan tasks.
4. **fingerprint.js / resolver.js** (Core embedded resources) + `FingerprintResolver` —
   id→css→name→class→xpath→aria→label→text cascade, first-unique-visible wins, clear-leader
   scoring (near-ties fail as ambiguous), overlay highlight, self-heal re-fingerprinting.
5. **IBrowserSurface.NavigateAsync** + `BrowserActions` (shared perform-mechanics factored from
   the LLM tools) + surface-based page-busy overload.
6. **ReplayEngine** — StepEvent stream, pauseForUser gate (`ReplayControl`), per-action
   post-condition auto-confirm, settle-wait, self-heal write-back. `RunLogWriter` →
   `Documents\Automata\Logs\`. (Dry Run/Validate modes were later removed by design — one Run
   button; `isCommitPoint` survives as an informational ◆ marker only.)
7. **RecorderSessionBuilder** — pure event→step coalescer (burst/focus-click/toggle/dropdown
   collapsing, navigate dedupe, commit auto-flag, masked passwords, generated labels).
8. **App recorder wiring** — recorder.js injected dormant on every document, target-pane
   WebMessageReceived + NavigationCompleted capture, `AutomationController`.
9. **Sidebar overhaul** — collections/tasks/steps tree, WYSIWYG step editor (typed action
   dropdown, editable fingerprint), HTML5 drag-and-drop (reorder/nest/move/trash), record
   preview, Run/Dry Run/Validate/Continue, import/export dialogs; free-text LLM path demoted
   to "AI task (advanced)".
10. **LLM repair** — opt-in last resort for unresolvable steps (Run mode only, 6-iteration cap).
11. **README rewrite + version 2.0.0.**

**Known, acknowledged gap:** `AnthropicToolCallingLlm`/`OpenAiToolCallingLlm` are direct
vendor-SDK adapters, not routed through MindAttic.Legion — documented exception to HOUSE-LAW-4
until Legion grows tool-calling support.

### v3 phase 1 — WCAG 2.2 AA baseline for the existing sidebar (2026-09-03)

First phase of the v3 workflow-engine plan, and deliberately no new features: it brings the UI
that already exists up to Level AA before anything is built on top of it.

- **Design tokens** — new `wwwroot/tokens.css` is the one place a colour, space or type value is
  defined; `app.css` now references tokens throughout instead of ~40 repeated hex literals.
  `--radius` keeps working exactly as before.
- **Three real contrast failures fixed**, each verified by computed ratio rather than eyeballed:
  `.empty` at 3.73:1; the **primary button** (`#3c82ff` fill + white 13px text) at 3.61:1, which
  meant every default button in the app failed; and the tooltip, which sat at `opacity: 0` while
  staying in the accessibility tree. Accent and danger are now split into a `*-bg` fill variant
  (needs 4.5:1 against white) and a plain variant (needs 4.5:1 against the dark surface) — one
  value cannot serve both roles.
- **The tree is a real ARIA tree** — `role="tree"`/`treeitem` with `aria-level`,
  `aria-posinset`/`aria-setsize`, `aria-expanded`, `aria-selected`, using ARIA's flat-tree form so
  the render recursion did not need restructuring. Rows were previously plain `<div>`s that no
  keyboard could reach at all.
- **Full keyboard operation** — roving tabindex, arrow/Home/End navigation, type-ahead,
  Enter/Space activation, `Shift+F10` row actions (enumerated from the row's own buttons, so the
  two can't drift apart).
- **Dragging is no longer the only way** (SC 2.5.7, previously a hard failure): `Alt+↑/↓` reorders
  a step, `Alt+→/←` nests and un-nests it, and `Ctrl+Shift+M` (or the new `⇄` row button) moves a
  task between collections. Drag-and-drop remains as a shortcut.
- **24×24 pointer targets** (SC 2.5.8) — `.mini` buttons meet the minimum, and `.node` reserves
  `min-height` so revealing them causes no layout shift. The 10px insert-gap stays mouse-only and
  `aria-hidden` on purpose, relying on the criterion's *Equivalent* exception: the new 24×24 `＋`
  button on each step row does the same job and is keyboard-operable. Growing every gap instead
  would have added ~14px per step row.
- **Focus** — a `:focus-visible` ring exists for the first time (there was no `:focus` rule of any
  kind), dialogs trap Tab, and closing one restores focus to whatever opened it.
- **Accessible names and live regions** — every icon-only row button gets an `aria-label` from the
  string that already fed its tooltip; `#log` is a live region; a new coalesced `#sr-status`
  announces run progress without flooding a screen reader.
- **Consistent help** (SC 3.2.6) — a `?` button in the header, in the same place on every view.
- **Verification** — `tools/verify-ui.mjs` grew from 21 to **33** checks: an `axe-core` scan
  (devDependency in `tools/`, injected over CDP, never shipped in `wwwroot`), targeted assertions
  per success criterion, keyboard-operation checks, and a **floor check** that relaunches against
  an empty store and asserts the first-run tutorial still works with none of the new machinery on
  screen. 33/33 pass; the 92 NUnit tests are untouched and still green.

### v3 phase 2 — information architecture (2026-09-03)

Structural only: no new capability, but the shape the later phases need.

- **Resizable sidebar.** `MainWindow.xaml` gained a `GridSplitter` (default 420px, min 340, max
  720 — 380 could not hold binding chips, schedule rows or lane cards). Width persists as
  `AutomataSettings.SidebarWidth`, restored on launch and saved on splitter release *and* on
  window close, since a keyboard resize raises no drag event. `GridSplitter` is keyboard-operable
  out of the box, so the sidebar stays resizable without a custom accessible control. A
  settings.json written before this property existed still loads at the default width
  (regression-tested).
- **Four ARIA tabs** — Build / Schedule / Data / Runs — with manual activation per the WAI-ARIA
  APG: arrows move focus, Enter/Space selects. Inactive panels use the real `hidden` attribute so
  their content leaves the accessibility tree instead of merely going invisible. Build is always
  the default, and anything implying "look at the steps" (a run starting, recording beginning)
  pulls the user back to it. Schedule/Data/Runs are one-sentence empty states for now.
- **`panel.js` split into ES modules**, no bundler: `core.js` (state, bridge, model lookups),
  `modal.js`, `tree.js`, `editor.js`, `render.js`, `tutorial.js`, `tabs.js`, `settings.js`,
  `bridge.js`, and `main.js` as the entry point. WebView2 serves `wwwroot` over the
  `https://automata.local/` virtual host, so `<script type="module">` resolves siblings exactly
  as any static file server would — verified end to end before the split, not assumed.
  `render.js` and `tree.js` import each other; that is safe because both sides export hoisted
  function declarations and neither calls the other during module evaluation. Cross-module
  mutable flags live on an exported `ui` object, since an imported binding cannot be assigned to.
- **Verification** — 36/36 harness checks (up from 33; the new ones cover ARIA tab semantics,
  selection not being colour-only, and manual-activation keyboard behaviour). The floor check was
  updated deliberately rather than quietly: tabs are chrome, not an advanced concept, so it now
  asserts a first-run user *lands on Build* with the other panels hidden, and keeps asserting
  that every advanced concept stays off screen. 93 NUnit tests green.

### v3 phase 3 — four-scope engine settings + schema v2 (2026-09-03)

The first phase to touch `Automata.Core`'s model. Settings now resolve through
**global → collection → task → step**, and two of them (retry, continue-on-error) are live.

- **`EngineSettingsOverride`** on `Collection`, `TaskDefinition` and `Step`, plus
  `AutomataSettings.EngineDefaults` as the outermost scope. Every property is nullable and null
  means *inherit* — an entity carries only what it actually overrides.
- **`EngineSettingsResolver`** flattens the chain. Two rules, deliberately different: most
  settings are **deepest-wins**, but `MaxConcurrency` is **tighten-only** — the global value is a
  ceiling a deeper scope may lower but never raise, so one task cannot starve the machine.
- **`Floor()` is the contract.** With nothing overridden anywhere it reproduces the pre-scopes
  behavior exactly, and a test pins every field of it. Note the asymmetry it encodes: a failed
  step aborts its task, but a failed task does *not* abort its collection — which is precisely
  what `ReplayEngine` and `RunCollectionAsync` already did. That asymmetry is why the model has
  `ContinueOnStepError` **and** `ContinueOnTaskError` rather than one ambiguous flag.
- **Retry and continue-on-error are real.** `ReplayEngine` retries a failed step per the resolved
  `RetryPolicy` (announced in the run log, never silent) and honours `ContinueOnStepError` for
  *siblings* — a failed step's own children still never run, because its post-condition did not
  hold. `RunCollectionAsync` honours `ContinueOnTaskError`. All default to today's behavior.
- **Per-step resolution** rides on a new `ReplayOptions.ResolveForStep` delegate. Callers that
  do not supply one (every existing test) fall back to the scalar properties, so an options
  object built the old way behaves exactly as it always did. A per-step `TimeoutMs` still beats
  the resolved chain — it is the most specific statement anyone made about that one step.
- **Schema v1 → v2** is purely additive, so migration is **lazy**: files are stamped with the
  current version when written for some other reason, never rewritten en masse on first launch.
  Opening v3 against an existing `Documents\Automata` touches nothing until the user edits
  something (regression-tested). `CollectionStore.WriteJson` is the single write path, so
  stamping and normalization cannot be bypassed — including by the store's own hand-edit healing.
- **Empty overrides are pruned** on both sides of the bridge. A scope that overrides nothing is
  dropped rather than persisted, so a task nobody has configured never *looks* configured.
- **The settings UI enforces the inherited/overridden distinction.** An inherited value renders as
  read-only text naming the nearest scope that set it, plus an explicit Override button; only then
  does a control appear, paired with a named Reset. Overriding seeds from the value already in
  effect, so taking ownership never changes behavior by itself. Reachable from a `⚙` in each
  collection and task row's hover-revealed buttons, `⚙ settings` in the step editor, and
  "Engine defaults…" in the Settings dialog. Only settings the engine honours today are offered —
  `MaxConcurrency`, `Isolation`, `BrowserProfile` and `ScreenshotOnFailure` are modelled but stay
  hidden until the phases that make them real, because a control that does nothing is worse than
  no control.
- **Verification** — 37/37 harness checks (the new one drives the full override → disk → reset →
  pruned-from-disk cycle) and 116 NUnit tests. Two floor-check assertions were updated on purpose:
  the per-scope cogs now exist in the DOM, so the invariant on them became *visibility* rather
  than presence; and a freshly written file carries schema 2, so the version assertion tracks the
  current version while the "no new fields" checks around it do the real work.

### v3 phase 4 - bindings, outputs, datasets, run store (2026-09-03)

The phase where `ExtractText`'s captured value stops being dropped.

- **A step can publish a named output** (`Step.Outputs`) and a later step can **bind its value to
  it** (`Step.Bindings`). Within one task this works end to end today: the engine keeps published
  values in its run state, resolves bindings before performing a step, and a `Prefix`/`Suffix`
  pair wraps the resolved value (`"https://shop.example/item/" + sku`). That prefix/suffix pair is
  the ONLY composition the binding model allows; anything more belongs to the authoring layer.
- **Bindings are selected, never typed.** The picker enumerates the outputs declared by steps that
  run *before* this one, so a binding cannot name something that does not exist or has not run
  yet. An environment variable is the one source whose name only the user knows, and it is asked
  for through the ordinary rename dialog rather than a formula box.
- **An unresolvable binding fails the step with a reason** rather than silently falling back to the
  literal beside it. Cross-task, dataset and task-input bindings say plainly that they need the
  workflow engine, which is not wired up yet.
- **`Step.Masked` withholds a value entirely** — not a scrub. A partial scrub that misses one
  interpolation is worse than a generic message, so a masked step reports no message and a
  redacted value. It still publishes its output, because masking hides a value from watchers, not
  from the run.
- **`Wait` is real** for a duration and until a time of day. `MillisecondsUntil` is a pure function
  so the DST cases are testable without waiting: a spring-forward gap takes the first valid
  instant rather than throwing. A wait longer than `ParkAfterMs` says plainly that the browser
  stays occupied until run parking lands, instead of appearing to hang.
- **`ForEach` / `If` / `RunTask` / `WriteDataset` are modelled and defensively rejected** with a
  message naming what they need. They are deliberately absent from the action picker: a control
  that fails at run time is worse than no control. `Wait` is the only new action offered, and it
  sits in a **collapsed "Flow control" group** below the original fourteen, never at the top level.
- **`DatasetIO`** — hand-rolled RFC 4180 CSV plus JSON arrays. Appending a row with a column the
  file has never seen rewrites against the union rather than dropping it; silently losing a value
  would be the worst of the three options.
- **`RunStore`** — `Runs\<yyyyMMdd-HHmmss>-<slug>-<id8>\` with a manifest, per-task
  `events.jsonl` and `outputs.json`, and a datasets folder. The timestamp-first directory name
  makes "newest first" an ordinary name sort. Nothing is created until a run actually starts.
- **Verification** — 38/38 harness checks and 158 NUnit tests. The new harness check drives the
  whole binding flow through the UI: declare an output, bind a later step to it, confirm the
  binding reached disk and the field became a chip, then unbind and confirm no empty `bindings`
  object was left behind. It immediately earned its keep by catching a CSS specificity bug —
  `button.mini { display: inline-flex }` outranked `.binding-toggle { display: none }`, so the
  toggle was permanently visible.

### v3 phase 5 - the workflow engine (2026-09-03)

Control flow executes for real: `if`, `forEach`, `runTask`, `writeDataset`, and a wait on a
condition. Still one browser lane - the lane pool is the next phase.

- **`WorkflowEngine` owns the tree walk; `ReplayEngine` owns one step.** That split is the whole
  design. A control-flow step decides for itself whether and how many times its children run,
  which a walker embedded in the step executor cannot express. So `ExecuteOneAsync` does exactly
  one step - pause gate, bindings, retry, masking, bookkeeping - and everything about ordering
  moved up. Nothing is duplicated: a plain task run still goes through the same executor.
- **A rejected alternative worth recording:** expanding `forEach`/`runTask` into a flat step list
  before running, so the existing engine could stay untouched. It fails on the case that matters -
  an `if` or a wait-until-condition depends on a value extracted *during* the run, so it cannot be
  decided ahead of time.
- **`WorkflowEngine.RunAsync` emits the same `StepEvent` stream** as a plain replay, so it is a
  drop-in swap for the caller. Lane-scoped events wait until there is more than one lane to talk
  about.
- **Conditions** are one comparison, evaluated against bound values. A numeric comparison strips
  currency symbols and separators first, because text read off a page is "$19.99", not 19.99 -
  "price less than 20" behaves the way anyone writing it expects.
- **`forEach`** reads a dataset and publishes each row's columns twice: bare (`sku`) and qualified
  (`row.sku`), so the common single-loop case stays short while a nested loop can disambiguate.
  Row variables are restored, not cleared, on exit - so an outer loop's row survives an inner one.
  Asking for concurrency that does not exist yet is said out loud in the log, not ignored.
- **`runTask`** runs another task's steps inline, sharing run state so outputs flow across the
  call. A task that reaches itself is stopped with a reason rather than looping.
- **`DatasetStore`** - one browsable folder (`Documents\Automata\Datasets`), overridable by
  `AUTOMATA_DATASETS_ROOT`. A dataset is a file; dropping a spreadsheet export in is the whole
  import workflow. The **Data tab** lists them with row and column counts.
- **The five flow actions are now offered**, because they now work, still inside the collapsed
  "Flow control" group beneath the original fourteen. Each has a real editor: a condition row with
  picker-driven operands, a dataset dropdown, a task dropdown, and a column list for
  `writeDataset`.
- **Verification** - 40/40 harness checks and 176 NUnit tests. The new harness checks run a real
  `forEach` over a two-row CSV through the app and assert the per-row log lines, and confirm the
  Data tab reports the dataset. The flow fixture lives in its own collection so it cannot disturb
  the pass/fail counts the older checks assert on.

### v3 phase 6 - natural-language authoring (2026-09-03)

Describe a workflow in prose and get real, editable steps. Two standards adopted rather than
invented, which was the explicit instruction and turned out to be the right call.

- **Gherkin is the authoring surface**, via the official `gherkin` package (42.0.1, Cucumber's own,
  no dependencies of its own). Its hierarchy already IS Automata's: `Feature` -> Collection,
  `Scenario` -> Task, step -> Step, `Background` -> steps prepended to every task,
  `Scenario Outline` + `Examples` -> a for-each over a dataset, tags -> scoped engine settings.
  Nothing was bent to make that line up.
  **This is Core's second external dependency** (after MindAttic.Legion) - a deliberate exception,
  noted here beside the HOUSE-LAW-4 one. Hand-rolling a Gherkin parser (localized keywords, tables,
  doc strings, tags) is real work for no gain.
- **`StepDefinitionCatalog` is what makes it Automata's Gherkin rather than Cucumber's.** In
  Cucumber the step definitions are user-written and the language is open; here the phrase table
  ships fixed, so validation is total - an unrecognised phrase is a diagnostic with a line and
  column, never a guess. The same table is rendered into the LLM prompt, so what the model is told
  it may write and what the compiler accepts cannot drift apart.
- **The two non-obvious mappings**, both tested directly:
  *Gherkin is flat, a step tree is nested.* There is no `if { }` block, so a **guard step becomes
  an `If` and the rest of the scenario becomes its children** - which is how idiomatic Gherkin
  already expresses "only do the rest when...". `GherkinWriter` inverts it.
  *A written target cannot invent a recorded fingerprint.* It compiles to a **partial** one, which
  is exactly what the resolver's tail strategies (aria label -> label text -> visible text) already
  handle, and self-heal upgrades it to a precise identity on the first successful run. An authored
  step gets more robust the first time it executes.
- **Fidelity is stated, not assumed.** Compiling in is total; rendering out is best-effort. A task
  whose steps carry recorded fingerprints, or whose tree nests in a way Gherkin cannot express,
  renders for reading and is flagged **read-only with the reasons** rather than silently degrading
  when recompiled. `*.automata.zip` remains the lossless format.
- **`FlowAuthoringService`** turns prose into a feature, then **repairs against its own
  diagnostics** - line numbers and all - up to three attempts before handing the failure to the
  user. The repair loop is the reason for having a checkable intermediate artifact at all.
- **Nothing is saved until it is reviewed.** The preview shows the feature text beside the step
  tree it compiles to, with the guard's nesting visible. Editing the feature and re-checking
  compiles it **directly, never back through the model** - a hand edit is held to the same
  standard, not re-rolled.
- **Chrome DevTools Recorder JSON** import and export (`@puppeteer/replay` schema). Its `selectors`
  array - a list of `aria/`, `text/`, `xpath/`, `pierce/` and CSS alternatives - is essentially
  Automata's multi-strategy fingerprint written down by someone else, so all of them survive rather
  than one winning. Unsupported step types and `pierce/` selectors are reported, never dropped
  quietly. It rides on the existing Import/Export dialogs as a second file type rather than a tenth
  toolbar button.
- **Verification** - 44/44 harness checks and 223 NUnit tests, including a round-trip property
  (compile -> write -> compile gives the same shape) and a check that every phrase the catalog
  advertises actually matches the catalog. The new harness checks drive the whole pipeline in the
  real app except the model itself: compile hand-written Gherkin, review the preview, insert, and
  confirm the guard compiled to an `if` with a bound condition on disk - plus that a bad phrase
  comes back with its line number and no Insert button.

### v3 phase 7 - lanes, parallelism, and the headless runner (2026-09-03)

Automata can now run tasks with no desktop app open, across several browsers at once.

- **`Automata.Browser`** - a new `net10.0-windows` project holding `WebView2BrowserSurface` and
  `DomFileInjector`, moved out of the app so the runner shares ONE browser implementation rather
  than growing a second one to keep behaviourally identical. `Automata.Core` stays plain `net10.0`
  and never references it; `IBrowserSurface` is still the seam.
- **`OffscreenWebView2LaneFactory`** - one lane per STA thread, each with its own message pump,
  window and user-data folder. **Verified by actually running it**, not assumed: a hidden lane
  navigated to a local fixture, resolved `#alpha`, and a coordinate-based CDP click landed.
  The window shape is the load-bearing detail. It is an ordinary `WS_POPUP` window at
  (-32000,-32000) sized 1280x900, **not** `HWND_MESSAGE`: a message-only window has no client
  area, so WebView2 never lays out, and every Click / PressEnter / checkbox step dispatches input
  at coordinates computed from a rendered box. With no layout there is nothing to hit.
- **`BrowserLanePool`** - bounded, and lanes are pooled *per profile* rather than created per
  lease, so a login stays warm for the next task that wants it. The bound is the backpressure that
  stops a fifty-thousand-row dataset from trying to open fifty thousand browsers.
- **Parallel `forEach`.** Two gates must open: the resolved Max-concurrency ceiling (a
  machine-resource decision, and the reason one task cannot starve the box) and the loop's own
  request. When the ceiling is what is holding a loop back, the run **says which one** - a
  silently ignored setting is exactly what makes people stop trusting the knob.
- **A row always runs in its own scope**, sequentially as well as in parallel. If a row's outputs
  leaked out when run one at a time but could not when run together, raising the concurrency of a
  working loop would change its results. That asymmetry is the kind of surprise worth designing
  out, so nothing published inside a loop is visible after it - which row's value would it be?
- **`RunnerCliDispatcher` lives in Automata.Core, not in the exe.** The exe must be
  `net10.0-windows` to host a browser and the test project is plain `net10.0`, so putting the
  logic in Core is what makes the CLI surface, the argument handling and the run orchestration
  unit-testable. `Automata.Runner/Program.cs` is a thin host - the same split the WPF app uses.
  `run --task` / `run --collection` / `status`, exit codes 0/1/2/3.
- **A bug caught in review rather than by a test:** the workflow engine briefly held the lane pool
  in a field. It is a DI singleton, so two concurrent runs would have clobbered each other - and a
  parallel for-each swaps the browser per row. Replaced with a `RunScope` record threaded through
  the walk.
- **Verification** - 256 NUnit tests (up from 223) and 44/44 harness checks. The pool is tested
  for reuse, isolation, its ceiling under twenty concurrent callers, and a double-release that
  would otherwise leak a permit; parallel loops for every row running exactly once, the tighter of
  the two limits winning, row isolation, and a failing row's events still reaching the caller.

### v3 phase 7b - the Runs tab (2026-09-03)

- **Every run is now recorded**, from the app as well as the runner. A collection run is ONE record
  covering its tasks rather than one record each; a lone task opens and closes its own. Extracted
  values are saved with the run, which is where they finally stop scrolling out of the log.
- **The Runs tab reads from the run store on disk**, not from anything the window remembers - which
  is exactly what lets it show runs it never saw, including ones `automata-runner` produced while
  the app was closed. Outcome is carried by glyph, word and colour, never colour alone.

### v3 phase 8 - scheduling (2026-09-03)

**A schedule fired on time, ran a real browser task in an off-screen lane, and recorded the run.**
Verified end to end with the real executable, not just unit-tested.

- **`CronSchedule`** - five-field cron with `*`, lists, ranges and steps, hand-rolled to keep Core's
  dependency count honest. It steps minute by minute rather than reasoning about month lengths,
  weekday alignment and DST all at once: obviously correct beats clever, and a year of candidates
  is well under a millisecond of work. Cron's oldest wart is pinned by a test - when BOTH day
  fields are restricted, a match on *either* counts.
- **DST is handled, and tested against a synthetic zone** rather than whatever the machine's
  timezone database happens to hold: a time inside a spring-forward gap is skipped rather than
  fired at a moment the wall clock never showed.
- **An expression that can never fire is refused when it is scheduled**, with the reason. A
  schedule that quietly does nothing is the worst possible failure mode for this feature, so
  `schedule add --cron "99 * * * *"` exits 3 and stores nothing.
- **Due times are written down, not recomputed from "now".** That is what lets a firing survive the
  process not running between ticks: a tick three minutes late still honours it. A firing missed by
  a long outage is **skipped by default** - a batch of missed runs all firing at once after a
  machine was off is rarely what anyone meant by "every hour" - with `RunOnceImmediately` available
  for the cases where it is.
- **Intervals are anchored**, so an hourly job stays on the hour instead of drifting later after
  every restart.
- **Chains work**: "after the ingest finishes, reconcile; after that, publish". Followed rather
  than forbidden, but an entry runs at most once per tick, so a cycle exhausts itself instead of
  looping forever. `--after` can also key off failure, for an alert task.
- **`tick` is the only thing Windows Task Scheduler ever invokes.** All the cron, interval and
  after-this-finishes reasoning happens in-process, which is what lets Automata express schedules
  `schtasks` has no vocabulary for while the registered task stays a dumb "run this exe every N
  minutes".
- **`install` registers with an interactive token (`/IT`), never "run whether user is logged on or
  not"** - and says why in the output. That flag runs the task in session 0, where WebView2 cannot
  render, so the task would start on time, open nothing, and fail every step. Registration is
  behind `IScheduledTaskRegistrar` so the CLI is testable without touching the machine's real Task
  Scheduler.
- **Verification** - 318 NUnit tests (up from 256) and 51/51 harness checks, plus a real end-to-end
  scheduling run: add an interval schedule, watch `tick` report "Nothing due", wait for it to come
  due, and see it run a browser task and land in `status`.

### Phase 8b - the Schedule tab

Schedules stopped being CLI-only. The sidebar's third tab is now a real editor, and the tree says
which collections run on their own.

- **A schedule is assembled from pickers, and COMPILES to cron.** "Every weekday at 09:30" is a
  choice plus a time; it becomes `30 9 * * 1-5`, and the expression is shown read-only beneath the
  picker rather than hidden behind it - it is what gets stored, what `schedule list` prints, and
  what travels if the schedule moves to another machine. Custom cron is one more entry in the same
  picker for people who want it, not the only way in. Editing an entry reads the shape back OUT of
  the expression, so a nightly job reopens as "every day at 09:00"; anything the patterns do not
  recognise (a CLI-authored expression, a hand-edited `schedule.json`) is honestly shown as custom
  rather than approximated into the nearest picker.
- **Nothing in the sidebar works out when anything fires.** Every due time, every "next in 3h", and
  every chain preview is computed host-side by the same `TriggerEvaluator` the runner's `tick`
  obeys, and pushed down already resolved. A sidebar that did its own cron arithmetic could
  disagree with the thing that actually runs, and a schedule preview that lies is worse than none.
- **Everything unfireable is refused with a reason, before it is stored** - the same posture the CLI
  already had. A bad expression, one that never matches a real date (`0 9 31 2 *`), a one-off time
  already in the past, an entry waiting on itself, and a chain that would close into a loop each
  come back with a sentence saying why. A refusal REOPENS the editor with everything still typed
  in it, so nothing is lost to a rejection.
- **Bookkeeping is not the panel's to write.** Last-run time and outcome are taken from the stored
  entry, not from whatever the sidebar was last shown, and the written-down next-due time is only
  recomputed when the triggers actually changed - so a firing missed while nothing was running
  survives an unrelated edit (a rename, a pause) instead of being quietly pushed forward.
- **The tree carries a chip for anything scheduled** - `⏰` for a clock, `⛓` for a chain, `⏸` when
  paused - naming the schedule and its cadence in its accessible name, so a glance at Build says
  which collections run on their own. Compact by design: it fits inside the 28px a row already
  reserves for its buttons, so a scheduled row is not a different height from an unscheduled one.
- **Chains are previewed in the order the tick would follow them**, straight from
  `TriggerEvaluator.Chain`, and deleting an entry that others wait on says which ones will no
  longer be started by anything.
- **The tab says plainly that nothing fires until `automata-runner install` has been run.** The app
  is the editor and the monitor; the runner is what Windows calls. Claiming otherwise would be the
  most expensive kind of lie this feature could tell.
- **Verification** - 6 new harness checks (51/51 total), covering the compile-to-cron path, the
  refusal-with-a-reason path, chain previews, pause keeping its trigger, named 24x24 row controls,
  and the tree chip not growing its row. The harness now also points the app at a scratch
  `schedule.json` (`AUTOMATA_SCHEDULE_PATH`) instead of the developer's real one, and refuses to
  start when a previous run's WebView2 is still serving CDP on a port it needs - attaching to a
  stale panel silently drives the wrong app and reports mysteries.

### Phase 8c - park-and-resume for long waits

A `wait until 09:00` used to hold a browser for nine hours. Now it writes down where the run had
got to, gives the lane back, and a later tick finishes it.

- **A wait longer than its step's `parkAfterMs` (15 minutes by default) checkpoints instead of
  sleeping.** The run emits `RunParked` and stops - deliberately NOT `RunCompleted`, because it has
  neither passed nor failed. Its manifest stays open, its lane is released, and the process is free
  to exit. `tick` resumes whatever is due, and does it *before* looking at schedules and *without*
  needing one to exist: a half-finished run matters more than starting something new, and a
  manually started run can park just as easily as a scheduled one.
- **The checkpoint is an index path plus the values in scope** - `[3]` is the fourth top-level step,
  `[3, 1]` the second child of it - and it carries the step id at that address. Resuming has to
  continue with what FOLLOWS the wait, which a step id alone cannot locate; the id is what catches
  a task edited during a nine-hour wait, which would otherwise resume into whatever step now sits
  at that index. That case is refused with the reason, not guessed at.
- **Parking is refused where a checkpoint would be a lie.** Inside a for-each, one address and one
  set of values cannot say which rows had finished; inside a called task, an index path from this
  task's root does not address anything. Both hold their browser and say exactly why, rather than
  resuming approximately and re-running or skipping rows. An `if` is fine and does park - it is one
  branch taken once - and resuming re-enters the branch WITHOUT re-evaluating the condition, which
  would otherwise send a resumed run down the other path.
- **Parking discards page state, and the run says so both times.** The browser is gone - that being
  the point - so a resumed run re-navigates to the task's start URL and states plainly that
  anything done to the page before the wait no longer applies. A task that must keep a session it
  logged into sets `parkAfterMs` to 0 and holds its browser instead; that is the documented opt-out,
  not a workaround.
- **A parked collection carries on through the rest of its tasks.** The checkpoint remembers the
  tasks queued behind the parked one and the tallies from before it, so a resumed collection still
  reports "2/2 task(s) passed" rather than counting only what ran after the wait.
- **One definition of "how long is left".** `WaitPlan` is the single place a wait's end is computed,
  shared by the replay engine that performs a wait, the workflow engine that decides whether to
  park, and the runner that decides what is due. The runner also hands the engine its own `IClock`:
  a run measuring a wait against a different clock from the tick meant to resume it would park until
  a moment the tick never agrees has arrived.
- **The app does not park - on purpose.** Parking exists to give a pooled lane back, and the window
  has exactly one browser pane, which is not pooled. Releasing it would free nothing and would make
  a run the user is watching vanish. So the app holds the pane, says so, and the Runs tab instead
  *shows* what the runner parked: a parked run's manifest is open, which on its own is
  indistinguishable from one still executing, so the parked record is joined in and the row reads
  "parked", names the step, and says when it resumes.
- **Verification** - 24 new NUnit tests (342 total) and one more harness check (52/52), all against
  a fixed clock, plus a real end-to-end run: a task with a 70-second wait parked, released its lane,
  reported "Nothing due" on an early tick, and was resumed and finished by a tick after the wait.

### Phase 8d - the live lane strip

`BrowserLanePool.Snapshot()` could always answer "which lane is running what". Nothing surfaced it,
because the answer was in the wrong process.

- **The lanes worth watching are never the app's.** The window has one browser pane and no pool;
  the pool lives in `automata-runner`, which is headless and usually running unattended. So the
  pool got a change callback, `LaneMonitor` writes what it reports to a small file per process
  (`Documents\Automata\Live\<pid>.json`), and the Runs tab polls for it. The pool itself still
  knows nothing about storage - it reports, the caller persists, the same split the engine and the
  run store already use.
- **Published on every change of hands, not on a timer.** A lane changes hands on acquire, release
  and each step start - a few times a second at most - so the strip updates when something actually
  happens rather than up to a poll-interval late. `Describe` now routes through the pool for exactly
  this reason: without it the strip could name the task but never the step, which is most of the
  value of a live view.
- **A monitor that shows work which is not happening would be worse than no monitor**, so the
  reader never trusts the file. Liveness is checked against the process id AND that process's start
  time - a pid alone is not an identity, since Windows reuses them - and a file left behind by a
  killed run is deleted as it is read rather than shown. A graceful exit removes its own file, so
  the common case is immediate.
- **Only busy lanes are rows.** A returned-but-open lane is counted as "warm" instead, which is
  what explains a browser count higher than the work in flight - lanes are pooled per profile so a
  login survives into the next lease.
- **Polling starts and stops with the panel.** Nothing polls while the Runs tab is off screen,
  including when a starting run pulls the user back to Build. And the strip is absent from the DOM
  entirely when nothing is running - an empty widget teaches a first-run user nothing, and the floor
  check requires exactly that.
- **`status` gained the same view**, grouped per process and ahead of anything historical, so the
  feature is usable without opening the app.
- **Verification** - 12 new NUnit tests (354 total) and one more harness check (53/53). The harness
  seeds both a live process (using its own pid and real start time, since a made-up one is correctly
  discarded) and a killed one, and asserts the phantom neither renders nor survives the read.

### Two bugs this phase's measurements turned up

- **Every tree row grew 2px the moment it was hovered or focused**, nudging everything below it -
  the "padding shift" behind `+ add step`. `.node` reserved `min-height: 28px` for a 24x24 row
  button but `box-sizing: border-box` means its own `3px` padding came out of that, leaving a 22px
  content box. Now `2px`, so the content box is exactly 24px and the row stays 28px throughout.
  The existing 2.5.8 check missed it for a year because it focused the row and THEN measured the
  "before" height, comparing a grown row against itself; it now blurs and un-hovers first.
- **The insert-zone's hairline struck through its own label.** Three layers back to front - line,
  an opaque patch matching the tree background, then the label - so the line stops either side of
  the text. The alternative, splitting the line into two absolutely-positioned halves, would have
  had to know the text's width.

### Still to do in v3

- **Multiple triggers on one entry.** The model has always been a list, and the evaluator already
  takes the soonest firing across all of them, but the editor writes exactly one - so "every
  weekday at 09:00 *and* after the ingest" needs the CLI or a hand edit today.

## Not done yet

- **Interactive end-to-end verification** — build+launch smoke test passed (no crash), but the
  full loop (record a Google search → refine → Validate → Dry Run → Run → export/import →
  re-run) needs a human at the keyboard. The original Phase-1 caveat about the postMessage
  bridge being only smoke-tested still stands.
- **Acceptance scenarios as saved profiles**: Google search → titles, Bing search → titles,
  webmail inbox → first 20 subject lines.
- **Fingerprint heuristic tuning** against real sites (auto-generated id/class reject patterns).
- **Orchestration (old Phase 4)**: multiple concurrent panes/instances with separate
  userDataFolders running the same task with different parameter bindings; templated parameters
  (`{{query}}`) in step values.
- **v2 limitations to lift later**: cross-origin iframes & shadow DOM piercing; file locking
  for multi-instance.
- **Known perf cleanup** (fine at current scale, flagged by code review): every panel mutation
  re-scans the whole store (`PushStateAsync` → `LoadCollections` + per-collection `LoadTasks`,
  each id lookup re-enumerating directories). Fix when stores get large: an id→path index
  invalidated on write, and pushing only the affected subtree.
