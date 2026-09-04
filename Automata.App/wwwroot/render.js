// Render orchestration. Deliberately thin: every mutation ends in a full re-render from `state`,
// which is what keeps the host's pushed tree authoritative.
//
// This module and tree.js import each other. That is safe here because both sides export hoisted
// function declarations and neither calls the other during module evaluation.

import { $, state, esc } from './core.js';
import { renderTree } from './tree.js';
import { renderEditor } from './editor.js';

export function render() {
    renderToolbar();
    renderTree();
    renderEditor();
}

export function renderToolbar() {
    $('btn-record').disabled = state.recording || state.running;
    $('btn-stop').disabled = !state.recording;
    // While a gap-recording session is armed, Continue must stay disabled — releasing the
    // paused replay would dispatch the original next step's real CDP events while the JS
    // recorder is still capturing, corrupting the in-progress recording.
    $('btn-continue').disabled = !state.pausedStepId || state.recording;
    $('btn-cancel-run').disabled = !state.running;
    $('btn-export').disabled = !state.sel.taskId && !state.sel.collectionId;
}
// ---- recording preview ---------------------------------------------------------------------

export function renderRecPreview(steps) {
    var box = $('rec-preview');
    if (!state.recording && (!steps || !steps.length)) { box.classList.add('hidden'); return; }
    box.classList.remove('hidden');
    $('rec-steps').innerHTML = (steps || []).map(function (s) {
        return '<div class="node step"><span class="name">' + esc(s.label || s.action) + '</span>' +
            (s.isCommitPoint ? ' <span class="flag commit">◆</span>' : '') + '</div>';
    }).join('') || '<div class="empty">…perform actions in the browser pane…</div>';
}
