// The Runs tab: what has run, and how it went.
//
// The list is read from the run store on disk, not from anything this window remembers — which is
// what lets it show runs it did not start, including ones the headless runner produced while the
// app was closed.

import { $, esc, post, state } from './core.js';
import { renderLanes } from './lanes.js';

var STATE_GLYPH = { running: '⟳', parked: '⏸', passed: '✓', failed: '✗' };

// A parked run's manifest is still open, so on its own it looks exactly like one that is
// executing. The parked record is what distinguishes them — without it the tab would show an
// hours-old overnight run as "running" and give no hint why.
function outcomeOf(run) {
    if (run.success === null || run.success === undefined) return run.parked ? 'parked' : 'running';
    return run.success ? 'passed' : 'failed';
}

function when(run) {
    var started = new Date(run.startedUtc);
    if (isNaN(started.getTime())) return '';
    var today = new Date();
    var sameDay = started.toDateString() === today.toDateString();
    var time = started.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    return sameDay ? time : started.toLocaleDateString() + ' ' + time;
}

function took(run) {
    if (!run.endedUtc) return '';
    var ms = new Date(run.endedUtc) - new Date(run.startedUtc);
    if (!(ms >= 0)) return '';
    return ms < 1000 ? ms + 'ms' : Math.round(ms / 100) / 10 + 's';
}

function whenText(iso) {
    var d = new Date(iso);
    if (isNaN(d.getTime())) return '';
    var time = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    return d.toDateString() === new Date().toDateString() ? time : d.toLocaleDateString() + ' ' + time;
}

// Says what is holding the run and what will pick it back up. "Due now" is worth calling out
// separately: it means the wait is over and the run is only waiting on the next scheduler tick,
// which is a very different thing from still counting down.
function parkedNote(parked) {
    return '<div class="run-summary parked-note">Parked on “' +
        esc(parked.stepLabel || parked.taskName) + '”, ' + esc(parked.reason) +
        ' — no browser is held. ' +
        (parked.due
            ? 'The wait is over; the next <code>automata-runner tick</code> carries it on.'
            : 'Resumes ' + esc(whenText(parked.resumeAtUtc)) + ', on the first tick after that.') +
        '</div>';
}

export function renderRuns() {
    var view = $('view-runs');
    if (!view) return;

    var runs = state.runs || [];
    var head =
        '<div class="section-head"><h2 class="section-label">Runs</h2>' +
        '<span class="node-btns">' +
        '<button class="mini" id="btn-refresh-runs" aria-label="Re-read the run history from disk"' +
        ' data-tooltip="Re-read from disk">⟳</button>' +
        '<button class="mini" id="btn-open-runs" aria-label="Open the Runs folder in File Explorer"' +
        ' data-tooltip="Open the Runs folder">📁</button>' +
        '</span></div>';

    // The live strip lives above the history, inside a host this function re-creates. renderRuns
    // replaces the whole panel, so the strip has to be re-hosted here and re-filled from state
    // rather than left to the next poll - otherwise a refresh would blank it for two seconds.
    var host = '<div id="lane-host"></div>';

    if (!runs.length) {
        view.innerHTML = head + host +
            '<p class="empty-state">No runs yet. Every run — from here or from ' +
            '<code>automata-runner</code> — is recorded in ' + esc(state.runRoot || 'the Runs folder') +
            ', so finished runs show up here even if this window was closed at the time.</p>';
    } else {
        view.innerHTML = head + host +
            '<div id="run-list" role="list" aria-label="Recent runs">' +
            runs.map(function (r) {
                var outcome = outcomeOf(r);
                var duration = took(r);
                // Glyph AND word AND colour: the outcome must never be carried by colour alone.
                return '<div class="run-row st-' + outcome + '" role="listitem" data-run="' + esc(r.id) + '">' +
                    '<span class="status" role="img" aria-label="' + outcome + '">' +
                    (STATE_GLYPH[outcome] || '·') + '</span>' +
                    '<span class="name">' + esc(r.name) + '</span>' +
                    '<span class="run-meta">' + esc(outcome) +
                    (duration ? ' · ' + esc(duration) : '') +
                    ' · ' + esc(when(r)) +
                    (r.trigger && r.trigger !== 'manual' ? ' · ' + esc(r.trigger) : '') +
                    '</span></div>' +
                    (r.parked ? parkedNote(r.parked) : '') +
                    (r.summary ? '<div class="run-summary">' + esc(r.summary) + '</div>' : '');
            }).join('') +
            '</div>';
    }

    renderLanes();

    var refresh = $('btn-refresh-runs');
    if (refresh) refresh.addEventListener('click', function () { post('getRuns'); });
    var open = $('btn-open-runs');
    if (open) open.addEventListener('click', function () { post('openRuns'); });
}
