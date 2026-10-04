# MindAttic project agent entrypoint

Read the shared protocol at ..\mindattic-agent-standard\AGENTS.md and this project's own
documentation. Common prompt commands are implemented by the shared runner; do not add a second
copy under a provider-specific command folder.

Watching the app while the person talks to you about it: Automata registers with AutoWebNav's Live
Observation (panel and target panes; preferred CDP ports 9370/9371, still overridable with
AUTOMATA_PANEL_CDP_PORT / AUTOMATA_TARGET_CDP_PORT for tools/verify-ui.mjs). From the AutoWebNav
repo, `node tools/awn-observe.mjs status automata` (run/record state, selected task, panel log) and
`watch automata`. Every session's target-pane actions are in Downloads as
`Automata-session-*.autowebnav-recording.json`, independent of the ● Record button (which still
records a task to edit).
