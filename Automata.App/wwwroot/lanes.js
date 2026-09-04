// The live lane strip: which browser is running what, right now.
//
// The lanes worth watching are never this window's. The app has one browser pane and no pool; the
// pool lives in `automata-runner`, which is headless and usually running unattended at 3am. Each
// process publishes its lanes to a small file as they change hands, and this polls for them — so
// the Runs tab shows what is happening now, not only what has already happened.
//
// The strip exists only while something is actually running. That is deliberate on two counts: a
// permanently empty widget teaches a new user nothing, and the first-run floor requires that no
// advanced affordance is on screen before it is relevant.

import { $, esc, post, state } from './core.js';

// Only while the Runs tab is on screen. Polling a hidden panel would keep re-reading the disk to
// update markup nobody can see.
var POLL_MS = 2000;
var timer = null;

function ago(iso) {
    var started = new Date(iso);
    if (isNaN(started.getTime())) return '';
    var seconds = Math.max(0, Math.round((Date.now() - started.getTime()) / 1000));
    if (seconds < 60) return seconds + 's';
    if (seconds < 3600) return Math.round(seconds / 60) + 'm';
    return (Math.round(seconds / 360) / 10) + 'h';
}

export function renderLanes() {
    var host = $('lane-host');
    if (!host) return;

    var processes = (state.lanes || []).filter(function (p) {
        return (p.lanes || []).some(function (l) { return l.busy; });
    });

    if (!processes.length) {
        // Removed from the DOM, not hidden: an empty strip is not a thing worth having on screen
        // or in the accessibility tree.
        host.innerHTML = '';
        return;
    }

    // One #lane-strip whatever the number of processes — an id has to stay unique, and the floor
    // check's "no advanced affordance on screen" rule is written against exactly this selector.
    host.innerHTML = '<div id="lane-strip">' + processes.map(function (p) {
        var busy = (p.lanes || []).filter(function (l) { return l.busy; });
        var warm = (p.lanes || []).length - busy.length;
        return '<div class="lane-group">' +
            '<div class="section-head">' +
            '<h3 class="section-label">Running now — ' + esc(p.processName) +
            (p.targetName ? ' · ' + esc(p.targetName) : '') + '</h3>' +
            '<span class="lane-meta">' + busy.length + ' of ' + esc(p.maxConcurrency) +
            ' lane' + (p.maxConcurrency === 1 ? '' : 's') + ' busy' +
            // A warm lane is idle but still open, holding its cookies for the next task that
            // wants that profile. Worth showing: it explains why the browser count exceeds the
            // work in flight.
            (warm > 0 ? ', ' + warm + ' warm' : '') + '</span>' +
            '</div>' +
            '<div role="list" aria-label="Browser lanes running now">' +
            busy.map(function (l) {
                return '<div class="lane-row" role="listitem">' +
                    '<span class="status" role="img" aria-label="running">⟳</span>' +
                    '<span class="name">' + esc(l.taskName || p.targetName || 'a task') + '</span>' +
                    '<span class="lane-step">' + esc(l.stepLabel || 'starting…') + '</span>' +
                    '<span class="lane-meta">' + esc(l.laneId) + ' · ' + esc(l.profileKey) +
                    (l.startedUtc ? ' · ' + esc(ago(l.startedUtc)) : '') +
                    '</span></div>';
            }).join('') +
            '</div></div>';
    }).join('') + '</div>';
}

export function startLanePolling() {
    if (timer) return;
    post('getLanes');
    timer = setInterval(function () { post('getLanes'); }, POLL_MS);
}

export function stopLanePolling() {
    if (!timer) return;
    clearInterval(timer);
    timer = null;
}
