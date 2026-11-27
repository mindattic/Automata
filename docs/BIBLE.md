---
layer: bible
status: living
---
# Automata — Project Bible

> Single source of truth for what Automata IS, is NOT, and the rules that keep it coherent.
> README says how to build and run it; this says how to think about it. The finish line lives in
> `docs/RELEASE.md`; the phase-by-phase build log is `NEXT_STEPS.md`.

## 1. The one sentence {#AUT-§1}
Record a browser task once in a live WebView2 pane, edit it as a typed step tree, and replay or schedule it with a
resolver that keeps finding elements after a site redesign.

## 2. The product promise {#AUT-§2}
- Automate websites that have no API, without writing code.
- A recorded task survives markup changes: every element is remembered eight ways and self-heals.
- Runs unattended: schedules, chains and park/resume through the headless `automata-runner`.
- Recording and replay never need an LLM; an LLM is an opt-in helper (draft steps, rescue one step).

## 3. What it is NOT {#AUT-§3}
- Not Playwright or Selenium: the host is WebView2, the recorder and resolver are its own.
- Not a scraper framework for code: tasks are data, built in the UI or from Gherkin.
- Not concurrent: since phase 31 one browser runs one task at a time.
- Not site-specific: site knowledge lives in tasks, browser mechanics live in AutoWebNav.

## 4. Architecture canon {#AUT-§4}
```text
Automata.App (WPF, net10.0-windows)      Automata.Runner (automata-runner.exe)
  sidebar panel (wwwroot ES modules)        run / status / schedule / tick / install
  target pane (WebView2) <-- AutoWebNav.WebView2 -->  off-screen lanes
            \                                  /
             Automata.Core (net10.0): model, stores, engines, scheduling, authoring
                         |
             %LocalAppData%\MindAttic\Automata\automata.db (SQLite, EF Core 10)
```
### 4.1 Projects
`Automata.App` (window, bridge, recorder wiring), `Automata.Core` (everything testable), `Automata.Runner` (thin
CLI host over `RunnerCliDispatcher`), `Automata.Tests` (NUnit), `tools/` (UI, JS, demo and shop harnesses).
### 4.2 Domain model
Collection 1:M TaskDefinition 1:M Step (recursive substeps); `StepAction`; `ElementFingerprint`; engine-settings
overrides at global, collection, task and step scope; datasets; runs with events and outputs; schedules; parked
runs.
### 4.3 Key services
`ReplayEngine` (one step), `WorkflowEngine` (the tree walk and control flow), `FingerprintResolver`,
`RecordingBuilder`, `CronSchedule` and `TriggerEvaluator`, `FlowAuthoringService` (Gherkin), the stores.

## 5. The Laws {#AUT-§5}
Inherits `MindAttic.HouseRules.md` by reference.
1. {#AUT-LAW-1} The database is the only store; files are how work travels (`*.automata.zip`, `*.automata.json`).
2. {#AUT-LAW-2} Delete hides (HOUSE-LAW-2); a hidden row keeps its id.
3. {#AUT-LAW-3} A control that does nothing is worse than no control: unbuilt settings stay hidden.
4. {#AUT-LAW-4} Refuse with a reason rather than guess: ambiguous matches, unfireable schedules, empty harvests.
5. {#AUT-LAW-5} Every `StepAction`, `WaitMode` and `ConditionOp` has a seeded demo (`DemoCoverageTests`).
6. {#AUT-LAW-6} The first-run tutorial and the floor check survive every change.
7. {#AUT-LAW-7} Silent actions never lose work; only an explicit, named action may.

## 6. Verified state {#AUT-§6}
See `docs/RELEASE.md` §6: phases 1-36 built, 589 NUnit tests and the UI/JS/shop harnesses green at phase 36,
`verify-demos` with three known failures, deployed to `R:\MindAttic\Apps\Automata`.

## 7. Active frontier {#AUT-§7}
`docs/RELEASE.md`: milestone MS-85, epics AUT-T53 (ship-ready engine) and AUT-T54 (3.0 documentation).

## 8. Quality bar {#AUT-§8}
A feature is done when it builds clean, has NUnit coverage in `Automata.Tests` for its Core logic, a seeded demo if
it adds an action, a `verify-ui` check if it changes the panel, meets WCAG 2.2 AA in the sidebar, and its commit
names its ticket.

## 9. Glossary {#AUT-§9}
- **Collection / task / step:** the three levels of the tree.
- **Fingerprint:** the eight-way identity of an element; **self-heal** rewrites it after a fallback match.
- **Lane:** an off-screen WebView2 browser used by the runner.
- **Park:** a long wait checkpoints, releases its lane and is resumed by a later `tick`.
- **Commit point:** an informational marker on a submit-like step.
