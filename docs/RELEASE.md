---
layer: release
status: living
---
# Automata — Release charter

> The one finish line this repo is working toward. Every open ticket in this repo sits under one of the epics
> below, and every epic is part of the milestone. Work that does not fit is out of scope until this milestone is
> reached (owner, 2026-11-27, MAT-T319).

## 1. What this is
Record-once, replay-many browser automation for Windows: record what you do in a live WebView2 pane, refine it in
a step tree editor, and replay or schedule it with a self-healing element resolver. For the owner's own unattended
web chores on sites with no API.

## 2. The milestone: Automata 3.0 {#milestone}
- **Milestone:** MS-85, "Automata 3.0: owner-verified record, replay and schedule"
- **Published means:** an internal tool on `R:\MindAttic\Apps\Automata\{Debug,Release}` (`Automata.App.exe` and
  `automata-runner.exe`), deployed by `run.ps1`. Version 3.0.0, tagged `v3.0.0`.
- **The natural stopping point:** v3 phases 1-36 are built (NEXT_STEPS.md). What is left is closing the gaps that
  file itself lists: the demo harness has three failures, a dataset cannot be deleted from the UI, the runner still
  registers its own Task Scheduler job although MindAttic.Scheduler now runs the tick, and no human has used it end
  to end. Once those close and the docs match, Automata is a complete product; new capability waits for a new
  milestone.

## 3. Done when (acceptance checks)
1. build — `Automata.slnx` builds clean on main.
2. harness — `node tools/verify-demos.mjs` passes every check.
3. exists — `R:\MindAttic\Apps\Automata\Release\Automata.App.exe`.
4. signoff — the owner records a Google search by hand, refines it in the editor and replays it.

## 4. Epics, in priority order
| Priority | Epic (ticket) | What it delivers | Features |
|---|---|---|---|
| 900 | AUT-T53 Ship-ready engine | The engine gaps NEXT_STEPS.md lists, closed | AUT-T55 Demo suite green (990; task AUT-T60), AUT-T56 Scheduler hand-off (980; task AUT-T20), AUT-T57 Dataset management (970; task AUT-T61) |
| 800 | AUT-T54 3.0 documentation | Docs and tag that describe 3.0 as it ships | AUT-T58 README and history (890; task AUT-T17), AUT-T59 Release tag (880; task AUT-T62) |

AUT-T50 (deploy on R:) and AUT-T51 (remove `C:\Apps\Automata`) stay under MindAttic.Agency's MAT-T290 deploy
rollout (milestone MS-20); they are not part of this tree.

## 5. Out of scope (not before this milestone)
- Several instances running the same task at once with different bindings: cut in phase 31, everything runs one
  thing at a time on purpose.
- Recording inside a closed shadow root, and attaching a file inside a cross-origin frame: refusals, not gaps (the
  browser does not expose the information).
- New step actions or wait modes (`WaitMode.UntilSignal` stays undemonstrable).
- Routing the tool-calling LLM adapters through MindAttic.Legion: waits for Legion to support tool calling.
- Open-ended bughunts, test-suite runs and cleanup sweeps: real bugs are filed one by one as fix tickets.

## 6. Current verified state
- Record, tree editor, self-healing replay, flow control, datasets, harvesting, Gherkin authoring, Chrome Recorder
  import/export, scheduling with park/resume, live lane strip: NEXT_STEPS.md phases 1-35.
- SQLite store (EF Core 10) with one-time legacy import: phase 36; 589 NUnit tests, `verify-ui` 84/84,
  `verify-js` 13/13, `verify-shop` passing at that point.
- Settings and LLM keys in MindAttic Vault (fab8d57); Media.Capture packages (a2c16c3); recent fixes AUT-T41..T49.
- Shared run menu deploying to `R:\MindAttic\Apps\Automata` (7177f42); both Debug and Release exist there.
- On AutoWebNav 5.0.0 and MindAttic.Components.Wpf 19.0.0.

## 7. Decision record
- 2026-11-27 — charter drafted from README/NEXT_STEPS/AGENTS/tickets; Legion vote: Approve, 9/9 voters, plurality
  quorum reached, consensus 1.0; no dissent.
- Triage: 9 open (plus the triage job AUT-T52) → done 0, duplicate 0, wont-do 5 (AUT-T3 superseded by
  Components.Wpf 19.0.0; AUT-T11, T15, T16, T37 sweeps and nice-to-haves), kept 2 (AUT-T20, AUT-T17 reworded),
  left under MAT-T290 2 (AUT-T50, AUT-T51); 3 new tasks (AUT-T60..T62).
