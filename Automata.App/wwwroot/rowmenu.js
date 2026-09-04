// The dropdown behind a row's wrench.
//
// Every collection, task and step used to carry its whole strip of icon buttons inline — six of
// them on a collection row, eight on a task. At a sidebar's width that is a wall of glyphs that
// only appears on hover, competes with the row's own name for space, and gives every operation
// equal weight whether it renames something or deletes it. One wrench per row replaces the lot:
// the row reads as a row, and the operations get room for actual words.
//
// It is a menu, not a modal. A modal would darken the app and take the eye away from the row the
// menu is about — and the whole point is that these operations belong to that row.

import { esc } from './core.js';

var openEl = null;      // the menu element currently on screen
var anchorEl = null;    // the wrench it belongs to
var tooltipText = null; // the anchor's tooltip, parked while its menu is up

/// True while a menu is open — the tree uses it to keep the wrench visible after the pointer has
/// left the row.
export function rowMenuIsOpen() { return openEl !== null; }

export function closeRowMenu(returnFocus) {
    if (!openEl) return;
    document.removeEventListener('mousedown', onOutside, true);
    document.removeEventListener('scroll', onScroll, true);
    openEl.remove();
    openEl = null;
    if (anchorEl) {
        anchorEl.setAttribute('aria-expanded', 'false');
        if (tooltipText !== null) anchorEl.setAttribute('data-tooltip', tooltipText);
        var node = anchorEl.closest('.node');
        if (node) node.classList.remove('menu-open');
        if (returnFocus && document.body.contains(anchorEl)) anchorEl.focus();
    }
    anchorEl = null;
    tooltipText = null;
}

/// `items` are `{ op, glyph, label, danger }`, or the string 'separator'. `onPick` is handed the
/// chosen op — the caller owns what every op means, exactly as it did when these were buttons.
export function openRowMenu(anchor, menuLabel, items, onPick) {
    // A second wrench closes the first rather than stacking. Clicking the same wrench again is a
    // toggle, which is what a button with aria-expanded promises.
    var reopening = anchorEl === anchor;
    closeRowMenu(false);
    if (reopening) return;

    anchorEl = anchor;
    anchor.setAttribute('aria-expanded', 'true');
    var node = anchor.closest('.node');
    if (node) node.classList.add('menu-open');

    // The wrench's own hover tooltip would otherwise sit under the menu it just opened, saying
    // "Actions for this collection" beside a menu of actions for that collection. Taking the
    // attribute away dismisses the live tooltip and stops it coming back while the menu is up.
    // The dismissal has to come FIRST and has to be a bubbling mouseout: the tooltip listens on
    // the document and looks the trigger up by that same attribute, so removing it first would
    // leave a tooltip on screen that nothing can any longer recognise as one.
    tooltipText = anchor.getAttribute('data-tooltip');
    anchor.dispatchEvent(new MouseEvent('mouseout', { bubbles: true }));
    anchor.removeAttribute('data-tooltip');

    var menu = document.createElement('div');
    menu.className = 'row-menu';
    menu.setAttribute('role', 'menu');
    menu.setAttribute('aria-label', menuLabel);
    menu.innerHTML = items.map(function (item) {
        if (item === 'separator') return '<div class="row-menu-sep" role="separator"></div>';
        return '<button type="button" role="menuitem" tabindex="-1" data-op="' + esc(item.op) +
            '"' + (item.danger ? ' class="danger"' : '') + '>' +
            '<span class="row-menu-glyph" aria-hidden="true">' + item.glyph + '</span>' +
            esc(item.label) + '</button>';
    }).join('');

    document.body.appendChild(menu);
    openEl = menu;
    position(menu, anchor);

    var entries = Array.prototype.slice.call(menu.querySelectorAll('[role=menuitem]'));
    if (entries.length) entries[0].focus();

    menu.addEventListener('click', function (e) {
        var btn = e.target.closest ? e.target.closest('[role=menuitem]') : null;
        if (!btn) return;
        var op = btn.getAttribute('data-op');
        // Closed BEFORE the op runs: most of these re-render the tree or open a modal, and a menu
        // still on screen over a tree that no longer contains its anchor is a ghost.
        closeRowMenu(false);
        onPick(op);
    });

    menu.addEventListener('keydown', function (e) {
        var index = entries.indexOf(document.activeElement);
        if (e.key === 'Escape') { e.preventDefault(); closeRowMenu(true); return; }
        if (e.key === 'Tab') { closeRowMenu(true); return; }
        if (e.key === 'ArrowDown') { e.preventDefault(); focusAt(entries, index + 1); return; }
        if (e.key === 'ArrowUp') { e.preventDefault(); focusAt(entries, index - 1); return; }
        if (e.key === 'Home') { e.preventDefault(); focusAt(entries, 0); return; }
        if (e.key === 'End') { e.preventDefault(); focusAt(entries, entries.length - 1); }
    });

    // Deferred, or the click that opened the menu closes it again on its way back up.
    setTimeout(function () {
        document.addEventListener('mousedown', onOutside, true);
        document.addEventListener('scroll', onScroll, true);
    }, 0);
}

function focusAt(entries, index) {
    if (!entries.length) return;
    var n = ((index % entries.length) + entries.length) % entries.length;
    entries[n].focus();
}

function onOutside(e) {
    if (!openEl) return;
    if (openEl.contains(e.target) || (anchorEl && anchorEl.contains(e.target))) return;
    closeRowMenu(false);
}

/// A menu is positioned against a row that can scroll away underneath it. Rather than track the
/// anchor, close — a menu about a row you can no longer see is not a menu about anything.
function onScroll(e) {
    if (openEl && !openEl.contains(e.target)) closeRowMenu(false);
}

/// Below the wrench and right-aligned to it, flipped above when there is not room below and
/// nudged back inside the panel when the sidebar is narrower than the menu.
function position(menu, anchor) {
    var rect = anchor.getBoundingClientRect();
    var size = menu.getBoundingClientRect();
    var gap = 2;

    var top = rect.bottom + gap;
    if (top + size.height > window.innerHeight - 4) {
        var above = rect.top - size.height - gap;
        top = above >= 4 ? above : Math.max(4, window.innerHeight - size.height - 4);
    }

    var left = rect.right - size.width;
    if (left + size.width > window.innerWidth - 4) left = window.innerWidth - size.width - 4;
    if (left < 4) left = 4;

    menu.style.top = Math.round(top) + 'px';
    menu.style.left = Math.round(left) + 'px';
}

// Closing on Escape from anywhere, not just from inside the menu: focus can be moved out by a
// screen reader's own navigation, and a menu that can only be dismissed from within would strand.
document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && openEl) closeRowMenu(true);
});

// A window resize invalidates the position, and re-measuring on every resize frame to chase a
// menu the user is not looking at is work for nothing.
window.addEventListener('resize', function () { closeRowMenu(false); });
