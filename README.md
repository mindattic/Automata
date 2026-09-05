# Automata

Record-once, replay-many browser automation for MindAttic. A WPF host drives a WebView2 pane
through the Chrome DevTools Protocol; you **record** a series of browser actions once, refine
them in a **WYSIWYG step editor**, and **replay** them any time — with a self-healing element
resolver that keeps finding the same controls even after a site redesigns its markup.

## Build & test

```
dotnet build Automata.slnx
dotnet test Automata.Tests
```

## Run

```
launch.bat
```

`launch.bat` (repo root) stops any running instance, clean-rebuilds, publishes to
`C:\Apps\Automata\`, and opens that deployed copy — so a double-click always runs current
source, never a stale build. (`dotnet run --project Automata.App` works too for a quick dev run.)

The window has two panes: the **sidebar** (collections/tasks/steps tree, step editor, record and
replay controls) and the live **browser pane** the automation acts on. The browser pane uses its
own persistent WebView2 profile, so a site login survives app restarts without touching your
regular browser.

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

## Storage — human-readable, Explorer-friendly

Everything lives under your Documents folder, named the way you named it:

```
Documents\Automata\
  Collections\
    Google Searches\
      collection.json          ← collection metadata + task order
      Wolf Tshirts.json        ← one task per file: metadata + full step tree
  Logs\
    20260825-141005-wolf-tshirts.log
```

The **📁 button** in the sidebar toolbar opens the Collections folder in File Explorer.

- **A task is one file** — copy `Wolf Tshirts.json` to share that task; copy a collection folder
  to share the set.
- **Names round-trip losslessly.** Folder/file names are sanitized projections of the display
  name (illegal characters → `_`, Windows-reserved names like `CON` prefixed, overlong names
  truncated); the JSON inside keeps the original name **with illegal characters intact**, so a
  task called `Wolf: Tshirts?` shows exactly that in the app while living in
  `Wolf_ Tshirts_.json` on disk.
- **Hand-edits heal, not break.** Rename a file/folder in Explorer → the app adopts the new
  name. Copy-paste a task file → the duplicate gets a fresh identity. Drop task files into a
  folder with no `collection.json` → a collection is recovered from the folder name. A task
  saved without a parent lands in an auto-created **Default** collection.

**⇩ Export** writes the selected collection (or single task) as a `*.automata.zip`;
**⇪ Import** reads one back. Imports never overwrite: colliding ids are regenerated, colliding
names get ` (2)` suffixes, and a task imported without its collection lands in an auto-created
**Imported** collection.

## Settings

The **⚙ Settings** fold-out in the sidebar holds:

- **Anthropic key (BYO-key)** — an API key that overrides the default credential chain
  (Claude Code OAuth session → shared MindAttic credential store) for the AI paths. The escape
  hatch when the OAuth session is rate-limited or out of quota. Saved to
  `%APPDATA%\MindAttic\Automata\settings.json`; takes effect on the next run, no restart.
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
one-offs and exploration. Providers (Anthropic first, OpenAI fallback) read credentials from
MindAttic.Vault.

## Architecture

```
Automata.App    WPF host: two WebView2 panes, postMessage bridge, AutomationController
Automata.Core   engine: model, name-based store, zip archive, fingerprint/resolver JS (embedded),
                replay engine, recorder coalescer, LLM tool loop — WebView2-free (IBrowserSurface)
Automata.Tests  NUnit 4 over model, store (incl. name round-trip and healing), archive,
                resolver, replay, workflow, recorder, settings, logs, demos
```

Beyond the unit tests, three acceptance harnesses drive the real app and the real runner:
`tools/verify-ui.mjs` (the sidebar over CDP, including a WCAG 2.2 AA baseline),
`tools/verify-demos.mjs` (every generated example, run in a browser), and `tools/verify-shop.mjs`
(the harvest-and-loop total, checked three ways against the pages themselves).

A fourth is deliberately outside that set. `tools/verify-live.mjs --live` runs the **acceptance
profiles** — a Google search, a Bing search and a webmail inbox — against the real sites, after
`automata-runner profiles seed` installs them. It is never part of the green bar, because a search
engine redesigning itself is not a regression in this repo and a check that cannot tell those apart
is worth less than none. The mail profile reads its account from `AUTOMATA_MAIL_URL`,
`AUTOMATA_MAIL_USER` and `AUTOMATA_MAIL_PASS`, and skips itself by name when they are not set.

Known limitations: closed shadow roots and CROSS-origin iframes are unreachable — the page cannot
see into either, so reaching them needs per-frame evaluation over CDP rather than one script in the
top document. Open shadow roots and same-origin iframes are resolved into. Recording still happens
in the top document, so a click inside an iframe is not recorded (one inside an open shadow root
is).
