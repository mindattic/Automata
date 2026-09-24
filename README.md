# Automata

Record-once, replay-many browser automation for MindAttic. A WPF host drives a WebView2 pane
through the Chrome DevTools Protocol; you **record** a series of browser actions once, refine
them in a **WYSIWYG step editor**, and **replay** them any time — with a self-healing element
resolver that keeps finding the same controls even after a site redesigns its markup.

## Getting started

Automata is a Windows desktop app (WPF + WebView2). If you were handed a published build rather
than building from source:

1. Run `Automata.App.exe`. Windows may offer to install the **.NET Desktop Runtime** the first
   time, if it isn't already on the machine — accept that prompt once and relaunch. The
   **WebView2 Runtime** is already installed on virtually every current Windows 10/11 machine, so
   nothing else is needed.
2. The window has two panes: the **sidebar** (collections/tasks/steps tree, step editor, record
   and replay controls) and the live **browser pane** the automation acts on. The browser pane
   uses its own persistent WebView2 profile, so a site login survives app restarts without
   touching your regular browser.
3. First launch walks you through building a real example — see
   [First run — the built-in tour](#first-run--the-built-in-tour) below.
4. **Automata is driven by the LLM of your choice — bring your own API key to turn it on.**
   Settings → pick a provider (Claude, OpenAI, Gemini, or Kimi) and paste in a key. Without one,
   free-text task authoring and self-heal repair have nothing to call; recording, editing and
   replaying steps you've already built still works either way. Automata asks once, on first
   launch, if no key is configured — add one there, or dismiss it and add one later from Settings.

Building from source instead, or want to publish your own build? See
[For developers](#for-developers) at the end of this file.

## First run — the built-in tour

On first open with an empty store, Automata teaches itself: a short OK-gated walkthrough builds a
real example in front of you —

1. *"A Collection is a group of Tasks"* → OK creates the **Google Searches** collection.
2. *"A Task is a member of a Collection; a Task is a group of Steps that run in order"* → OK
   creates the **Wolf Tshirts** task.
3. The sample steps appear (navigate to Google, type *wolf tshirts*, press Enter, wait for the
   results, then click **Images**), and a final popup says to click **Run**.

Run it, watch the steps light up, then poke at everything else — the rest of the app works the
way that example looks.

## Concepts

- **Collection** — a named group of tasks. `Collection 1:M Task 1:M Step`.
- **Task** — a replayable automation ("Check Email from Dave"): an ordered tree of steps.
- **Step** — one typed action. Steps can nest **substeps** (`children`), which execute
  sequentially after the parent's own action confirms. Each step auto-confirms its
  post-condition (value read back, checked state, navigation settled, page no longer busy)
  before the next one runs.

### Step actions

| Action | Does |
|---|---|
| `navigate` | Load a URL and wait for the navigation to finish |
| `click` | Trusted CDP mouse click at the element's center |
| `typeText` | Real CDP keystrokes (for fields with `onkeydown`-style logic) |
| `setValue` | Native-property-setter + input/change events (React-safe, fast) |
| `pressEnter` | Real Enter key press (submits search boxes / Enter-to-submit forms) |
| `check` / `uncheck` | Ensure a checkbox's final state (native or `role=checkbox` widget) |
| `selectRadio` | Select a radio input or `role=radio` widget |
| `selectOption` | Pick a `<select>` option by visible text |
| `uploadFile` | Attach a local file via CDP — no native picker |
| `waitForElement` | Block until the target resolves and is visible |
| `assertElement` | Fail the run unless the target exists / contains expected text |
| `extractText` | Read the target's text into the run output/log |
| `group` | Pure container for substeps |

Two per-step flags:

- **`pauseForUser`** — replay halts before the step until you press **Continue**.
- **`isCommitPoint`** — informational ◆ marker for steps that commit a permanent write
  (submit/save/purchase). Auto-flagged at record time for submit-looking clicks.

## Recording

Press **● Record**, perform the actions in the browser pane, press **■ Stop**. The captured
events coalesce into clean steps (keystroke bursts become one `typeText`, focus-clicks vanish,
checkbox toggles collapse to the final state, dropdown-opening clicks fold into the
`selectOption` they led to) and save as a new task in the selected collection. A live preview
shows the steps as you act. Refine afterwards in the editor — recording is the primary way to
build a task; hand-building in the editor works too.

Notes:
- Password values are never recorded (`masked`) — fill them in the editor.
- File uploads record the file *name* only (browsers hide local paths from JS) — set a real
  local path on the step before replaying.

## Editing

- **Tree**: hover a collection or task row for its buttons — **+task/+step**, **✎ rename**
  (opens a modal), **⧉ duplicate**, **🗑 delete**. Double-click a name for quick inline rename.
- **Insert between steps**: hover the gap between two step rows — a "＋ add step here" sliver
  appears; clicking it opens a picker listing every action, and the new step lands exactly
  there, selected in the editor.
- **Step editor**: click any step — typed action dropdown, label, value/URL, editable target
  fingerprint fields, `pause for user` / `commit point` flags, timeout, add-substep/delete.
- **Drag & drop**: drag steps to reorder (drop on a row's middle to nest as a substep); drag a
  task onto another collection to move it.
- **Deletes always confirm**: every delete (collection, task, step) opens a purpose-built
  confirm modal — Escape or clicking away cancels; destruction takes an explicit click.

## Passing values between tasks

A task's wrench menu has **Inputs and outputs…**. *Takes* declares what the task needs from
whoever runs it — a name and a default, blank meaning required. *Publishes* declares what it hands
on: a name, and a pick of any step in the task that captures a value.

Wire them together in the same dialog: each input has a **comes from** dropdown listing every
output published by another task in the same collection. Run that collection and its tasks walk in
order on one browser, each one's published values reaching the tasks after it. Both ends are
picked, never typed.

A wiring is a hint, not a requirement. Run a wired task on its own and it uses its declared default
and says so, and a value supplied directly — `--input name=value`, or a `runTask` step's binding —
always wins over a wiring. The **Demos** collection ships three examples (`Pipeline 1–3`) that only
mean anything in order.

## Replaying

Select a task and click **▶ Run**. Step rows light up live (running / passed / failed / healed /
paused); `pauseForUser` steps hold until **Continue**. Every run also writes a log file to
`Documents\Automata\Logs\<timestamp>-<task>.log`.

### Self-healing element resolution

Each targeted step stores a multi-strategy **fingerprint** (id, CSS selector, name, classes,
XPath, ARIA role/label, nearby label text, visible text). Replay resolves it via the path of
least resistance — the first strategy with exactly **one visible** match wins:

```
#id → css selector → tag[name] → tag.classes → xpath → aria label → label text → visible text
```

If no strategy is unique, candidates are scored (text/aria/name/class overlap…); a clear leader
wins, a near-tie fails as *ambiguous* rather than guessing. When a step only resolved via a
fallback strategy, the resolver re-fingerprints the found element and the refreshed identity is
saved back into the task (**self-heal**) — the tree shows `✓♻`.

As an opt-in last resort (checkbox under *AI task (advanced)*), an unresolvable step's intent can
be handed to the LLM tool-calling loop to complete just that one step (Run mode only).

## Storage — one database, files for sharing

Everything — collections, tasks, datasets, run history, the schedule, parked runs and settings —
lives in one SQLite database:

```
%LocalAppData%\MindAttic\Automata\automata.db
```

The only store is the database; files are how work travels. **Open data folder** in Settings shows
`automata.db` in File Explorer (copy it while the app is closed for a raw backup). Run logs stay
plain text, one file per run, in `Documents\Automata\Logs\` — the 📁 button on the Runs tab opens
that folder.

- **Export and import** (Settings → *Move a collection or task…*) takes the selected collection or
  task out as a `*.automata.zip` (the long-standing share format), a single readable
  `*.automata.json`, or a Chrome DevTools Recorder flow. **⇪ Import** takes any of those back — and
  also an old per-task `.json` file from the file-based versions.
- **The whole workspace** (Settings → *The whole workspace…*) exports everything to one
  `*.automata.json` — collections with their tasks, datasets, the schedule, settings, run history
  and parked runs — and imports one back. Into an empty database that is a restore: every id, name
  and timestamp arrives as it left. Into a populated one it merges.
- **Imports never overwrite.** Colliding ids are regenerated (and every reference follows — task
  order, `runTask` steps and input wiring, schedule targets and chains, run history), colliding
  names get ` (2)` suffixes, a task imported without its collection lands in an auto-created
  **Imported** collection, and a dataset, run or settings that already exist here are kept, with a
  warning in the log.
- **Datasets** keep their file-style names (`skus.csv`, `roster.json`) — the extension still picks
  the shape. **⇪ Import** on the Data tab brings CSV/JSON files in (one with the same name is
  replaced, which is how a dataset is refreshed from a new spreadsheet export); each dataset's
  **⇩** writes it back out as a file. A JSON dataset keeps nested objects as objects.
- **Deleting hides** (HOUSE-LAW-2): a deleted collection or task keeps its row, marked deleted, and
  stops appearing anywhere. A task saved without a parent lands in an auto-created **Default**
  collection.
- **Upgrading from the file-based versions.** On the first launch against an empty database,
  Automata imports the old `Documents\Automata\{Collections,Datasets,Runs,Schedule,Parked}` and
  `%APPDATA%\MindAttic\Automata\settings.json` into it, applying the old store's hand-edit rules on
  the way in (a folder or file renamed in Explorer names its collection or task; a copy-pasted file
  gets a fresh id; a folder of tasks without `collection.json` is recovered under its name). **The
  files are left exactly as they were**, as a backup. The import runs once per database — the
  outcome is recorded in it, so nothing comes back from the old files after you delete it — and
  what came in is reported in the sidebar log.
- **Scratch databases for tests and tools**: `AUTOMATA_DB_PATH` names the database file. A harness
  that sets `AUTOMATA_SETTINGS_PATH` (every one in `tools/` does) gets `automata.db` beside that
  path instead, so it can never touch the real one; the old `AUTOMATA_COLLECTIONS_ROOT`,
  `AUTOMATA_DATASETS_ROOT`, `AUTOMATA_RUNS_ROOT`, `AUTOMATA_SCHEDULE_PATH`, `AUTOMATA_PARKED_ROOT`
  and `AUTOMATA_SETTINGS_PATH` now say where the one-time import looks. The harnesses read the
  scratch database with `tools/automata-db.mjs`.
- **Schema changes** ship as EF Core migrations in `Automata.Core/Automation/Data/Migrations`,
  applied automatically at startup by the app and the runner. To add one:
  `dotnet ef migrations add <Name> --project Automata.Core --output-dir Automation/Data/Migrations`.

## Settings

The **⚙ Settings** fold-out in the sidebar holds:

- **LLM provider & keys** — **bring your own key here to turn Automata's AI side on.** Four
  providers (Claude, OpenAI, Gemini, Kimi/Moonshot); pick which one runs first, the rest stay
  fallbacks. Without a key for at least one of them, free-text task authoring and self-heal repair
  have nothing to call — Automata asks once on first launch if none is configured. A key entered
  here is Automata's own — it's stored separately from every other MindAttic app you might have, so
  it never changes what one of those resolves. A blank key falls back to whatever shared default is
  actually configured on this machine, which is checked live rather than assumed: the field says
  "Not configured" when nothing backs it, never a label that might not be true. Takes effect on the
  next run, no restart. Recording, editing and replaying steps you've already built works with none
  configured.
- **Engine defaults…** — timeouts, retries and self-heal behavior every collection, task and step
  inherits unless something further down overrides it.
- **Examples…** — review the generated Demos collection, reset it to the version this build ships,
  or take a guided tour through every example, one at a time.
- **Import / Export** — move a collection or task in or out of this workspace as a
  `*.automata.zip` or `*.automata.json`, or back up / merge the **whole workspace** as one
  `*.automata.json` (see [Storage](#storage--one-database-files-for-sharing)).
- **Open data folder** — shows the database file, `automata.db`, in File Explorer.
- **Layout** — **Detach the sidebar** moves the build panel into its own window: put it on another
  monitor, or take a third of the screen for building and give the browser the rest. Closing that
  window docks it again, and where it was is remembered across launches. It is the same panel
  either way — reparented, never reloaded — so nothing it was holding is lost by moving it.
- **Theme** — **Dark** (default) or **Light**, applied the moment it is chosen and remembered
  across launches. Both palettes are checked against WCAG 2.2 AA by `tools/verify-ui.mjs`, which
  runs the same axe-core pass over each.
- **Border radius** — 0–10px slider (default 5) rounding every button and input, applied live.

## Free-text AI mode (advanced)

The original plain-English path is folded under *AI task (advanced)*: type an instruction and an
LLM drives the pane through generic DOM tools (click, set field, type, select, check, upload,
page status). Recording + the WYSIWYG editor are the primary workflow — free text is for
one-offs and exploration. Whichever provider is selected in Settings runs first, with the other
three as fallbacks — first one with a usable key wins. A key added in Settings always takes
priority; some providers can otherwise fall back to a shared default that is only ever configured
on the maintainer's own machine, and is never assumed to be there (see [Settings](#settings)).

## For developers

### Build & test

```
dotnet build Automata.slnx
dotnet test Automata.Tests
```

### Run from source

```
launch.bat
```

`launch.bat` (repo root) stops any running instance, clean-rebuilds, publishes to
`C:\Apps\Automata\` — a convention specific to the maintainer's own machine — and opens that
deployed copy, so a double-click always runs current source, never a stale build.
`dotnet run --project Automata.App` works too for a quick dev run.

### Publish a distributable build

```
dotnet publish Automata.App -c Release -r win-x64 --self-contained false
```

Framework-dependent: the output (`Automata.App\bin\Release\net10.0-windows\win-x64\publish\`) is a
few MB and needs the .NET 10 Desktop Runtime on the machine it runs on — Windows offers to install
that automatically the first time `Automata.App.exe` runs, if it's missing. Hand that folder to
someone and it runs as-is; there is no separate installer.

### Architecture

```
Automata.App    WPF host: two WebView2 panes, postMessage bridge, AutomationController
Automata.Runner headless CLI runner over offscreen WebView2 lanes
Automata.Core   engine: model, SQLite store (EF Core 10), zip/JSON export-import, one-time legacy
                import, replay engine, recorder coalescer, LLM tool loop — WebView2-free
AutoWebNav      (NuGet, github.com/mindattic/AutoWebNav) the shared browser layer: IBrowserSurface,
                fingerprint/resolver + page toolkit JS, BrowserActions, and AutoWebNav.WebView2's
                WebView2BrowserSurface / DomFileInjector / OffscreenWebView2Factory. Shared with
                KdpPublish and JobHunt; change browser mechanics there, not here.
Automata.Tests  NUnit 4 over model, database (real migrations on scratch SQLite files), stores,
                export/import, legacy import, replay, workflow, recorder, settings, logs, demos
```

Beyond the unit tests, four acceptance harnesses drive the real app and the real runner:
`tools/verify-ui.mjs` (the sidebar over CDP, including a WCAG 2.2 AA baseline),
`tools/verify-js.mjs` (the injected scripts themselves — the naming rule as a pure function, and
fingerprint/resolver/harvest against a real DOM in the real WebView2),
`tools/verify-demos.mjs` (every generated example, run in a browser), and `tools/verify-shop.mjs`
(the harvest-and-loop total, checked three ways against the pages themselves).

`tools/collect-names.mjs` sits beside them and is not a check at all — it never fails, it reports.
It visits a spread of real sites and sorts every id and class they use into what `stability.js` would
keep and what it would throw away, which is how the filter gets tuned against what shipped rather
than against what somebody imagined. What it turns up goes into the corpus in `verify-js.mjs`, which
is where a pattern is proven.

A fourth is deliberately outside that set. `tools/verify-live.mjs --live` runs the **acceptance
profiles** — a Google search, a Bing search and a webmail inbox — against the real sites, after
`automata-runner profiles seed` installs them. It is never part of the green bar, because a search
engine redesigning itself is not a regression in this repo and a check that cannot tell those apart
is worth less than none. The mail profile reads its account from `AUTOMATA_MAIL_URL`,
`AUTOMATA_MAIL_USER` and `AUTOMATA_MAIL_PASS`, and skips itself by name when they are not set.

Every boundary a selector stops at is now reached into: open shadow roots and same-origin iframes
by walking them, CLOSED shadow roots by being installed before the page runs and keeping a list of
the roots it opens, and CROSS-ORIGIN iframes by talking to the copy of the resolver already running
inside them over `postMessage`. The last one carries coordinates back out through each enclosing
frame, because a cross-origin document cannot know where it sits on the page and its parent can.

Recording, harvesting and uploading reach the same places. The recorder runs in every frame and
sends its events out through the bridge, so a click inside a cross-origin iframe is recorded, in
order, alongside the clicks around it; a harvest generalises and reads rows through the same root
walk, asking a cross-origin frame's own copy by name when it has to.

Known limitations: a closed shadow root that existed BEFORE the toolkit was installed is not
reachable, and never becomes reachable — there is one instant when a closed root is visible to
anything, and it is the instant it is created. A frame that runs no script at all (`sandbox` with no
`allow-scripts`) cannot answer. Forwarding an ACTION into a cross-origin frame needs `new Function`
there, so a frame whose CSP forbids `unsafe-eval` can be searched, read and harvested but not acted
in, and says so; nothing about the ordinary same-document path goes near `eval`. Two things are
genuinely out rather than merely unbuilt: **recording** inside a closed shadow root, because the
event is retargeted with an empty `composedPath` and there is nothing to read; and attaching a
**file** inside a cross-origin frame, because that needs a handle on the element and a handle does
not cross an origin boundary.
