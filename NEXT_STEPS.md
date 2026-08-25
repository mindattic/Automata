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
6. **ReplayEngine** — StepEvent stream, pauseForUser gate (`ReplayControl`), Dry Run stops
   before first commit point, Validate resolves without mutating, per-action post-condition
   auto-confirm, settle-wait, self-heal write-back. `RunLogWriter` → `~\Automata\logs\`.
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
- **v2 limitations to lift later**: cross-origin iframes & shadow DOM piercing;
  `submitWithEnter` flag on typeText for Enter-only forms; file locking for multi-instance.
