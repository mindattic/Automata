// The host-facing surface. The host calls these directly as injected script
// (window.ssPanel.onX(...)), which is the inbound half of the bridge; post() in core.js is the
// outbound half.

import {
    $, logEl, state, post, announce, findTask, findCollection, spliceStepsAt, saveTask,
} from './core.js';
import { render, renderRecPreview } from './render.js';
import { renderDatasets } from './data.js';
import { renderRuns } from './runs.js';
import { renderLanes } from './lanes.js';
import { renderSchedule, onSchedulePushed } from './schedule.js';
import { showDraft, showFeatureView } from './flow.js';
import { maybeStartTutorial, advanceTutorial } from './tutorial.js';
import { showBuildTab } from './tabs.js';
import { LLM_PROVIDERS } from './settings.js';

window.ssPanel = {
    onLog: function (line) {
        var div = document.createElement('div');
        div.className = 'log-line';
        div.textContent = line;
        logEl.appendChild(div);
        logEl.scrollTop = logEl.scrollHeight;
    },
    onRunState: function (running) {
        if (!running) post('getRuns');
        state.running = running;
        if (!running) state.pausedStepId = null;
        if (running) state.stepStatus = {};
        $('run').disabled = running;
        $('cancel').disabled = !running;
        announce(running ? 'Run started.' : 'Run finished.');
        render();
    },
    onTaskStarted: function (payload) {
        var taskId = payload && payload.taskId;
        if (!taskId) return;
        showBuildTab();
        state.expanded[taskId] = true;
        state.sel = { collectionId: (payload.collectionId || state.sel.collectionId), taskId: taskId, stepId: null };
        var started = findTask(taskId);
        if (started) announce('Running task ' + started.name + '.');
        render();
    },
    onState: function (model) {
        state.collections = (model && model.collections) || [];
        // Drop selections that no longer exist (deleted/moved elsewhere).
        if (state.sel.taskId && !findTask(state.sel.taskId)) state.sel.taskId = state.sel.stepId = null;
        if (state.sel.collectionId && !findCollection(state.sel.collectionId)) state.sel.collectionId = null;
        render();
        maybeStartTutorial();
        advanceTutorial();
    },
    onStepEvent: function (e) {
        state.stepStatus[e.stepId] = e.status;
        if (e.status !== 'paused' && state.pausedStepId === e.stepId) state.pausedStepId = null;
        // Only the terminal, actionable outcome is announced — narrating every step's
        // "running" would bury the failure that actually matters.
        if (e.status === 'failed') announce('Step failed: ' + (e.message || 'no detail given') + '.');
        render();
    },
    onPaused: function (stepId) {
        state.pausedStepId = stepId;
        announce('Run paused — press Continue to resume.');
        render();
    },
    onRecordingState: function (recording) {
        state.recording = recording;
        if (recording) showBuildTab();
        if (!recording) { renderRecPreview([]); state.gapInsert = null; }
        render();
    },
    onRecordedSteps: function (steps) {
        renderRecPreview(steps);
    },
    onGapRecorded: function (payload) {
        var task = payload && findTask(payload.taskId);
        if (!task || !payload.steps || !payload.steps.length) return;
        if (!spliceStepsAt(task, payload.parentStepId, payload.index, payload.steps)) return;
        state.sel = { collectionId: state.sel.collectionId, taskId: payload.taskId, stepId: payload.steps[0].id };
        state.expanded[payload.taskId] = true;
        saveTask(task);
    },
    onFlowDraft: function (draft) {
        showDraft(draft || {});
    },
    onFeatureView: function (view) {
        showFeatureView(view || {});
    },
    onRuns: function (payload) {
        state.runs = (payload && payload.runs) || [];
        state.runRoot = (payload && payload.root) || '';
        renderRuns();
    },
    onLanes: function (payload) {
        state.lanes = (payload && payload.processes) || [];
        renderLanes();
    },
    onSchedule: function (payload) {
        state.schedule = (payload && payload.entries) || [];
        state.scheduleError = (payload && payload.error) || '';
        state.timeZones = (payload && payload.timeZones) || state.timeZones;
        state.localTimeZoneId = (payload && payload.localTimeZoneId) || state.localTimeZoneId;
        renderSchedule();
        // Tree rows carry a chip for anything scheduled, so the tree has to be redrawn too —
        // otherwise a schedule added here would not show up on its collection until something
        // else happened to re-render.
        render();
        onSchedulePushed(state.scheduleError);
    },
    onDatasets: function (payload) {
        state.datasets = (payload && payload.datasets) || [];
        state.datasetRoot = (payload && payload.root) || '';
        renderDatasets();
    },
    onSettings: function (s) {
        state.engineDefaults = (s && s.engineDefaults) || null;
        state.engineFloor = (s && s.engineFloor) || null;
        var radius = (s && s.borderRadius != null) ? s.borderRadius : 5;
        document.documentElement.style.setProperty('--radius', radius + 'px');
        $('set-radius').value = radius;
        $('set-radius-value').textContent = radius + 'px';

        LLM_PROVIDERS.forEach(function (p) {
            $('llm-' + p).checked = (s && s.provider) === p;
            var info = s && s.keys && s.keys[p];
            // The key never crosses the bridge — the input's placeholder shows the status.
            $('key-' + p).placeholder = info ? info.hint : '';
        });
    },
};
