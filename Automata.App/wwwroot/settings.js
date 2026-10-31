// The Settings dialog and the Help dialog. App-level settings (LLM provider and keys, theme, border
// radius, global engine defaults) are edited in MindAttic Vault (LAMP, AUT-T27): its button asks the host
// to open Automata's tab there. Keys never cross the bridge.

import { $, post } from './core.js';
import { trapFocus, openInfoModal } from './modal.js';

// Settings and Help dialogs: same open/close/focus discipline as the generic modal.
var settingsReturnEl = null;

function openSettings() {
    settingsReturnEl = document.activeElement;
    $('settings-modal').classList.remove('hidden');
    $('settings-modal-close').focus();
}

export function closeSettings() {
    if ($('settings-modal').classList.contains('hidden')) return;
    $('settings-modal').classList.add('hidden');
    if (settingsReturnEl && document.body.contains(settingsReturnEl)) settingsReturnEl.focus();
    settingsReturnEl = null;
}

$('btn-settings').addEventListener('click', openSettings);
$('settings-modal-close').addEventListener('click', closeSettings);
$('settings-modal').addEventListener('keydown', function (e) { trapFocus($('settings-modal'), e); });
document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') closeSettings();
});
$('settings-modal').addEventListener('mousedown', function (e) {
    if (e.target === $('settings-modal')) closeSettings();
});

// A single, always-present help entry point in the same place on every view — which is all
// WCAG 2.2 SC 3.2.6 (Consistent Help) asks for.
var HELP_TEXT = [
    'TREE — MOVING AROUND',
    '  Up / Down          previous / next row',
    '  Right / Left       expand / collapse, or move in / out',
    '  Home / End         first / last row',
    '  Enter or Space     select the row',
    '  Shift+F10          row actions menu',
    '  any letter         jump to the next row starting with it',
    '',
    'REARRANGING — NO DRAGGING NEEDED',
    '  Alt+Up / Alt+Down  move a step up / down',
    '  Alt+Right          nest a step under the one above it',
    '  Alt+Left           un-nest a step out to its parent level',
    '  Ctrl+Shift+M       move a task to another collection',
    '',
    'DIALOGS',
    '  Escape             close',
    '  Tab                cycles inside the dialog only',
    '',
    'MOUSE SHORTCUTS',
    '  Hover the gap between two steps to insert one there.',
    '  Drag rows to reorder steps, or drop a task on another collection.',
].join('\n');

$('open-vault').addEventListener('click', function () {
    closeSettings();
    post('openVault');
});

$('btn-help').addEventListener('click', function () {
    openInfoModal('Help & keyboard shortcuts', HELP_TEXT, null);
});

$('set-detach').addEventListener('click', function () {
    // The host owns the window, so it owns the answer: the button's own label is rewritten when
    // the settings come back, rather than guessed at here.
    post('togglePanelDetached');
});
