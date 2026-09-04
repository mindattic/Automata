// The "Examples…" dialog: what the generated demo tasks are in, and what regenerating will do.
//
// Demos is generated territory. Regenerating puts every example back to the version this build
// ships — there is no per-example negotiation, because the answer to "I want to keep my version"
// is not a checkbox: it is to move or duplicate that task into a collection of your own, where
// nothing regenerates anything. Both of those gestures take the example marker off the copy, so it
// stops being an example the moment you claim it.
//
// What this dialog owes the user, then, is not a set of choices but an honest warning: exactly
// which of their edits are about to go, named, before they press the button.

import { $, esc, post, state } from './core.js';
import { trapFocus } from './modal.js';
import { closeSettings } from './settings.js';

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
    var edited = items.filter(function (d) { return d.state === 'edited'; });

    var html = '<p class="scope-note">Pages are written to <code>' + esc(survey.root || '') +
        '</code> and rebuilt every time.</p><ul class="demo-list">' +
        items.map(function (d) {
            return '<li class="demo-row' + (d.state === 'edited' ? ' demo-row-edited' : '') +
                '" data-demo="' + esc(d.key) + '">' +
                '<b>' + esc(d.name) + '</b> <span class="key-status">' +
                esc(STATE_TEXT[d.state] || d.state) + '</span></li>';
        }).join('') + '</ul>';

    // Named, not counted. "3 examples will be replaced" is a number; "Fill in a form will be
    // replaced" is the thing the user actually has to decide about.
    html += edited.length
        ? '<p class="demo-warning" role="alert"><b>Regenerating replaces every example here with ' +
          'the version this build ships</b> — including the ' + edited.length +
          ' you have changed: ' +
          edited.map(function (d) { return esc(d.name); }).join(', ') + '. ' +
          'To keep one of those, close this and move or duplicate it into a collection of your ' +
          'own first. A copy that leaves Demos stops being an example, and is never regenerated ' +
          'again.</p>'
        : '<p class="scope-note">Nothing here has been changed, so regenerating only brings the ' +
          'examples up to date.</p>';

    body.innerHTML = html;
}

function regenerate() {
    post('regenerateDemos');
    close();
}

$('set-regen-demos').addEventListener('click', openDemosDialog);
$('demos-modal-close').addEventListener('click', close);
$('demos-regen').addEventListener('click', regenerate);
$('demos-modal').addEventListener('keydown', function (e) { trapFocus($('demos-modal'), e); });
$('demos-modal').addEventListener('mousedown', function (e) {
    if (e.target === $('demos-modal')) close();
});
document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') close();
});
