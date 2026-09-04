// The "Examples…" dialog: what the generated demo tasks are in, and what to do about the ones
// this user has edited.
//
// The whole reason this asks rather than just rebuilding is that an edited example is often
// somebody's real work — they opened the one thing that already ran, changed it, and kept going.
// An untouched example carries nothing, so it is refreshed silently and never mentioned; only an
// edited one gets a question, and every answer to that question is non-destructive except the one
// that says otherwise in as many words.

import { $, esc, post, state } from './core.js';
import { trapFocus } from './modal.js';
import { closeSettings } from './settings.js';

var STATE_TEXT = {
    missing: 'not there yet',
    current: 'up to date',
    stale: 'an older build made it — will be refreshed',
    edited: 'you have changed this one',
};

var CHOICES = [
    { value: 'keep', label: 'Keep mine', detail: 'leave it exactly as it is' },
    { value: 'clone', label: 'Keep mine + add the original', detail: 'nothing is lost' },
    { value: 'revert', label: 'Restore the original', detail: 'discards your changes' },
];

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

    var edited = (survey.items || []).filter(function (d) { return d.state === 'edited'; });
    var html = '<p class="scope-note">Pages are written to <code>' + esc(survey.root || '') +
        '</code> and rebuilt every time.</p><ul class="demo-list">' +
        (survey.items || []).map(function (d) {
            return '<li class="demo-row" data-demo="' + esc(d.key) + '">' +
                '<b>' + esc(d.name) + '</b> <span class="key-status">' +
                esc(STATE_TEXT[d.state] || d.state) + '</span>' +
                (d.state === 'edited'
                    ? '<div class="demo-choices" role="radiogroup" aria-label="What to do about ' +
                      esc(d.name) + '">' +
                      CHOICES.map(function (c, i) {
                          var id = 'demo-' + esc(d.key) + '-' + c.value;
                          return '<label class="inline" for="' + id + '">' +
                              '<input type="radio" id="' + id + '" name="demo-' + esc(d.key) + '"' +
                              ' value="' + c.value + '"' + (i === 0 ? ' checked' : '') + ' /> ' +
                              esc(c.label) + ' <span class="key-status">' + esc(c.detail) + '</span>' +
                              '</label>';
                      }).join('') + '</div>'
                    : '') +
                '</li>';
        }).join('') + '</ul>' +
        (edited.length
            ? ''
            : '<p class="scope-note">Nothing you have edited, so nothing to decide.</p>');

    body.innerHTML = html;
}

function regenerate() {
    var resolutions = {};
    document.querySelectorAll('.demo-row').forEach(function (row) {
        var key = row.getAttribute('data-demo');
        var picked = row.querySelector('input[type=radio]:checked');
        if (picked) resolutions[key] = picked.value;
    });
    post('regenerateDemos', { resolutions: resolutions });
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
