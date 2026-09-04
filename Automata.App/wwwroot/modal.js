// Every dialog in the sidebar, plus the focus discipline they share: a Tab trap so focus cannot
// walk out into the page behind the overlay, and focus restore so closing a dialog never strands
// a keyboard user at the top of the document (WCAG 2.2 SC 2.1.2 / 2.4.3).

import { $, esc, state, ui, rowByKey, ACTIONS, ACTION_INFO, FLOW_ACTIONS } from './core.js';

var modalCommit = null;
var modalMode = null;      // 'rename' | 'info' | 'confirm' | 'picker' | 'form'
var focusReturnEl = null;  // what had focus before the modal opened

// Keeps Tab inside an open dialog. Without this, tabbing walks out into the page behind the
// overlay, which is still in the DOM and still focusable (WCAG 2.2 SC 2.1.2 / 2.4.3).
function focusablesIn(root) {
    var sel = 'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), ' +
        'textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
    return Array.prototype.slice.call(root.querySelectorAll(sel)).filter(function (el) {
        return el.getClientRects().length > 0;
    });
}

export function trapFocus(root, e) {
    if (e.key !== 'Tab') return;
    var f = focusablesIn(root);
    if (!f.length) return;
    var first = f[0], last = f[f.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
}

// Puts focus back where it came from. If the trigger was a tree row button that a re-render
// has since replaced, fall back to the row itself rather than dropping focus to the document.
function restoreFocus() {
    var el = focusReturnEl;
    focusReturnEl = null;
    if (el && document.body.contains(el) && el.getClientRects().length > 0) { el.focus(); return; }
    var row = rowByKey(state.focusKey);
    if (row) row.focus();
}

// Reset to a clean base state; each open* then shows only what its mode needs.
function prepareModal(mode, title) {
    // Captured before any open* moves focus into the dialog.
    if (!focusReturnEl) focusReturnEl = document.activeElement;
    modalMode = mode;
    modalCommit = null;
    $('modal-title').textContent = title;
    $('modal-msg').classList.add('hidden');
    $('modal-input').classList.add('hidden');
    $('modal-list').classList.add('hidden');
    $('modal-form').classList.add('hidden');
    $('modal').querySelector('.modal-box').classList.remove('wide');
    $('modal-ok').classList.remove('hidden', 'danger');
    $('modal-cancel').classList.remove('hidden');
    $('modal').classList.remove('hidden');
}

export function openRenameModal(title, currentName, onCommit) {
    prepareModal('rename', title);
    var input = $('modal-input');
    input.classList.remove('hidden');
    $('modal-ok').textContent = 'Rename';
    input.value = currentName;
    modalCommit = onCommit;
    input.focus();
    input.select();
}

// Message + single OK button — used by the first-run tutorial. Dismissing any other way
// (Escape, backdrop) counts as OK so a stray click can never strand the tutorial mid-flow.
export function openInfoModal(title, message, onOk) {
    prepareModal('info', title);
    $('modal-msg').textContent = message;
    $('modal-msg').classList.remove('hidden');
    $('modal-cancel').classList.add('hidden');
    $('modal-ok').textContent = 'OK';
    modalCommit = onOk;
    $('modal-ok').focus();
}

// Reusable destructive-action gate: every delete in the app goes through this.
// Escape/backdrop CANCELS (the opposite of info mode) — destruction needs an explicit click.
export function openConfirmModal(title, message, confirmText, onConfirm) {
    prepareModal('confirm', title);
    $('modal-msg').textContent = message;
    $('modal-msg').classList.remove('hidden');
    $('modal-ok').textContent = confirmText || 'Delete';
    $('modal-ok').classList.add('danger');
    modalCommit = onConfirm;
    $('modal-cancel').focus();   // the safe choice gets the keyboard
}

// Generic list of choices; picking one commits immediately (no OK button). Focus lands on the
// first choice so the whole list is arrow/Tab reachable straight away.
export function openListPicker(title, message, items, onPick, group) {
    prepareModal('picker', title);
    $('modal-msg').textContent = message;
    $('modal-msg').classList.remove('hidden');
    $('modal-ok').classList.add('hidden');
    var list = $('modal-list');
    list.classList.remove('hidden');
    list.innerHTML = items.map(function (it) {
        return '<button class="action-pick' + (it.cls ? ' ' + it.cls : '') + '" data-value="' +
            esc(it.value) + '"><b>' + esc(it.label) + '</b>' +
            (it.detail ? '<span>' + esc(it.detail) + '</span>' : '') + '</button>';
    }).join('');
    // A secondary group renders inside a collapsed <details> AFTER the main list, so the primary
    // choices stay the whole of what a new user sees. Keeping it a sibling of the top-level
    // buttons (not a wrapper around them) is what lets "#modal-list > .action-pick" mean exactly
    // "the choices offered up front".
    if (group && group.items && group.items.length) {
        var details = document.createElement('details');
        details.className = 'pick-group';
        details.innerHTML = '<summary>' + esc(group.label) + '</summary>' +
            group.items.map(function (it) {
                return '<button class="action-pick" data-value="' + esc(it.value) + '"><b>' +
                    esc(it.label) + '</b>' + (it.detail ? '<span>' + esc(it.detail) + '</span>' : '') +
                    '</button>';
            }).join('');
        list.appendChild(details);
    }

    list.querySelectorAll('.action-pick').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var v = btn.getAttribute('data-value');
            closeModal();
            onPick(v);
        });
    });
    var first = list.querySelector('.action-pick');
    if (first) first.focus();
}

// List of step actions, plus "Record".
// onPick('__record') signals the caller to record the step live instead of hand-filling it.
export function openActionPicker(onPick) {
    var items = [{ value: '__record', label: '🔴 Record', cls: 'record-pick',
        detail: 'Perform the action live in the browser pane' }];
    ACTIONS.forEach(function (a) {
        items.push({ value: a, label: a, detail: ACTION_INFO[a] || '' });
    });
    openListPicker('New step', 'Choose the action this step performs, or record it live:',
        items, onPick, {
            label: 'Flow control',
            items: FLOW_ACTIONS.map(function (a) {
                return { value: a, label: a, detail: ACTION_INFO[a] || '' };
            }),
        });
}

// Container-only dialog: the caller renders into the returned element and wires its own
// handlers. Used by the scoped-settings editor, which commits each change as it is made — the
// same convention as the step editor — so the only button needed is Done.
//
// A form that builds something not yet on disk cannot use that convention: there is nothing to
// commit each keystroke to, and abandoning it has to leave no trace. Such a caller passes
// {okText, cancel, onCommit} and gets a real Save plus a real way to back out — Escape and the
// backdrop then mean cancel, as they already do for every non-tutorial dialog.
export function openFormModal(title, message, options) {
    var opts = options || {};
    prepareModal('form', title);
    $('modal-msg').textContent = message;
    $('modal-msg').classList.remove('hidden');
    if (opts.cancel) $('modal-cancel').classList.remove('hidden');
    else $('modal-cancel').classList.add('hidden');
    $('modal-ok').textContent = opts.okText || 'Done';
    modalCommit = opts.onCommit || null;
    $('modal').querySelector('.modal-box').classList.add('wide');
    var form = $('modal-form');
    form.classList.remove('hidden');
    form.innerHTML = '';
    return form;
}

function closeModal() {
    $('modal').classList.add('hidden');
    modalMode = null;
    modalCommit = null;
    restoreFocus();
}

function commitModal() {
    var mode = modalMode;
    var name = $('modal-input').value.trim();
    var commit = modalCommit;
    closeModal();
    if (!commit) return;
    // A commit almost always mutates the store, and the host's echo re-renders the tree out
    // from under whatever restoreFocus just focused. Arm the post-render hand-off so focus
    // lands back on the tree row instead of the top of the document.
    //
    // Only while the tree is actually on screen. A row in a hidden tab panel cannot take focus,
    // so arming the flag there would achieve nothing now and then fire on some later, unrelated
    // render — yanking focus away from whatever the user had moved on to.
    ui.pendingFocus = !$('view-build').hidden;
    if (mode === 'rename') { if (name) commit(name); }
    else commit(null);
}

function dismissModal() {
    if (modalMode === 'info') commitModal();   // tutorial popups always advance
    else closeModal();                          // rename/confirm/picker: dismissal = cancel
}

$('modal-ok').addEventListener('click', commitModal);
$('modal-cancel').addEventListener('click', closeModal);
$('modal-input').addEventListener('keydown', function (e) {
    if (e.key === 'Enter') commitModal();
    if (e.key === 'Escape') closeModal();
});
$('modal').addEventListener('keydown', function (e) { trapFocus($('modal'), e); });
document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && !$('modal').classList.contains('hidden')) dismissModal();
});
$('modal').addEventListener('mousedown', function (e) {
    if (e.target === $('modal')) dismissModal();
});
