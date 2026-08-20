# Automata — status and remaining work

## Where this came from

Automata generalizes the browser-automation pattern proven out in `Prose.KdpPublish` (a WPF app
that drives KDP's publishing flow through a WebView2 pane) into a reusable engine: record a
browser session once, and replay it — once, many times, sequentially or in parallel —
recognizing the same DOM elements even after a site's markup changes, by falling back across
multiple identification strategies (id, CSS selector, class, XPath, visible text/content, ARIA
role, page context). Target end-to-end proof points: a Google search, a Bing search, and reading
the first 20 subject lines out of an email inbox.

KdpPublish itself does **not** record-and-replay — it uses an LLM tool-calling loop that
re-decides every step live from a hand-written playbook. What ported over from it (Phase 0/1,
done — see below) is the WebView2/CDP host plumbing, the provider-neutral tool-calling engine,
and a set of proven generic DOM-manipulation primitives. The KDP-specific business logic
(bookshelf search, categories, ASIN capture, manifest tracking) did not port.

Chosen architecture: keep the **WebView2 WPF host** (not Playwright), but build the
locator/recorder engine **from scratch, Playwright-inspired** (resilient, multi-strategy
locators) rather than taking a Playwright dependency.

## Done — Phase 0 (repo/launcher bootstrap) + Phase 1 (port generic infra, runnable shell)

Commit `30bcdb2` on `master`. Solution: `Automata.slnx` — `Automata.App` (WPF/WebView2 host),
`Automata.Core` (engine library), `Automata.Tests` (NUnit4, 10/10 passing). Build clean, 0
warnings.

Ported from `Prose.KdpPublish` / `Prose.Core\Services\Operator\` and generalized:
- WebView2/CDP host shell: dual-pane pattern (sidebar + target site), `postMessage` bridge,
  trusted clicks/keystrokes via CDP, `DomFileInjector` (no native file-picker dialog),
  `ScriptDialogOpening` auto-accept, `NewWindowRequested` redirect-into-pane.
- `IBrowserSurface` (ex-`IKdpBrowser`) — keeps `Automata.Core` WebView2-agnostic.
- Provider-neutral LLM tool-calling loop: `IToolCallingLlm`, `ToolLoopMessage`/`AssistantPart`/
  `ToolResultPart`, `OperatorEvent`, `AnthropicToolCallingLlm`, `OpenAiToolCallingLlm`,
  `BrowserOperatorService` (the generic loop, KDP business logic and hard-gates stripped out).
- Generic tools only: `click_button`, `check_checkbox`, `select_form_option`, `set_field`,
  `type_into_field` (real-CDP-keystroke variant), `upload_file`, `get_page_status`, `log_note`.
  Left behind entirely: everything KDP/Amazon/Prose-DB-specific (bookshelf search, categories,
  ASIN capture, manifest/publish tracking, CKEditor-specific description tool).
- MindAttic.Vault wired in (`AddMindAtticVaultFiles()` + `AddMindAtticVault(configuration)`) for
  API keys and future site-login credentials — never hard-coded, per HOUSE-LAW-3.
- Registered in MindAttic.Launcher's roster (tab color `#3C82FF`, WT scheme `MindAttic-Automata`).

**Known, acknowledged gap:** `AnthropicToolCallingLlm`/`OpenAiToolCallingLlm` are direct
vendor-SDK adapters, not routed through MindAttic.Legion — a documented exception to HOUSE-LAW-4,
because Legion currently has no tool-calling/function-calling support at all. Migrate once Legion
grows that capability; not a blocker for now.

**Not yet verified:** full WebView2 UI / postMessage-bridge behavior interactively (only a
build+launch smoke test was done, since that's not scriptable headlessly). Do a real interactive
check before building Phase 2 on top of the sidebar bridge.

## Not done yet — Phase 2, 3, 4

### Phase 2 — Recorder + fingerprint + profile format
JS injected via `AddScriptToExecuteOnDocumentCreatedAsync` captures click/input/change events on
the target pane, computing a multi-strategy `ElementFingerprint` per acted-on element (id, CSS
selector, class list, XPath, visible text, ARIA role/label) and posting it through the existing
postMessage bridge. Steps accumulate into a named, saved JSON **Profile** (ordered steps +
optional named parameters like `{{query}}` for templated re-runs).

### Phase 3 — Replayer + resolution cascade
An injected resolver script tries each fingerprint strategy in a fixed priority order against the
live DOM, stopping at the first unique match. Ambiguous/zero matches trigger an optional
LLM-repair path: hand the step's intent to the ported tool-calling loop (`BrowserOperatorService`
+ the generic tool set from Phase 1), let it complete just that one step, and optionally
re-fingerprint the result back into the profile (self-healing).

### Phase 4 — Orchestration + validation scenarios
Multiple concurrent `Automata.App` instances/panes, each with its own `userDataFolder` (same
isolation trick as the existing dual-pane setup), running the same profile with different
parameter bindings — sequential or parallel per user choice. Acceptance scenarios, built as real
recorded profiles rather than hardcoded tools:
1. Google search → extract result titles
2. Bing search → extract result titles
3. Webmail login → extract first 20 subject lines
