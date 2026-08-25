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
dotnet run --project Automata.App
```

Launches a two-pane window: the **sidebar** (collections/tasks/steps tree, step editor, record
and replay controls) and the live **browser pane** the automation acts on. The browser pane uses
its own persistent WebView2 profile, so a site login survives app restarts without touching your
regular browser.

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
- **`isCommitPoint`** — marks a permanent-write boundary (submit/save/purchase). Auto-flagged at
  record time for submit-looking clicks; toggle it in the editor.

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

## Replaying

Select a task, then:

- **▶ Run** — execute everything.
- **Dry Run** — execute for real but **stop before** the first `isCommitPoint` step: exercises
  the whole flow without committing any permanent submission.
- **Validate** — resolve and flash-highlight every step's element, mutating nothing
  (`navigate` steps still execute so multi-page tasks validate end-to-end).

Step rows light up live (running / passed / failed / healed / paused). Every run also writes a
log file to `~\Automata\logs\<timestamp>-<task>.log`.

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

## Storage, import & export

Everything is human-readable JSON under your profile:

```
~\Automata\
  collections\<collectionId>\collection.json
  collections\<collectionId>\tasks\<taskId>.json
  logs\<timestamp>-<task-slug>.log
```

A task file is fully self-contained and shareable as-is. In the sidebar you can create, rename,
duplicate, and delete collections and tasks; **drag a task onto another collection** to move it;
**drag steps** to reorder them (drop on a row's middle to nest as a substep); drag anything to
the trash zone to delete.

**⇩ Export** writes the selected collection (or single task) as a `*.automata.zip`;
**⇪ Import** reads one back. Imports never overwrite: colliding ids are regenerated, colliding
names get ` (2)` suffixes, and a task imported without its collection lands in an auto-created
**Imported** collection. A task saved without any parent gets a **Default** collection — a task
never exists without a collection.

## Free-text AI mode (advanced)

The original plain-English path is still there, folded under *AI task (advanced)*: type an
instruction and an LLM drives the pane through generic DOM tools (click, set field, type,
select, check, upload, page status). Recording + the WYSIWYG editor are the primary workflow —
free text is for one-offs and exploration. Providers (Anthropic first, OpenAI fallback) read
credentials from MindAttic.Vault.

## Architecture

```
Automata.App    WPF host: two WebView2 panes, postMessage bridge, AutomationController
Automata.Core   engine: model, store, zip archive, fingerprint/resolver JS (embedded),
                replay engine, recorder coalescer, LLM tool loop — WebView2-free (IBrowserSurface)
Automata.Tests  NUnit 4 — 64 tests over model, store, archive, resolver, replay, recorder, logs
```

Known limitations (v2): top-document, light-DOM only (no cross-origin iframes / shadow roots);
Enter-to-submit-only sites need a recorded click or navigation; single app instance assumed.
