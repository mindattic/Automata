// The Settings dialog (LLM provider, BYO keys, border radius) and the Help dialog. Raw API keys
// never cross the bridge - the host sends only a redacted hint, shown as the input's placeholder.

import { $, post } from './core.js';
import { trapFocus, openInfoModal } from './modal.js';
import { openScopedSettings } from './scoped-settings.js';

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

// Opened from the Settings dialog rather than nested inside it: two stacked overlays would fight
// over Escape and the focus trap.
$('set-engine-defaults').addEventListener('click', function () {
    closeSettings();
    openScopedSettings('global', {});
});

$('btn-help').addEventListener('click', function () {
    openInfoModal('Help & keyboard shortcuts', HELP_TEXT, null);
});

// Settings: LLM provider radios, per-provider BYO keys, border radius.
export const LLM_PROVIDERS = ['claude', 'openai', 'gemini', 'kimi'];

LLM_PROVIDERS.forEach(function (p) {
    $('llm-' + p).addEventListener('change', function () {
        if ($('llm-' + p).checked) post('saveSettings', { provider: p });
    });
});
$('set-key-save').addEventListener('click', function () {
    var payload = {};
    LLM_PROVIDERS.forEach(function (p) {
        var value = $('key-' + p).value.trim();
        if (value) { payload[p + 'Key'] = value; $('key-' + p).value = ''; }
    });
    if (Object.keys(payload).length) post('saveSettings', payload);
});
document.querySelectorAll('[data-clear]').forEach(function (btn) {
    btn.addEventListener('click', function () {
        post('saveSettings', { clearKey: btn.getAttribute('data-clear') });
    });
});
$('set-radius').addEventListener('input', function () {
    var radius = parseInt($('set-radius').value, 10) || 0;
    document.documentElement.style.setProperty('--radius', radius + 'px');
    $('set-radius-value').textContent = radius + 'px';
});
$('set-radius').addEventListener('change', function () {
    post('saveSettings', { borderRadius: parseInt($('set-radius').value, 10) || 0 });
});
