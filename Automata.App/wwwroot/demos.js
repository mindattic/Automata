// The "Examples…" dialog: what the generated demo tasks are, and what regenerating does.
//
// Demos is generated territory. Regenerating always puts every example back to the version this
// build ships, unconditionally — the same demo, in the same order, every time. There is no
// per-example negotiation and nothing to warn about: the answer to "I want to keep my version" is
// to move or duplicate that task into a collection of your own, where nothing regenerates
// anything. Both of those gestures take the example marker off the copy, so it stops being an
// example the moment you claim it — a capability that already exists (the tree's row menu), not
// something this dialog needs to broker.

import { $, esc, post, state } from './core.js';
import { trapFocus } from './modal.js';
import { closeSettings } from './settings.js';
import { startTour } from './tour.js';

var STATE_TEXT = {
    missing: 'not there yet — will be added',
    current: 'up to date',
    stale: 'an older build made it — will be refreshed',
    edited: 'you have changed this one',
};

var returnEl = null;

export function openDemosDialog() {
    // Settings closes rather than stacking behind this. Two overlapping modals fight over the
    // focus trap and the backdrop, and the button that opened this one is inside the other — so
    // focus goes back to the Settings button, which is somewhere that still exists afterwards.
    closeSettings();
    returnEl = $('btn-settings');
    // Ask the host for a fresh survey; renderDemosDialog runs again when the answer lands.
    post('surveyDemos');
    $('demos-modal').classList.remove('hidden');
    renderDemosDialog();
    $('demos-modal-close').focus();
}

function close() {
    if ($('demos-modal').classList.contains('hidden')) return;
    $('demos-modal').classList.add('hidden');
    if (returnEl && document.body.contains(returnEl)) returnEl.focus();
    returnEl = null;
}

/// Re-rendered whenever a survey arrives, so the dialog is never showing a stale verdict.
export function renderDemosDialog() {
    var body = $('demos-body');
    if (!body || $('demos-modal').classList.contains('hidden')) return;

    var survey = state.demos;
    if (!survey) {
        body.innerHTML = '<p class="scope-note">Looking at the examples…</p>';
        return;
    }

    var items = survey.items || [];

    var html = '<p class="scope-note">Pages are written to <code>' + esc(survey.root || '') +
        '</code> and rebuilt every time.</p><ul class="demo-list">' +
        items.map(function (d) {
            return '<li class="demo-row" data-demo="' + esc(d.key) + '">' +
                '<b>' + esc(d.name) + '</b> <span class="key-status">' +
                esc(STATE_TEXT[d.state] || d.state) + '</span></li>';
        }).join('') + '</ul>';

    html += '<p class="scope-note">Regenerating always puts every example back to the version ' +
        'this build ships, in the same order — including any you have changed. To keep a change, ' +
        'duplicate that task (or its collection) into one of your own first; a copy stops being ' +
        'an example and is never touched by this again.</p>';

    body.innerHTML = html;
}

function regenerate() {
    post('regenerateDemos');
    close();
}

// Take Tour is a regenerate too — the tour never runs against anything but the shipped
// version — plus the guided walkthrough after. Closing this dialog first is what lets the
// tour's own popups (and the tree selection it drives) show through without a second overlay
// behind them.
function takeTour() {
    close();
    startTour();
}

$('set-regen-demos').addEventListener('click', openDemosDialog);
$('demos-modal-close').addEventListener('click', close);
$('demos-regen').addEventListener('click', regenerate);
$('demos-take-tour').addEventListener('click', takeTour);
$('demos-modal').addEventListener('keydown', function (e) { trapFocus($('demos-modal'), e); });
$('demos-modal').addEventListener('mousedown', function (e) {
    if (e.target === $('demos-modal')) close();
});
document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') close();
});
