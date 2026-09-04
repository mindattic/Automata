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

### Phase 8e - several triggers on one schedule

The model was always a `List<TriggerDefinition>` and `TriggerEvaluator.NextAcross` always took the
soonest firing across it. The editor was the part that wrote exactly one, so "every weekday at
09:00 **or** once the ingest has finished" needed the CLI or a hand-edited `schedule.json`.

- **The editor is a list now**: one boxed block per trigger, add and remove, each with its own
  shape picker, its own fields and its own compiled-cron line. Numbered only when there is more
  than one - a single trigger is the overwhelmingly common case and should not be dressed up as a
  list - and the last one is never removable, because an entry with no trigger runs solely by hand,
  which is a different thing from a schedule.
- **They are alternatives, and the form says so.** "Any one of these starts the run - whichever
  comes first. They are not steps and they do not wait for each other." Rows describe every
  trigger joined by ", or" for the same reason, and the glyph goes to the clock when a clock is
  involved at all: "it runs on its own" is what is worth seeing at a glance.
- **Every control is scoped to its trigger.** `data-trigger` is what tells the change handler which
  one to mutate, and the accessible names carry the number too - a form with three time pickers
  must not offer a screen reader three controls called "Time of day". The harness asserts no two
  controls in the dialog share an accessible name.
- **Bounded at 8, in both halves.** Several are the point; past that a schedule is easier to read
  as two, every trigger is evaluated on every tick, and the cap is enforced host-side as well so a
  hand-edited file cannot be saved back as something unreadable.
- **Mixing kinds works and is now tested**: a clock trigger and an after-entry trigger on the same
  entry answer different questions and must not interfere - the clock gives it a due time of its
  own, and the upstream finishing starts it regardless.
- **Verification** - 2 new NUnit tests (356 total) and one more harness check (54/54), covering
  both triggers reaching disk, each block editing only its own trigger, reopening as the shapes
  they were built with rather than raw cron, and removal keeping the right one.

### Phase 9 - harvesting a page into a dataset, and generated examples (2026-09-04)

The gap this closes: everything in the data model could already fan out over a dataset and write
results back, but a dataset could only come from a **file a human put there**. `WorkflowEngine`
read `ForEach.Source.DatasetName` and nothing else, so there was no way to loop over a list
gathered **while browsing**.

- **`StepAction.ExtractAll`** (`HarvestSpec`, `HarvestField`, `HarvestSource`) reads many rows off
  the current page and writes them to a dataset. It joins the collapsed flow group, NOT the
  original fourteen, so the floor is untouched.
- **The dataset is the seam, deliberately.** A harvest writes the same CSV a hand-dropped
  spreadsheet would, so looping, conditions, parallel lanes, park/resume and the Data tab all keep
  working with no new machinery. A file also survives parking with no serialization of engine
  internals, and it can be opened in Explorer and checked *before* the loop that consumes it runs.
- **Nothing is typed.** `harvest.js` walks OUTWARD from one clicked element to the first ancestor
  that has siblings of its own kind — that ancestor is the row — and generalises away the clicked
  element's own id, text and framework hash classes. The count it matched is reported back and
  shown in the editor before anything is stored. Field selectors are relative to the row.
- **A harvest that read nothing usable FAILS.** Zero matches, or rows matched with every column
  blank, are refused with a reason rather than written as an empty dataset — an empty dataset lets
  the ForEach that consumes it loop zero times and report a clean pass, which is the most expensive
  way this engine can be wrong. `HarvestRunner.Shape` holds those rules and is pure, so they are
  unit-testable with no browser in sight.
- **Generated examples** (`DemoPages`, `DemoTasks`, `DemoSeeder`): local HTML written to
  `Documents\Automata\Demos` plus a matching "Demos" collection, seeded on first load and
  regenerable from Settings -> Examples. Local pages rather than live sites, because a demo whose
  job is to prove harvesting works cannot also be a bet on someone else's markup, consent banner
  and rate limiter. `buttons.html` is deliberately the same three-button page as
  `tools/verify-ui-fixture.html`, but the harness still writes its own copy — folding the two into
  one asset set is left for when there are enough demo pages to be worth it.
- **Regeneration never eats work.** An untouched example is refreshed silently; an edited one is
  left alone unless answered, with three answers: keep mine, restore the original, or keep mine +
  add the original beside it. *(Superseded in phase 12 - regenerating is wholesale now.)*
- **`DemoOrigin` records a content hash** over what a demo DOES - steps, start URL, settings.
  *(Phase 12 folded name and description into the hash too, since restoring puts those back.)*
  Demo step ids are FIXED, not generated, or every regenerate would either break every binding or
  look like a hand edit.
- **The first-run tutorial survives.** `maybeStartTutorial` now ignores the generated collection
  (`state.demoCollectionId`, pushed by the host), because "this person has built nothing yet" is a
  different question from "this person has nothing but the examples we made for them". Seeding
  without this would have silently suppressed the tutorial - the one invariant this project does
  not trade away.
- **`tools/verify-shop.mjs`** - the three-way acceptance check: harvest 12 products, visit each in
  turn collecting prices, do it again with 4 lanes, and require **sequential == parallel == what
  the generated pages actually say**. Comparing the two runs to each other is not enough; both can
  skip the same rows and agree perfectly while being wrong, so the pages are read off disk for the
  oracle ($457.50). It also asserts the parallel run was not silently throttled to one lane, since
  a throttled run would agree with the sequential one for entirely the wrong reason.
- **`AUTOMATA_SETTINGS_PATH`** - the one store with no environment hook, which meant a scratch run
  read and wrote the developer's real `settings.json`. Added, and the UI harness now uses it (so it
  can finally open Settings), along with `AUTOMATA_DEMOS_ROOT`.

### The concurrency bug the three-way check found on its first real run

`MaxConcurrency` is **tighten-only** by design - the global value is the machine's ceiling and a
task may only lower it - so the parallel example needs the ceiling granted before it demonstrates
anything. Once it was, four lanes appending to one dataset produced:

    The process cannot access the file 'shop-prices-parallel.csv' because it is being used by
    another process.

Ten rows of twelve, total $426.95 against a true $457.50. `DatasetIO.Write` is a read-modify-write
(read the rows, work out the union of columns, write it all back), so racing writers lose whichever
update finished first, and on Windows they usually collide on the handle instead.

Fixed with **`ExclusiveFileLock`**, which serialises across threads AND processes: a per-path
in-process semaphore (a parallel for-each is several rows in ONE process) plus a sentinel file
opened `FileShare.None` with a retry (the app and the runner are separate executables over one
workspace). The lock spans the whole read-modify-write, not just the write - locking only the write
would still let two writers read the same "before". `Read` takes it too, so a reader never catches
a full-file rewrite half-done. Sentinels live in the system temp folder keyed by a hash of the full
path, not as `.lock` files beside the data, because datasets are meant to be browsable in Explorer.

This is the "file locking for multi-instance" item that had been sitting in **Not done yet** since
v2. It was found by measurement, not by reading, which is the second time this has happened - and
both times the test that found it was one that compared against an independent oracle rather than
against another run.

### Phase 10 - an example of everything, enforced by the build (2026-09-04)

The demo batch stopped being three examples and became the coverage checklist. **Adding a
`StepAction`, `WaitMode` or `ConditionOp` now fails the build until a seeded example demonstrates
it** (`DemoCoverageTests`) - the same mechanical trick as the sidebar's floor check, aimed at a
different failure. A capability with no example is one nobody finds: it ships, it is never used,
and it rots until the day somebody tries it.

Five new examples and four new generated pages fill the gaps that check exposed:

- **Fill in a form** (`form.html`) - one of every input control on one page, so the ten actions
  that touch a field all have somewhere to be seen: keystrokes, a direct value set, the Enter key
  with no target (it goes to whatever has focus, the way a search box wants), a box ticked, a box
  unticked, a radio, a dropdown, and a file attached. `notes.txt` is generated beside the pages so
  the upload has something real to attach - an example that told the user to go and find a file of
  their own would not run. The summary appears 900ms after submit, deliberately, so there is
  something to wait for.
- **Wait for a page that is not ready** (`slow.html`) - the three shapes of waiting: a flat
  duration, an element that does not exist yet, and a condition over a value already read.
- **Check an order before shipping** (`order.html`) - nine `if` steps, one per comparison the
  picker offers. Each check that holds writes its own row to `order-checks.csv`, which is what
  makes the example self-evidencing: **nine rows means all nine branches were taken**, and a
  condition that quietly did not hold is otherwise indistinguishable from a task that passed.
- **Run two other examples** - `runTask`, which is why the next item had to change.
- **Start at a set time** - the one example that deliberately does not finish. Parking is the
  property being shown, and there is no way to show "hours pass with nothing held open" in two
  seconds.

**Demo task ids are now fixed** (`demo-<key>`), for exactly the reason step ids already were: a
`runTask` step names a task BY id, so an id decided at seed time would leave the demo that calls
another demo unwritable. The seeder gained `Reidentify` for the consequence - the store keys a task
file by the id inside it, so writing a changed id straight over the old file reads as a different
task landing on an occupied name and the store would dutifully keep BOTH. Cloning an edited demo
now hands the fixed id to the pristine copy along with the marker: an id, like the marker, names
the demo rather than the work somebody built on top of it.

`WaitMode.UntilSignal` is the single entry in `NotDemonstrable`, with its reason in code: the
engine refuses it outright ("waiting for a signal needs the scheduler, which is not built yet"), so
an example of it would be an example that hangs. A stale exemption fails too - once something IS
demonstrated, its exemption has to go.

**`tools/verify-demos.mjs`** is the fourth gate. `DemoCoverageTests` can only prove an example was
*written*; this one runs every example in a real browser and checks what it left behind - nine
distinct rows in `order-checks.csv`, every recorded value one that `order.html` actually prints,
and the parking example parking rather than ploughing on. It also fails when a new example is added
with no entry saying where it is covered. The shop pair is skipped by name because `verify-shop.mjs`
already checks their answers against an oracle, which is a stronger claim than exiting zero.

One harness fix fell out of the bigger demo tree: the hover-gap geometry check now scrolls the gap
into view *before* its baseline snapshot. Hovering was scrolling it there itself, and every row
shifting by the same 133px is the tree scrolling, not the gap pushing its neighbours around.

Two limitations the examples made plain, both recorded under **Not done yet**: a condition wait can
only ever hold immediately or time out, and a called task starts on whatever page the caller left
open.

### Phase 11 - a collecting loop can start its dataset fresh (2026-09-04)

An appending loop was not repeatable: running the shop example twice left it holding twenty-four
products and double the money, and the only way back was deleting the file by hand.
`verify-shop.mjs` had been sidestepping it with a fresh scratch workspace every time, which is
exactly the kind of workaround that hides a real defect from everyone except the person who wrote
it.

**`DatasetWriteSpec.ResetOnFirstWrite`**: the first write of the RUN replaces the dataset and every
write after it appends. Not the first write of the step, and not the first row - a for-each isolates
each row on purpose, so no step inside one can know whether it is the first, and a second write step
aimed at the same dataset must add to what the loop collected rather than wipe it.

The claim lives on `ReplayRunState` in a set that is **shared by reference with every forked row
state** - deliberately the one thing a fork does not isolate, because "has this run started this
dataset yet?" is a question about the run and no other scope can answer it. It is settled **inside
the dataset's own write lock**, not by the caller: decided outside, two rows finishing at once on
different lanes would both be told they were first, or the one that was would replace a file the
other had already appended to. `DatasetIO.Write` takes an optional `claimFirstWrite` for exactly
that reason.

The checkpoint carries the claimed names across a park (`ParkCheckpoint.FreshenedDatasets`, optional
so older checkpoints still load). "First write of the run" has to mean the whole run including the
half that happens nine hours later - a resumed run that forgot would clear a dataset it had spent
the first half filling and still report success.

Both shop examples and the new order example now set it, so running any of them twice gives the
same answer as running it once; `verify-demos.mjs` runs the order example a second time and checks
the row count did not move. The editor shows "start fresh each run" beside "append" and withdraws
it when append is unticked, because without append it would be offering the same thing twice.

### Phase 12 - Demos is generated territory (2026-09-04)

The three-way prompt is gone. **Regenerating restores every example to the version this build
ships** - contents, name and description alike - and the answer to "I want to keep my version" is
no longer a checkbox: move or duplicate that task into a collection of your own.

The reason is what the batch is FOR. A collection where any given example might be somebody's
half-finished experiment cannot also be the place a new user looks for a working reference, and a
per-example negotiation guarantees exactly that state. It also compounds: every regenerate has to
re-ask about every edit, forever.

- `DemoResolution` (keep / revert / clone) is deleted, along with `demos regenerate --revert
  <key> --clone <key> --revert-all`. `Regenerate()` takes no arguments.
- **Startup did not change and must not.** `SeedMissing` runs on every launch without anyone asking
  for it, so it still adds what is absent, refreshes what nobody has touched, and leaves every edit
  exactly as it is. Silent actions may not lose work; an explicit one, asked for in as many words,
  may. That split is the whole reason regenerating can be as blunt as it is.
- **Taking a copy takes it out of reach.** `MoveTask` and `DuplicateTask` both drop the demo marker,
  and a move also takes a fresh id. Without that, the generator would write the pristine example
  back onto the same fixed id and two tasks would answer to it - and a duplicate carrying the key
  would leave the generator restoring whichever it found first and silently abandoning the other.
- **The name is in the hash now.** Restoring puts the name back, so a survey that called a renamed
  example "up to date" would be promising not to touch something it is about to rename.
- The dialog **names** what it is about to replace rather than counting it - "including the 2 you
  have changed: Fill in a form, Click a button" - and says what to do instead. A warning with
  nothing at stake behind it teaches people to ignore warnings, so it does not appear at all when
  nothing has been edited.

Also fixed while here: both acceptance harnesses swept every stale scratch directory in the temp
folder on startup, so two of them running at once deleted each other's workspace mid-run. The
victim failed with a missing generated page, which reads exactly like the product being broken.
They now only sweep what is an hour old or more.

### Phase 13 - one wrench per row (2026-09-04)

A collection row carried six icon buttons, a task row eight. At a sidebar's width that is a wall
of glyphs that only appears on hover, competes with the row's own name for the space, and gives
every operation the same weight whether it renames something or deletes it. **Every row now has a
single wrench that opens a menu**, and the operations get room for actual words - "Move to another
collection…", not `⇄`.

- `rowmenu.js` is the menu: `role="menu"` with `role="menuitem"` buttons, opened focused on its
  first item, arrow keys wrapping, Home/End, Escape closing and handing focus back to the wrench,
  Tab closing, click-outside and scroll closing, and one open at a time. Positioned fixed against
  the wrench, flipped above when there is no room below.
- The ops themselves moved into `collectionOp` / `taskOp` / `stepOp`, one definition each, so a
  menu pick and any other route to the same operation cannot diverge - there is no version of
  "delete task" that skips its confirmation because it was reached another way.
- **Shift+F10 and the Context Menu key now open the same menu.** They used to assemble a separate
  list picker by reading labels back off the row's buttons; there is one menu now, so there is
  nothing left to drift from.
- The insert-gap's WCAG 2.2 SC 2.5.8 "Equivalent" argument still holds and its comment says so
  properly: the equivalent control is the step menu's "Insert a step after this one", reached from
  a 24x24 wrench, with 24px menu rows.
- The wrench parks its own hover tooltip while its menu is up. Otherwise "Actions for this
  collection" sits underneath a menu of actions for that collection - the same words twice, one of
  them behind the other.

`clickRowOp` in the harness became two clicks, which kept all twenty-odd existing checks working
unchanged, and two new groups cover the menu's keyboard model and the one-at-a-time rule.

### Phase 14 - zoom is a step (2026-09-04)

Some layouts do not fit the window they are being driven in, and the control you need is off the
side of it. **`StepAction.SetZoom`** puts the browser's zoom in the hands of the task: zoom out to
60%, do the thing, zoom back to 100%. The level is a whole percentage in `Step.ZoomPercent`,
offered from the levels a browser's own menu offers rather than typed - a free number box invites
6 for 60 and a page nobody can automate.

**Two obvious implementations were tried and both were wrong**, which is why this took three
attempts and why the code says so:

1. **CSS `zoom` on the root element** - one script evaluation, no interface change, works on every
   surface. It does not work: this Chromium still returns UNZOOMED values from
   `getBoundingClientRect` under it, so the resolver would measure an element in one space and the
   click would be dispatched in another. Found by the demo asserting a measured width and getting
   the unzoomed one back.
2. **CDP `Emulation.setDeviceMetricsOverride`** - what the browser's own device toolbar does, and
   coordinate-consistent by construction. Also does not work here: Chromium reverts an emulation
   override when the DevTools session that set it detaches, and WebView2 detaches after every
   `CallDevToolsProtocolMethodAsync`. The override survived long enough to be read back and no
   longer - a step that verifies itself and is wrong by the next one. Caught by measuring the
   viewport from inside the step (5120px) and again from the next step (1280px).

What works is the browser's own zoom, `CoreWebView2Controller.ZoomFactor`, so
`IBrowserSurface.SetZoomAsync` is on the interface and the surface takes an `Action<double>` from
whoever owns the controller - the app hops to its UI thread, a lane does not. It returns the factor
the page MEASURED afterwards (the ratio of the two viewport widths), and a zoom that did not take
fails the step: passing it would leave the click after it aiming at coordinates from a layout the
page is not in, and the failure would surface as an unrelated step several steps later.

The zoom belongs to the run, not the page: `ReplayRunState.ZoomPercent` is re-applied after a
navigation, so a task that zoomed out to reach a wide layout is not quietly returned to 100% by a
link. Each lane of a parallel loop starts at 100% of its own, since a lane is a browser nobody has
zoomed yet.

**A third thing this turned up, and it applies to every future demo page: a browser lane renders
into an off-screen window, so its page is HIDDEN.** Animation frames stop and repeating timers
throttle almost to nothing, which means a page that reports something about itself on a timer or a
`resize` sits on its load-time text forever *while appearing to work* - the first assertion passes
because the value happens to be the initial one. The zoom example measures itself in a click
handler the automation triggers, which always runs. The generated page says why, in the page.

`ACTIONS`'s original fourteen are untouched; the collapsed group they hide behind is now
`ADVANCED_ACTIONS` and its label is "Advanced", because `setZoom` is not flow control and a list
whose name fits only some of its members is a list people stop adding to correctly.

### Phase 15 - the total is a step now (2026-09-04)

Summing a harvested column used to happen in `verify-shop.mjs` - outside the product, in the thing
that was supposed to be checking it. **`StepAction.Aggregate`** brings it in: five reductions over
one dataset column, published as the step's `value` output for a later step to bind to.

Five, and no more. This is the one place arithmetic enters the step model, and it enters as a
closed list a picker renders - the total, how many, the smallest, the largest, the average. The
moment a sixth is a formula rather than a name, a task stops being a record of what it does.

What it refuses is as much of the design as what it does:

- **A cell that is not a number fails the step**, naming the cell. Skipping it would produce a
  plausible average nobody could tell was short - the most expensive kind of wrong this engine can
  be. A BLANK cell is skipped, because blank is absence rather than zero.
- **A column that is not in the dataset fails**, and says which columns are. Answering 0 would be
  answering a question nobody asked while looking exactly like a working step.
- **Averaging nothing fails; counting nothing is 0.** One has no answer, the other has an obvious
  one.
- Money is expected: `"$12.50"` is what text off a page looks like, and an aggregate that only
  worked on bare numbers would only work on datasets nobody has.

The new **"Add up what you collected"** example harvests three invoice amounts and reduces them
five ways into one row of `invoice-totals.csv` - on disk rather than only in a log, because a
number in a log is a number nobody can check. `verify-demos.mjs` recomputes all five from the
amounts printed on the generated page and compares, so the example is evidence rather than a
demonstration. `AggregateOp` joined the enums `DemoCoverageTests` enforces.

**A real focus bug fell out of this.** The bigger demo batch made `PushStateAsync` slower, which
made a long-standing defect visible: `renderTree` replaces every row, so a row that HAS focus loses
it, and `ensureFocusKey` only put focus back when the render was caused by a keyboard action. Any
background echo of the store - a save made anywhere, for any reason - therefore threw a keyboard
user from halfway down the tree to the top of the document. It now notices that focus was inside
the tree before the render and puts it back, while still leaving focus alone when it was somewhere
else. There is a check for it: focus a row, provoke a push, and assert the focus is still there.

### Phase 16 - a task can take a value (2026-09-04)

"The same search for a different term" is the oldest item on the list, and it was written down as
templated parameters - `{{query}}` in a step value. **It is not implemented that way, deliberately.**
A hand-typed placeholder is an expression language arriving one string at a time: nothing can
enumerate it, nothing can check it, and a typo in it fails at run time as a value that quietly
stayed literal. That is the exact opposite of the rule the whole editor is built on - a user
SELECTS a source, never types a reference.

So a task **declares** its inputs (`TaskDefinition.Inputs`), and they show up everywhere a binding
can be made. Three things can supply one:

- **a default on the declaration** - and a declaration with no default is REQUIRED: a run that does
  not supply it fails at the step that needed it, naming it. Resolving to an empty string would
  type nothing into a search box and report success, which is the failure declaring inputs exists
  to prevent;
- **a `runTask` step** (`Step.RunTaskInputs`), resolved in the CALLER's scope so one task can hand
  another something it read off a page, and pushed onto a stack so a callee's inputs are its own -
  letting them leak back would make the same binding mean different things after a call;
- **`run --task X --input name=value`**, repeatable, and a malformed one is refused rather than
  ignored. Silently dropping the value a run was supposed to be parameterised by produces a run
  that looks right and did the wrong thing.

`BindingRef.Prefix` finally earns its keep: the search example asserts on `"searched: "` + the
input, which is the whole of composition here and enough for what composition is for.

New example **"Search for a word you choose"** (declares `term`, defaults to "wolf"), and the chain
example now calls it with `"badger"` and checks it searched for that rather than its default - so
the passing of a value is demonstrated, not just the declaring of one. `verify-demos.mjs` also runs
it from the command line with a third term, and checks that a malformed `--input` is refused.

The Inputs dialog hangs off the task's wrench and commits as it is edited, like scoped settings.

### Phase 17 - the store stops re-reading itself (2026-09-04)

Finding a collection by id read every collection manifest; finding a task by id then read every
task file in that folder. A save did both, and the step editor commits a save on every field
change. Fine at ten tasks; not at a few hundred - and the demo batch growing to eleven was enough
to make pushes slow enough to expose the focus bug fixed in phase 15.

Both answers are remembered now. **What they are not is trusted.** This store is a folder people
are invited to rearrange in Explorer, and a cache that believed itself would hand back a path to a
file somebody has since renamed, moved or replaced - and the next save would write a second copy
beside it. So every hit is confirmed by reading the id back out of the file it points at: one small
read instead of a whole directory, and a confirmation that fails simply falls through to the scan
that populated it. Three tests pin exactly that: a task file renamed by hand, a collection folder
renamed by hand, and a deleted id that must stop resolving rather than keep answering from memory.

What is NOT fixed is the other half - `PushStateAsync` still sends the whole store to the panel
after every mutation. That needs a push protocol that can say "this one task changed" and a panel
that can apply it without losing selection, expansion or focus, which is a different kind of change
and is recorded under **Not done yet**.

### Phase 18 - reaching behind a boundary (2026-09-04)

`document.querySelector` stops at a shadow boundary and at a frame edge, so a component library
that renders its button inside a shadow root produced "element not found by any strategy" - which
reads as a broken recording rather than as a limit of the tool. **Every strategy in `resolver.js`
now runs across every reachable root**: the document, every OPEN shadow root inside it, and every
same-origin iframe's document, recursively.

The parts that are easy to get wrong, and how:

- **Coordinates.** A rect measured inside an iframe is relative to THAT frame's viewport, while a
  click is dispatched against the top document's. `__automataViewportRect` walks out through every
  enclosing frame and adds each one's position; the check-state probe uses it too, since that is the
  other place a rect turns into a click. A shadow root needs no adjustment - a shadow tree shares
  its host's coordinate space.
- **Id lookups.** An id is unique within its own tree, not across the page, so `getElementById`,
  `label[for]` and `aria-labelledby` all resolve in the element's OWN root. Two instances of the
  same component each having a `#submit` is normal, and is now correctly reported as ambiguous
  rather than resolved to whichever came first.
- **XPath** runs against documents only - it has no way to express a shadow boundary - so shadow
  roots are skipped by that one strategy and frames are not.
- **Cost.** Collecting roots means a `querySelectorAll('*')` per root, so it happens ONCE per
  resolve and is shared by all eight strategies. A resolve polls every half second until its element
  appears; eight walks per poll instead of one is the whole difference on a large page.

The recorder reads `composedPath()[0]` rather than `event.target`, because an event crossing an open
shadow boundary is RETARGETED to the component's host on the way out - recording that would produce
a step that clicks the wrapper and never the control.

New example, **"Reach into a shadow root and a frame"**, clicks a button in each and then reads the
answer back out of the same tree it was written into. Asserting on something the outer page put up
would only have proved the click landed; asserting inside proves the resolver got back in there.
Its frame is a `srcdoc` one, and that detail is worth keeping: a page loaded from `file://` has an
OPAQUE origin, so one local file embedding another is cross-origin even in the same folder. A
`srcdoc` frame inherits its embedder's origin, which is what a real same-origin embed looks like -
and the file:// case being unreachable is itself the demonstration of the limit above.

### Phase 19 - branching over a list with gaps in it (2026-09-04)

One line of pseudo-code was the whole spec:

    FORLOOP(stuff.json) | IF(item).HAS('Name') | Enter Item[x].Name into 'txtName' | ELSE()

Three of its four parts did not exist. What follows is what each turned into, and the answer to
`Item[x]` first, because it is the one that stays different: **there is no index and no whole-row
value.** A loop hands each step ONE row, so the index is implicit; what a step binds to is a
COLUMN, published bare (`Name`) and qualified (`row.Name`). `[x]` implies random access into the
collection, which the model does not have.

- **`ConditionOp.Exists` / `NotExists`** — the `HAS`. Not expressible before, and the nearest thing
  was worse than missing: a JSON array is RAGGED (some objects carry a key, some do not), and
  asking `is not empty` about an absent column **failed the run**. Proven before it was fixed, by a
  throwaway run: `left side: no value for 'Name' here`. `BindingResolver` now has a three-answer
  `Lookup` — the value, legitimately absent, or the binding is broken — and only a presence test
  gets to treat absence as an answer. Everything else still refuses, because a column that is not
  there is nearly always a mis-typed column name, and reading it as empty would type nothing into a
  field and call the step a success. The message even changed to say which mistake it is: with no
  enclosing loop, the binding is in the wrong place; inside one, "this row has no 'Name' — check the
  column name, or guard the step with 'exists'".
- **`StepAction.Else`** — the `ELSE`. A SIBLING of the `if`, not a second child list on it: it is
  how a person sketches it (three things in a row, which is exactly how the line above reads), and
  it is what the tree, its drag-and-drop and its insert gaps already know how to render. The verdict
  is keyed **by the if's own step id**, because an `if` runs its children before anything looks at
  what it decided — reading "the last verdict" would pair an outer `else` with a nested `if`,
  silently, and only in tasks that happen to nest.
- **Dataset columns in the binding picker.** The shop examples bind to `row.url`, but that was only
  ever expressible in code: the picker offered captured outputs, task inputs and environment
  variables, and no way to name a column of the row a loop is on. It now walks outward from the step
  through every enclosing `forEach` and offers that dataset's columns by name — and the editor asks
  the host for them as it opens, since a picker is built synchronously from a click and a fetch
  started then would arrive after the list it was meant to fill.

**Gherkin**, which was the part most likely to be quietly wrong:

- `otherwise` is the block end Gherkin does not have. Rendered as **`But otherwise`** — `But` is
  Gherkin's own word for the contrasting case — and compiled back by splitting the guard's
  remainder in two. It is claimed by the INNERMOST guard still open, because the search for it stops
  at the next guard, which takes everything after itself anyway. That rule is what makes both
  shapes round-trip, and both are tested.
- **A guard's operands are written bare now** (`row.Name`), not as the quoted placeholder
  `"<Name>"`. The placeholder form belongs to a step VALUE, where Gherkin's own Scenario Outline
  substitution gives it meaning; the guard grammar does not accept it, so a column guard rendered
  to a line the compiler could not read back. A feature that printed and would not recompile.
- **A step with no Gherkin form no longer takes its subtree with it.** `continue` skipped the
  children, so a loop rendered as one comment line and the eight steps inside it vanished — with a
  reason about the loop and nothing about them. They are written flat now, and the lossiness says
  so.

The example is **"Work through a list with gaps in it"**: `roster.json` — two rows with a name, one
without — iterated, with `Role is present` outside and `Name is not present` / `otherwise` inside.
Nested on purpose, because that is the shape the round-trip rule has to get right. The list is
seeded as an example ASSET rather than harvested, since a harvest fills every column of every row
and so could never produce the gap; it is written when absent and replaced only by an explicit
regenerate, because a dataset sits among the user's own files and a generated page does not.

Not fixed, and worth knowing: this example loops AND checks a tally once afterwards, and a Scenario
Outline has no room for the second part — every step in an outline runs per row. So it renders as a
plain Scenario with the loop as a comment, honestly flagged lossy. The clean-outline round-trip is
proven separately on the same guard shape.

### Phase 20 - nestable, and obvious without knowing what an "if" is (2026-09-04)

Phase 19 made branching WORK. This made it legible. Planning it turned up two ways it was quietly
wrong, and those were fixed first.

**A select that could not show its own value overwrote it.** `exists` and `notExists` reached the
C# enum, the engine and the Gherkin vocabulary in phase 19 but not the editor's `OPS`. A `<select>`
with no option for its own value does not fail — the browser reports the first one — and every
field in that editor commits on `change`, so opening a guard that used `exists` and touching
anything at all rewrote it to `is exactly` and gave it a right-hand operand. The roster example's
own guards were subject to it. Fixed as a class: `optionsFor` always carries the value being
displayed, which is the rule `datasetOptions` already followed. That also closed the same hole in
the wait modes (`untilSignal` is deliberately not offered), the zoom levels (the engine accepts any
25-500, the list offers seventeen) and a `runTask` pointing at a task no longer in the workspace.

**A branch could change hands in silence.** The pairing between an `if` and its `otherwise` was pure
adjacency, so deleting a guard could hand its branch to whichever one ended up in front — the task
still ran, still passed, and took the wrong half. `Step.PairedIfId` records which guard the branch
was written for; the compiler sets it at the split, the panel sets it on creation, the engine
refuses a mismatch, and a step from before the field falls back to adjacency so nothing on disk
broke. The quietest case of all was the action dropdown: an `else` that stops being an `else` KEEPS
its children, and the engine runs an ordinary step's children unconditionally, so a conditional
branch silently starts running every pass. That now asks first.

Then the three things the user actually asked for:

- **Rows say what the step is.** A label was written once at creation and never re-derived, so a row
  could read `Click 'Alpha'` while its action had become `if`. `phrases.js` derives the row's text
  from the record on every render — nothing to keep in sync, because there is no second copy. It is
  deliberately NOT `GherkinWriter.Phrase`: that names an element selector-first because its output
  must recompile, this one is read by a person and names it `Search` rather than
  `textarea[name="q"]`. The stored label survives as a snapshot the panel refreshes, because the
  HOST writes run-log lines from `Step.Label` and cannot run this derivation.
- **A branch looks like a branch.** One hairline guide per ancestor level, absolutely positioned
  inside each row AND each insert gap (a guide on the steps alone breaks the line at every gap),
  chaining into a continuous rule because rows sit flush. A branch's guide is coloured *and dashed*,
  since colour alone is the one distinction a colour-blind reader could not make. Branches and loops
  close with a quiet end marker — not a tree item: no role, no data-key, unfocusable, the same shape
  as an insert zone, so the ARIA tree and roving tabindex are untouched. An orphaned `otherwise`
  shows the engine's verdict on the row, before anyone presses Run.
- **Nesting is an affordance.** Each row's menu is built from what the step IS: a loop offers "Add a
  step inside the loop", an `if` offers "Add an Otherwise", every step offers to nest inside the one
  above or move out a level. All of them go through the action picker, which is now the ONLY way a
  step gets created — no path left that makes a `click` step and hopes.

Structural constraints that shaped all of it, each verified rather than assumed: the picker's top
level stays at fifteen entries; the tree stays a flat list of rows (28 harness selectors depend on
it, four using the sibling combinator), so nesting is drawn rather than structured; and every tree
helper assumes exactly one `children` array per step, which is why `else` stayed a sibling rather
than becoming `ElseChildren`.

A guard against the first bug recurring: a check reads `ConditionOp` and `StepAction` out of the C#
source and asserts the panel can express every value, with a documented exemption list. Nothing had
been checking that — `DemoCoverageTests` proves an enum value has a DEMO, not that the editor can
EDIT it.

Also, scrollbars: everything that scrolls is thin, and nothing scrolls sideways at all — every
`text-overflow: ellipsis` is gone and long text wraps. Row buttons moved out of the flow to absolute
position, so revealing them cannot re-wrap a row; the "nothing moves when the buttons appear" check
now holds for width as well as height, structurally rather than by luck.

### Phase 21 - what a loop knows that its columns do not (2026-09-04)

A for-each published a row's columns and nothing else. Two things it knew perfectly well were
unreachable: where the row sat in the list (the number appeared in a log line and nowhere a binding
could see it) and the row itself, which `BindingKind.DatasetRow` promised in its own doc comment
while the resolver answered "not supported yet".

Both are bindings now, and neither needed a new mechanism:

- **`row.#` is the position, counting from 1** — the same number the run log already prints, because
  two numbering schemes for one thing is worse than either. It is published exactly the way a column
  is, bare and qualified, so a binding never has to know whether it is naming data or bookkeeping;
  publishing it qualified-only was the first attempt, and `verify-demos` caught it within a minute
  of the demo running. It is written BEFORE the columns, so a dataset that really has a column
  called `#` wins: that one is data and this is not. The position belongs to the SOURCE list, not to
  a count of what the loop did — the roster's gap is its second row, so the two people it adds come
  from positions 1 and 3, and the acceptance check asserts exactly that. A running count of writes
  would say 1 and 2 and look perfectly fine.
- **The whole row is one line of JSON**, keyed by dataset name so a nested loop can say which loop it
  means — the same disambiguation `row.sku` gets from its row variable — and falling back to the
  innermost when a binding names none. Absence gets the two-message treatment `DatasetColumn`
  already had: outside a loop the binding is in the wrong place, inside one over a different dataset
  it is naming the wrong file, and those are not the same mistake.

Both reach the Gherkin surface without new grammar. `row.#` rides the existing column syntax once
`Ref` admits a `#` (safe: Gherkin's comments are whole lines, so a `#` inside step text is text),
and a bare `row` inside a loop is the whole row — deliberately not `<row>`, which Gherkin would read
as an Examples substitution and hand back as a column called "row". A step output of the same name
does not lose silently; it gets a diagnostic saying the row won.

**And a round-trip bug the new test walked straight into.** A write step's column bound to a dataset
column rendered as `sku="<sku>"`, and the compiler read a quoted value in an assignment as a
LITERAL — so the binding survived being written and stopped being a binding when read back. Nothing
had exercised it, because the one round-trip test that wrote a dataset bound its column to a step
output, which renders bare. Fixed at both ends: assignments now render in the same bare form guards
use (one renderer for the two slots whose grammar takes bare references), and a quoted placeholder
in an assignment is read as the column it obviously means, so feature files already on disk keep
their meaning.

The guard against the class: `DemoCoverageTests` now covers `BindingKind` as well, and `verify-ui`'s
enum check now proves the picker can PRODUCE every kind, not just that the engine accepts it. The
`BindingKind` coverage test says out loud what it cannot prove — `DatasetRow` is sited two ways, and
every loop satisfies the count with the source form — because an exemption or a check that quietly
means less than it appears to is worse than none.

### Phase 22 - a field inside a nested object is just a column (2026-09-04)

A JSON dataset kept a nested object as raw text in its column, so `Address.City` could be seen and
not reached. It is a column now, published alongside the parent rather than instead of it: nothing
that already bound to `Address` changes meaning, and flattening only ever adds names.

Three decisions, each of which had a cheaper wrong answer:

- **Objects only; an array keeps its JSON.** `Items.0` would be a column that exists on some rows
  and not others — ragged by construction, for a shape this product already answers with a loop.
- **A real property always wins.** Every top-level name is written first and a leaf never
  overwrites one. Same rule phase 21 gave a column called `#` against the row's position, and it is
  now a stated principle rather than a coincidence: what the file says is data, what the reader
  works out is convenience.
- **The faithful read stayed.** Flattening at read time was the obvious answer and the obvious
  risk, and the risk turned out not to be the collision — it was `WriteJsonArray`'s append, which
  reads every existing row and writes it back. Flattening THAT read would bake the convenience
  columns into the file, and the next append would do it again. So `ReadJsonArray` still reports
  the file exactly as it stands and a separate read publishes the leaves; the append uses the first
  and everything a task binds against uses the second. There is a test that opens the file
  afterwards and looks.

No new syntax anywhere: a nested field's name simply has a dot in it, and every reference grammar
already allowed one — `row.Contact.Email` loses its `row.` prefix like any column, and the picker
offers it because `Columns` reads through the same flattening. The roster example carries a
`Contact` object now and binds one, so the capability has somewhere to be seen working.

### Still to do in v3

Nothing. All eight planned phases plus 8b-8e and phase 9 are done; what remains is in **Not done
yet** below.

## Not done yet

- **Interactive end-to-end verification** — build+launch smoke test passed (no crash), but the
  full loop (record a Google search → refine → Validate → Dry Run → Run → export/import →
  re-run) needs a human at the keyboard. The original Phase-1 caveat about the postMessage
  bridge being only smoke-tested still stands.
- **Acceptance scenarios as saved profiles**: Google search → titles, Bing search → titles,
  webmail inbox → first 20 subject lines.
- **Fingerprint heuristic tuning** against real sites (auto-generated id/class reject patterns).
- **Orchestration (old Phase 4)**: several instances running the SAME task at once with different
  parameter bindings. The parameters themselves landed in phase 16; what is left is a runner that
  fans one task out over a list of them, which is a scheduling question rather than a model one.
- **Cross-origin iframes, and closed shadow roots.** Open shadow roots and same-origin iframes
  landed in phase 18; these two did not, and the reason is the same for both: **the page cannot see
  in.** A closed root exposes nothing to script by design, and a cross-origin document throws on
  access. Reaching either means evaluating in each frame's own context over CDP - `Page.getFrameTree`
  plus `Runtime.evaluate` against a specific execution context - rather than running one script in
  the top document, and then reconciling coordinates across those contexts. That is a different
  mechanism, not a longer selector.
- **Recording inside an iframe.** The recorder listens on the top document, and an iframe never
  delivers its events there; it would have to be injected into every frame. Recording inside an open
  SHADOW root does work, since phase 18 - the recorder reads `composedPath()[0]` instead of
  `event.target`.
- **Harvesting inside a shadow root or a frame.** `harvest.js` still queries the top document only.
  The resolver's root walk is the shape the answer takes; the picker also has to be able to point at
  something in there, which is the harder half.
- **Uploading into a shadow root or a frame.** `DomFileInjector` matches its file input by a
  selector against the top document, so the one action that does not go through the resolver is the
  one action that still stops at the boundary.
- **A condition wait can only hold immediately or time out.** `WaitMode.UntilCondition` polls
  `Evaluate(spec.Condition, state)`, and nothing writes to that state while the poll loop is
  running - a parallel for-each forks a state per row, and no other step is in flight. So it is
  really an assertion with a timeout, not a wait. Making it a wait means either re-reading the page
  each poll (a condition over a live element, not over a captured output) or letting a signal or a
  sibling lane write into run state. The `slow` example uses it in the only shape that currently
  works, as a guard on a value already read.
- **A called task starts on whatever page the caller left open.** `runTask` runs the callee's steps
  and ignores its `StartUrl`, which is defensible - a subtask usually belongs in its caller's
  context - but it is undocumented anywhere except the `chain` example, which navigates explicitly
  before each call and says why in its description. Worth either honouring the callee's start URL
  behind a flag or naming the rule in the editor.
- **Pushing only the affected subtree.** The id→path index landed in phase 17, so a save no
  longer re-reads the workspace to find one file — but `PushStateAsync` still serialises the WHOLE
  store to the panel after every mutation, and that is the remaining half. It needs a push protocol
  that can say "this one task changed" and a panel that can apply it without losing selection,
  expansion or focus, which is a protocol change rather than a caching one.
