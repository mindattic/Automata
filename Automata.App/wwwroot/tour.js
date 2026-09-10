// Take Tour: resets the Demos collection to a known state, then walks it task by task — select,
// explain, run, advance — ending with how to build your own.
//
// Same stage-machine shape as the first-run tutorial (tutorial.js): a module-level stage, advanced
// only as the host echoes each thing back (onState after the reset, onRunState after each run),
// never by anything this module does on its own. Unlike the tutorial, this one is started
// explicitly from Settings and can be run any number of times, not just once on an empty store.

import { state, post } from './core.js';
import { openInfoModal } from './modal.js';
import { render } from './render.js';
import { closeSettings } from './settings.js';

var tourStage = null;   // null | 'reset' | 'task' | 'running'
var tourIndex = -1;

function findDemosCollection() {
    return state.collections.find(function (c) { return c.id === state.demoCollectionId; }) || null;
}

export function startTour() {
    closeSettings();
    tourStage = 'intro';
    openInfoModal('Demos',
        "A Collection is a group of Tasks. This is 'Demos' — one generated example per " +
        "capability. Press OK to reset it to the version this build ships, so the tour walks the " +
        "same thing every time, and begin.",
        function () { tourStage = 'reset'; post('regenerateDemos'); });
}

function finishTour() {
    tourStage = null;
    tourIndex = -1;
    openInfoModal('That’s the tour',
        "That's every kind of thing a Task can do. To build your own: right-click in the tree to " +
        "add a Collection, then a Task inside it, then Steps one at a time — or press ● " +
        "Record and perform the actions yourself.",
        null);
}

function showTaskIntro(col) {
    var task = (col.tasks || [])[tourIndex];
    if (!task) { finishTour(); return; }

    state.sel = { collectionId: col.id, taskId: task.id, stepId: null };
    state.expanded[task.id] = true;
    render();

    tourStage = 'task';
    openInfoModal(task.name, task.description || '', function () {
        tourStage = 'running';
        post('runTask', { taskId: task.id });
    });
}

/// Called from onState: advances past the reset once the freshly-regenerated Demos collection
/// has come back.
export function advanceTour() {
    if (tourStage !== 'reset') return;
    var col = findDemosCollection();
    if (!col) return;
    tourIndex = 0;
    showTaskIntro(col);
}

/// Called from onRunState when a run finishes — whichever way it finished. A failed run (most
/// likely the one demo that reaches out to a real page) is not special-cased: a person running the
/// same task by hand would see the same failure, and the tour just moves on to the next one.
export function onTourRunFinished() {
    if (tourStage !== 'running') return;
    tourIndex++;
    var col = findDemosCollection();
    if (!col) { finishTour(); return; }
    showTaskIntro(col);
}
