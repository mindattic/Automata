# Automata

Record-once, replay-many browser automation for Windows. Record what you do in a live browser pane, refine it in a visual step editor, and replay it with a resolver that keeps working after a site redesign.

![C#](https://img.shields.io/badge/language-C%23-239120) ![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) ![WPF and WebView2](https://img.shields.io/badge/UI-WPF%20%2B%20WebView2-0078D4) ![Windows](https://img.shields.io/badge/platform-Windows-0078D6) ![License MIT](https://img.shields.io/badge/license-MIT-green)

```text
+-- Automata ---------------------------------+------------------------------------------------+
| ? Record  Stop  Continue  Cancel  Settings  |                                                |
| [ https://www.google.com            ] [Go]  |                                                |
| Build | Schedule | Data | Runs              |        live browser pane (WebView2)            |
|---------------------------------------------|                                                |
| COLLECTIONS                 + add collection|        the page your task acts on, with its    |
|  v Google Searches                          |        own persistent profile, so a site       |
|     v Wolf Tshirts               [Run]      |        login survives app restarts             |
|        1 Navigate to google.com     passed  |                                                |
|        2 Type "wolf tshirts"        passed  |                                                |
|        3 Press Enter                running |                                                |
|        4 Wait for results                   |                                                |
|        5 Click "Images"                     |                                                |
| > Describe it instead (advanced)            |                                                |
| LOG                                         |                                                |
+---------------------------------------------+------------------------------------------------+
```

The sidebar on the left is where you build, schedule and review. The browser pane on the right is the real page the automation drives. The tree above is the example the first-run tour builds for you.

## Why

- Automate the websites that have no API: record the clicks once, replay them whenever you want.
- Stop rewriting scripts every time a site changes its markup: steps remember each element eight ways and heal themselves when one stops matching.
- Edit what you recorded instead of re-recording it: every step is a typed, editable row in a tree.
- Run unattended: schedule collections, chain them, and let the headless runner pick them up from Windows Task Scheduler.
- Feed tasks with data: loop over CSV or JSON rows, harvest lists off a page, and pass values from one task to the next.
- Bring your own LLM key when you want it: draft steps from a plain-English description, or let a model rescue a step that cannot be found. Recording and replay never need one.

## Features

### Record

Press **Record**, perform the actions in the browser pane, press **Stop**. The captured events coalesce into clean steps and save as a new task in the selected collection, with a live preview while you act:

- keystroke bursts become one `typeText`
- focus-clicks before typing vanish
- checkbox toggles collapse to their final state
- dropdown-opening clicks fold into the `selectOption` they led to
- submit-looking clicks are auto-flagged as commit points

Recording is the primary way to build a task; hand-building in the editor works too.

- Password values are never recorded (`masked`). Fill them in the editor.
- File uploads record the file name only, because browsers hide local paths from JavaScript. Set a real local path on the step before replaying.
- The recorder runs in every frame, so a click inside a cross-origin iframe is recorded in order alongside the clicks around it.

### Edit

- **Tree**: hover a collection or task row for its buttons: add task or step, rename (opens a modal), duplicate, delete. Double-click a name for a quick inline rename.
- **Insert between steps**: hover the gap between two step rows and an "add step here" sliver appears; clicking it opens a picker listing every action, and the new step lands exactly there, selected in the editor.
- **Step editor**: click any step for its typed action dropdown, label, value or URL, editable target fingerprint fields, pause-for-user and commit-point flags, timeout, and add-substep or delete.
- **Drag and drop**: drag steps to reorder (drop on a row's middle to nest as a substep); drag a task onto another collection to move it.
- **Deletes always confirm**: every delete opens a purpose-built confirm modal. Escape or clicking away cancels; destruction takes an explicit click.

### Replay

Select a task and click **Run**. Step rows light up live (running, passed, failed, healed, paused); `pauseForUser` steps hold until **Continue**. Every run also writes a log file to `Documents\Automata\Logs\<timestamp>-<task>.log`.

Each step auto-confirms its post-condition (value read back, checked state, navigation settled, page no longer busy) before the next one runs.

### Self-healing element resolution

Each targeted step stores a multi-strategy fingerprint (id, CSS selector, name, classes, XPath, ARIA role and label, nearby label text, visible text). Replay resolves it by the path of least resistance; the first strategy with exactly one visible match wins:

```text
#id -> css selector -> tag[name] -> tag.classes -> xpath -> aria label -> label text -> visible text
```

If no strategy is unique, candidates are scored by text, ARIA, name and class overlap. A clear leader wins; a near-tie fails as ambiguous rather than guessing. When a step only resolved through a fallback strategy, the resolver re-fingerprints the element and saves the refreshed identity back into the task (self-heal), and the tree marks the step as healed.

As an opt-in last resort (the "allow LLM repair" checkbox under *Describe it instead (advanced)*), an unresolvable step's intent can be handed to the LLM tool-calling loop to complete just that one step, in Run mode only.

### Flow control, data and harvesting

Steps go beyond single clicks: `wait` (for a duration, a time of day, a condition or a signal), `forEach` over dataset rows, `if` and `else`, `runTask`, `writeDataset`, `extractAll` (harvest many rows off a page into a dataset), `aggregate` (total, count, min, max or average of a column) and `checkElement` (is it there right now, without failing). The **Data** tab holds the datasets these read and write.

### Passing values between tasks

A task's wrench menu has **Inputs and outputs**. *Takes* declares what the task needs from whoever runs it: a name and a default, blank meaning required. *Publishes* declares what it hands on: a name, and a pick of any step in the task that captures a value.

Wire them together in the same dialog: each input has a **comes from** dropdown listing every output published by another task in the same collection. Run that collection and its tasks walk in order on one browser, each one's published values reaching the tasks after it. Both ends are picked, never typed.

A wiring is a hint, not a requirement. Run a wired task on its own and it uses its declared default and says so, and a value supplied directly (`--input name=value`, or a `runTask` step's binding) always wins over a wiring. The **Demos** collection ships three examples (`Pipeline 1` to `Pipeline 3`) that only mean anything in order.

### Schedule and run headless

The **Schedule** tab runs a collection at a set time, on an interval, or once another collection has finished. The **Runs** tab lists finished and in-progress runs, including ones that started while the window was closed. Both are backed by `automata-runner`, the headless command-line runner described under [Command-line runner](#command-line-runner).

### Describe it instead

Under *Describe it instead (advanced)*, type what a task should do in plain English:

- **Draft steps** asks the selected LLM for a short Gherkin feature file in a closed, documented vocabulary, compiles it, and shows you both the feature and the resulting steps. Nothing is saved until you click Insert. A draft that does not compile is repaired against its own line-numbered diagnostics, up to three attempts.
- **Run once** lets an LLM drive the pane directly through generic DOM tools (click, set field, type, select, check, upload, page status) without saving steps. It is for one-offs and exploration.

A drafted feature looks like this:

```text
Feature: Supplier restock

  Scenario: Sign in
    Given I open "https://shop.example/login"
    And I click "Sign in"

  Scenario: Check stock
    Given I open "https://shop.example/stock"
```

### Reaching into hard places

Every boundary a selector stops at is reached into: open shadow roots and same-origin iframes by walking them, closed shadow roots by being installed before the page runs and keeping a list of the roots it opens, and cross-origin iframes by talking to the copy of the resolver already running inside them over `postMessage`. The last one carries coordinates back out through each enclosing frame, because a cross-origin document cannot know where it sits on the page and its parent can.

Recording, harvesting and uploading reach the same places. A harvest generalises and reads rows through the same root walk, asking a cross-origin frame's own copy by name when it has to.

## Quick start

### From a published build

1. Run `Automata.App.exe`. Windows may offer to install the .NET Desktop Runtime the first time, if it is not already on the machine; accept that prompt once and relaunch. The WebView2 Runtime is already installed on virtually every current Windows 10 or 11 machine, so nothing else is needed.
2. The window has two panes: the sidebar (collections, tasks and steps tree, step editor, record and replay controls) and the live browser pane the automation acts on. The browser pane uses its own persistent WebView2 profile, so a site login survives app restarts without touching your regular browser.
3. The first launch walks you through building a real example (see [First run](#first-run)).
4. Optional: to turn on the AI side, open Settings, pick a provider (Claude, OpenAI, Gemini or Kimi) and paste in a key. Without one, free-text authoring and LLM repair have nothing to call; recording, editing and replaying steps you have already built work either way. Automata asks once, on first launch, if no key is configured.

### From source

```powershell
git clone https://github.com/mindattic/Automata
cd Automata
dotnet run --project Automata.App
```

You need Windows and the .NET 10 SDK. See [Building](#building) for publishing your own build.

## First run

On first open with an empty store, Automata teaches itself. A short OK-gated walkthrough builds a real example in front of you:

1. "A Collection is a group of Tasks". OK creates the **Google Searches** collection.
2. "A Task is a member of a Collection; a Task is a group of Steps that run in order". OK creates the **Wolf Tshirts** task.
3. The sample steps appear (navigate to Google, type *wolf tshirts*, press Enter, wait for the results, then click **Images**), and a final popup says to click **Run**.

Run it, watch the steps light up, then poke at everything else. The rest of the app works the way that example looks.

## Concepts

- **Collection**: a named group of tasks. `Collection 1:M Task 1:M Step`.
- **Task**: a replayable automation ("Check Email from Dave"), an ordered tree of steps.
- **Step**: one typed action. Steps can nest substeps (`children`), which execute sequentially after the parent's own action confirms.

## Step actions

| Action | Does |
|---|---|
| `navigate` | Load a URL and wait for the navigation to finish |
| `click` | Trusted CDP mouse click at the element's center |
| `typeText` | Real CDP keystrokes, for fields with keydown-style logic |
| `setValue` | Native property setter plus input and change events (React-safe, fast) |
| `pressEnter` | Real Enter key press, to submit search boxes and Enter-to-submit forms |
| `check` and `uncheck` | Ensure a checkbox's final state (native or `role=checkbox` widget) |
| `selectRadio` | Select a radio input or `role=radio` widget |
| `selectOption` | Pick a select option by visible text |
| `uploadFile` | Attach a local file via CDP, with no native picker |
| `waitForElement` | Block until the target resolves and is visible |
| `assertElement` | Fail the run unless the target exists and contains the expected text |
| `extractText` | Read the target's text into the run output and log |
| `group` | Pure container for substeps |
| `wait` | Pause for a duration, until a time of day, until a condition holds, or for a signal |
| `forEach` | Run the substeps once per row of a dataset |
| `if` | Run the substeps only when a condition holds |
| `else` | Run the substeps only when the `if` immediately before did not |
| `runTask` | Invoke another task inline |
| `writeDataset` | Write bound values as a row of a named dataset |
| `extractAll` | Harvest many rows off the current page into a dataset |
| `setZoom` | Zoom the page so a later step can reach something a cramped layout hides |
| `aggregate` | Reduce one dataset column to a total, count, smallest, largest or average |
| `checkElement` | Report whether the target is there right now; absence is an answer, not a failure |

Two per-step flags:

- **pauseForUser**: replay halts before the step until you press **Continue**.
- **isCommitPoint**: an informational marker for steps that commit a permanent write (submit, save, purchase). Auto-flagged at record time for submit-looking clicks.

## Settings

The **Settings** fold-out in the sidebar holds:

- **LLM provider and keys**: four providers (Claude, OpenAI, Gemini, Kimi via Moonshot). Pick which one runs first; the rest stay fallbacks, and the first with a usable key wins. Each provider takes one or more keys, one per line. A key entered here is Automata's own, stored separately from every other MindAttic app, so it never changes what one of those resolves. A blank key falls back to whatever shared default is actually configured on this machine, which is checked live rather than assumed: the field says "Not configured" when nothing backs it. Changes take effect on the next run, with no restart.
- **Engine defaults**: timeouts, retries and self-heal behaviour that every collection, task and step inherits unless something further down overrides it.
- **Examples**: review the generated Demos collection, reset it to the version this build ships, or take a guided tour through every example, one at a time.
- **Import and export**: move a collection or task in or out as a `*.automata.zip` or `*.automata.json`, or back up and merge the whole workspace as one `*.automata.json` (see [Storage](#storage)).
- **Open data folder**: shows `automata.db` in File Explorer.
- **Layout**: **Detach the sidebar** moves the build panel into its own window, for another monitor or a third of the screen. Closing that window docks it again, and its position is remembered across launches. It is the same panel either way, reparented and never reloaded, so nothing it was holding is lost.
- **Theme**: Dark (default) or Light, applied immediately and remembered. Both palettes are checked against WCAG 2.2 AA by `tools/verify-ui.mjs`.
- **Border radius**: a 0 to 10 px slider (default 5) rounding every button and input, applied live.

## Storage

Everything (collections, tasks, datasets, run history, the schedule, parked runs and settings) lives in one SQLite database:

```text
%LocalAppData%\MindAttic\Automata\automata.db
```

The only store is the database; files are how work travels. **Open data folder** shows `automata.db` in File Explorer (copy it while the app is closed for a raw backup). Run logs stay plain text, one file per run, in `Documents\Automata\Logs\`; the folder button on the Runs tab opens it.

- **Export and import** (Settings, *Move a collection or task*) takes the selected collection or task out as a `*.automata.zip` (the long-standing share format), a single readable `*.automata.json`, or a Chrome DevTools Recorder flow. **Import** takes any of those back, and also an old per-task `.json` file from the file-based versions.
- **The whole workspace** (Settings, *The whole workspace*) exports everything to one `*.automata.json` (collections with their tasks, datasets, the schedule, settings, run history and parked runs) and imports one back. Into an empty database that is a restore: every id, name and timestamp arrives as it left. Into a populated one it merges.
- **Imports never overwrite.** Colliding ids are regenerated and every reference follows (task order, `runTask` steps and input wiring, schedule targets and chains, run history). Colliding names get a ` (2)` suffix, a task imported without its collection lands in an auto-created **Imported** collection, and a dataset, run or settings that already exist are kept, with a warning in the log.
- **Datasets** keep their file-style names (`skus.csv`, `roster.json`); the extension picks the shape. **Import** on the Data tab brings CSV and JSON files in (one with the same name is replaced, which is how a dataset is refreshed from a new spreadsheet export); each dataset's download button writes it back out as a file. A JSON dataset keeps nested objects as objects.
- **Deleting hides** (HOUSE-LAW-2): a deleted collection or task keeps its row, marked deleted, and stops appearing anywhere. A task saved without a parent lands in an auto-created **Default** collection.
- **Upgrading from the file-based versions.** On the first launch against an empty database, Automata imports the old `Documents\Automata\{Collections,Datasets,Runs,Schedule,Parked}` folders and `%APPDATA%\MindAttic\Automata\settings.json`, applying the old store's hand-edit rules on the way in (a folder or file renamed in Explorer names its collection or task; a copy-pasted file gets a fresh id; a folder of tasks without `collection.json` is recovered under its name). The files are left exactly as they were, as a backup. The import runs once per database; the outcome is recorded in it, so nothing comes back from the old files after you delete it, and what came in is reported in the sidebar log.
- **Scratch databases for tests and tools**: `AUTOMATA_DB_PATH` names the database file. A harness that sets `AUTOMATA_SETTINGS_PATH` (every one in `tools/` does) gets `automata.db` beside that path instead, so it can never touch the real one. The old `AUTOMATA_COLLECTIONS_ROOT`, `AUTOMATA_DATASETS_ROOT`, `AUTOMATA_RUNS_ROOT`, `AUTOMATA_SCHEDULE_PATH`, `AUTOMATA_PARKED_ROOT` and `AUTOMATA_SETTINGS_PATH` now say where the one-time import looks. The harnesses read the scratch database with `tools/automata-db.mjs`.
- **Schema changes** ship as EF Core migrations in `Automata.Core/Automation/Data/Migrations`, applied automatically at startup by the app and the runner.

To add a migration:

```powershell
dotnet ef migrations add <Name> --project Automata.Core --output-dir Automation/Data/Migrations
```

## Command-line runner

`automata-runner` (the `Automata.Runner` project) runs tasks without the desktop app, against the same database:

```text
automata-runner — runs Automata tasks without the desktop app.

  run --task <id|name>          run one task
  run --collection <id|name>    run every task in a collection, in order
  tick                          resume parked runs and run whatever is due (what Task Scheduler invokes)
  status                        parked runs, then the ten most recent

  schedule list                 what is scheduled, and when each is next due
  schedule add --collection <id|name> --cron "0 9 * * *" [--timezone <id>]
  schedule add --task <id|name> --every-minutes <n>
  schedule add --collection <id|name> --after <entry-id>
  schedule enable|disable|remove <entry-id>

  install [--interval-minutes 5]   register the tick with Windows Task Scheduler
  uninstall                        remove it

  run --task <id|name> [--input <name>=<value>]...
                                supply the task's declared inputs; anything not named
                                falls back to that input's default

  demos list                    the generated examples, and which have been edited
  demos seed                    write any example that is missing; refresh untouched ones
  demos regenerate              put EVERY example back to the shipped version, edits and
                                all; move or duplicate one out of Demos to keep it

  profiles list                 the acceptance scenarios, and whether they are installed
  profiles seed                 install any that are missing, into "Acceptance"
  --help                        this text

Exit codes: 0 success, 1 a run failed, 2 fault, 3 bad arguments.
```

- The browser needs an interactive session: WebView2 cannot render in session 0, so a scheduled task must be registered to run only when the user is logged on.
- A wait longer than its step's park-after threshold (15 minutes by default) checkpoints the run and closes its browser rather than holding one idle; the next tick after the wait ends picks it up. Parking resets the page to the task's start URL, so a task that must keep what it did before the wait should set that threshold to 0 and hold the browser instead.
- Acceptance profiles run against real sites, so they are never seeded on launch and never refreshed; adapt them and they stay adapted.

## How it works

```text
Automata.App     WPF host: two WebView2 panes, postMessage bridge, AutomationController
Automata.Runner  headless CLI runner (automata-runner) over offscreen WebView2 lanes
Automata.Core    engine: model, SQLite store (EF Core 10), zip/JSON export-import, one-time legacy
                 import, replay engine, recorder coalescer, Gherkin flow compiler, scheduling,
                 LLM tool loop. WebView2-free.
AutoWebNav       (NuGet, github.com/mindattic/AutoWebNav) the shared browser layer: IBrowserSurface,
                 fingerprint/resolver + page toolkit JS, BrowserActions, and AutoWebNav.WebView2's
                 WebView2BrowserSurface / DomFileInjector / OffscreenWebView2Factory. Shared with
                 KdpPublish and JobHunt; change browser mechanics there, not here.
Automata.Tests   NUnit 4 over model, database (real migrations on scratch SQLite files), stores,
                 export/import, legacy import, replay, workflow, recorder, settings, logs, demos
```

The sidebar is plain HTML, CSS and JavaScript in `Automata.App/wwwroot`, hosted in one WebView2 pane and talking to the C# host over `postMessage`. The other pane is the target browser. LLM access goes through MindAttic.Legion; credentials resolve through MindAttic.Vault.

## Building

```powershell
dotnet build Automata.slnx
dotnet test Automata.Tests
```

### Run from source

```powershell
launch.bat
```

`launch.bat` (repo root) stops any running instance, clean-rebuilds, publishes to `C:\Apps\Automata\` (a convention specific to the maintainer's own machine) and opens that deployed copy, so a double-click always runs current source, never a stale build. `dotnet run --project Automata.App` works too for a quick dev run. The publish step is `tools/deploy.ps1`; it never touches the WebView2 profile folders under `%LocalAppData%\MindAttic\Automata\`, so site logins survive redeploys.

### Publish a distributable build

```powershell
dotnet publish Automata.App -c Release -r win-x64 --self-contained false
```

Framework-dependent: the output (`Automata.App\bin\Release\net10.0-windows\win-x64\publish\`) is a few MB and needs the .NET 10 Desktop Runtime on the machine it runs on. Windows offers to install that automatically the first time `Automata.App.exe` runs, if it is missing. Hand that folder to someone and it runs as-is; there is no separate installer.

## Testing

Beyond the NUnit tests in `Automata.Tests`, four acceptance harnesses drive the real app and the real runner (run `npm install` in `tools/` first; they use Playwright and axe-core):

- `tools/verify-ui.mjs`: the sidebar over CDP, including a WCAG 2.2 AA baseline for both themes.
- `tools/verify-js.mjs`: the injected scripts themselves (the naming rule as a pure function, and fingerprint, resolver and harvest against a real DOM in the real WebView2).
- `tools/verify-demos.mjs`: every generated example, run in a browser.
- `tools/verify-shop.mjs`: the harvest-and-loop total, checked three ways against the pages themselves.

`tools/collect-names.mjs` sits beside them and is not a check at all; it never fails, it reports. It visits a spread of real sites and sorts every id and class they use into what `stability.js` would keep and what it would throw away, which is how the filter gets tuned against what shipped rather than against what somebody imagined. What it turns up goes into the corpus in `verify-js.mjs`, which is where a pattern is proven.

`tools/verify-live.mjs --live` is deliberately outside that set. It runs the acceptance profiles (a Google search, a Bing search and a webmail inbox) against the real sites, after `automata-runner profiles seed` installs them. It is never part of the green bar, because a search engine redesigning itself is not a regression in this repo. The mail profile reads its account from `AUTOMATA_MAIL_URL`, `AUTOMATA_MAIL_USER` and `AUTOMATA_MAIL_PASS`, and skips itself by name when they are not set.

## Limitations

- Windows only: the host is WPF and the browser is WebView2.
- A closed shadow root that existed before the toolkit was installed is not reachable, and never becomes reachable. There is one instant when a closed root is visible to anything, and it is the instant it is created.
- A frame that runs no script at all (`sandbox` without `allow-scripts`) cannot answer.
- Forwarding an action into a cross-origin frame needs `new Function` there, so a frame whose CSP forbids `unsafe-eval` can be searched, read and harvested but not acted in, and says so. Nothing on the ordinary same-document path goes near `eval`.
- Recording inside a closed shadow root is out, not merely unbuilt: the event is retargeted with an empty `composedPath` and there is nothing to read.
- Attaching a file inside a cross-origin frame is out, because that needs a handle on the element and a handle does not cross an origin boundary.
- Scheduled runs need a logged-on user session (see [Command-line runner](#command-line-runner)).

## Documentation

- Status, history and remaining work: [NEXT_STEPS.md](NEXT_STEPS.md)
- Agent and contributor instructions: [AGENTS.md](AGENTS.md)
- Shared browser layer: [AutoWebNav](https://github.com/mindattic/AutoWebNav)

## License

MIT. See [LICENSE](LICENSE).

---

Part of [MindAttic](https://mindattic.com) — see more projects at [github.com/mindattic](https://github.com/mindattic). Related: [AutoWebNav](https://github.com/mindattic/AutoWebNav), [MindAttic.Legion](https://github.com/mindattic/MindAttic.Legion), [MindAttic.Vault](https://github.com/mindattic/MindAttic.Vault).
