// The collections/tasks/steps tree: markup, wiring, drag-and-drop, and the full keyboard model
// (ARIA tree semantics, roving tabindex, and the Alt+Arrow rearranging that gives dragging a
// non-drag equivalent).

import {
    $, treeEl, esc, newId, post, state, ui, rowByKey, announce,
    findCollection, findTask, findStep, removeStep, containsStep, insertStepRelative,
    locateStep, spliceStepsAt, nextStepIdAfterGap, saveTask, STATUS_GLYPH,
} from './core.js';
import {
    openRenameModal, openConfirmModal, openInfoModal, openListPicker, openActionPicker,
} from './modal.js';
import { render } from './render.js';
import { openScopedSettings } from './scoped-settings.js';
import { scheduleChipFor } from './schedule.js';
import { openRowMenu, closeRowMenu } from './rowmenu.js';
import { phraseFor } from './phrases.js';
import { openTaskInputs } from './task-inputs.js';

var dragCtx = null;           // {type:'step'|'task', id, taskId}

// Roving tabindex (the ARIA tree pattern): exactly one row is tabbable, so a 400-step task
// adds one Tab stop instead of four hundred. Arrow keys move focus between rows.
function tabIndexFor(key) { return state.focusKey === key ? '0' : '-1'; }

// One wrench per row, in place of the strip of six-to-eight icon buttons these rows used to
// carry. Its accessible name says WHICH row it belongs to: a screen reader moving down the tree
// would otherwise hear "Actions, Actions, Actions" all the way down.
function wrench(what, name) {
    return '<span class="node-btns"><button class="mini row-menu-btn" data-op="menu"' +
        ' aria-haspopup="menu" aria-expanded="false"' +
        ' aria-label="Actions for ' + what + ' ' + esc(name) + '"' +
        ' data-tooltip="Actions for this ' + what + '">🔧</button></span>';
}

// What each kind of row offers. Held here rather than built at click time so the menu and the
// keyboard shortcuts cannot drift apart, and so the ops a row supports are readable in one place.
var COLLECTION_MENU = [
    { op: 'run-collection', glyph: '▶', label: 'Run every task, in order' },
    { op: 'add-task', glyph: '＋', label: 'New task' },
    'separator',
    { op: 'ren-collection', glyph: '✎', label: 'Rename…' },
    { op: 'dup-collection', glyph: '⧉', label: 'Duplicate' },
    { op: 'collection-settings', glyph: '⚙', label: 'Engine settings…' },
    'separator',
    { op: 'del-collection', glyph: '🗑', label: 'Delete…', danger: true },
];

var TASK_MENU = [
    { op: 'run-task', glyph: '▶', label: 'Run this task' },
    { op: 'add-step', glyph: '＋', label: 'Add a step at the end' },
    'separator',
    { op: 'ren-task', glyph: '✎', label: 'Rename…' },
    { op: 'move-task', glyph: '⇄', label: 'Move to another collection…' },
    { op: 'dup-task', glyph: '⧉', label: 'Duplicate' },
    { op: 'task-inputs', glyph: '⌸', label: 'Inputs and outputs…' },
    { op: 'task-feature', glyph: '{ }', label: 'Read as a Gherkin feature' },
    { op: 'task-settings', glyph: '⚙', label: 'Engine settings…' },
    'separator',
    { op: 'del-task', glyph: '🗑', label: 'Delete…', danger: true },
];

/// What this particular step offers.
///
/// Built per step rather than fixed, because putting a step INSIDE another one used to be a drag
/// into the middle third of a row or Alt+Right — two gestures nobody discovers. The row that would
/// hold the step is the obvious place to ask, and an `if` is the obvious place to be offered its
/// other half.
function stepMenuFor(task, step, loc) {
    var inside =
        step.action === 'forEach' ? 'Add a step inside the loop' :
        step.action === 'if' || step.action === 'else' ? 'Add a step inside this branch' :
        'Add a step inside this one';

    var items = [
        { op: 'add-inside', glyph: '↳', label: inside },
        { op: 'ins-after', glyph: '＋', label: 'Insert a step after this one' },
    ];

    var list = siblingList(task, loc.parentId);
    var after = list[loc.index + 1];
    if (step.action === 'if' && !(after && after.action === 'else')) {
        items.push({ op: 'add-otherwise', glyph: '⋔', label: 'Add an “Otherwise”' });
    }

    // Offered only when it would actually mend something: an `otherwise` sitting after an `if` it
    // was not written for. Re-pointing it is the one-click version of what the warning describes.
    var before = list[loc.index - 1];
    if (step.action === 'else' && before && before.action === 'if' && step.pairedIfId !== before.id) {
        items.push({ op: 'repair-pair', glyph: '🔗', label: 'Belongs to the “if” above' });
    }

    items.push('separator');
    // Omitted rather than disabled when they cannot apply — a menu of greyed-out words is a menu
    // you have to read twice.
    if (loc.index > 0) items.push({ op: 'indent', glyph: '⇥', label: 'Nest inside the step above' });
    if (loc.parentId) items.push({ op: 'outdent', glyph: '⇤', label: 'Move out one level' });

    items.push('separator');
    items.push({ op: 'del-step', glyph: '🗑', label: 'Delete…', danger: true });
    return items;
}

export function renderTree() {
    // The tree is about to be rebuilt, so an open menu's anchor is about to stop existing.
    closeRowMenu(false);

    // Every row is about to be replaced, so a row that HAS focus is about to lose it. A render can
    // happen at any moment for reasons that have nothing to do with the user — the host echoes the
    // whole store back after any change, including one made somewhere else entirely — and a
    // keyboard user halfway down the tree should not be thrown to the top of the document because
    // a background save landed. Noted here, put back in ensureFocusKey.
    var hadFocus = treeEl.contains(document.activeElement);
    var html = '';
    var cCount = state.collections.length;
    state.collections.forEach(function (c, ci) {
        var open = state.expanded[c.id] !== false;
        var isSel = state.sel.collectionId === c.id && !state.sel.taskId;
        var key = 'c:' + c.id;
        html += '<div class="node collection' + (isSel ? ' selected' : '') + '" role="treeitem"' +
            ' data-key="' + key + '" data-kind="collection" data-collection="' + c.id + '"' +
            ' tabindex="' + tabIndexFor(key) + '" aria-level="1"' +
            ' aria-posinset="' + (ci + 1) + '" aria-setsize="' + cCount + '"' +
            ' aria-expanded="' + open + '" aria-selected="' + isSel + '">' +
            '<span class="twist" aria-hidden="true">' + (open ? '▾' : '▸') + '</span>' +
            '<span class="icon" aria-hidden="true">🗂️</span>' +
            '<span class="name">' + esc(c.name) + '</span>' +
            scheduleChipFor('collection', c.id) +
            wrench('collection', c.name) + '</div>';
        if (!open) return;
        var tasks = c.tasks || [];
        tasks.forEach(function (t, ti) {
            var tOpen = state.expanded[t.id] === true;
            var tSel = state.sel.taskId === t.id && !state.sel.stepId;
            var tKey = 't:' + t.id;
            html += '<div class="node task' + (tSel ? ' selected' : '') + '" role="treeitem"' +
                ' draggable="true" data-key="' + tKey + '" data-kind="task"' +
                ' data-task="' + t.id + '" data-collection="' + c.id + '"' +
                ' tabindex="' + tabIndexFor(tKey) + '" aria-level="2"' +
                ' aria-posinset="' + (ti + 1) + '" aria-setsize="' + tasks.length + '"' +
                ' aria-expanded="' + tOpen + '" aria-selected="' + tSel + '">' +
                '<span class="twist" aria-hidden="true">' + (tOpen ? '▾' : '▸') + '</span>' +
                '<span class="icon" aria-hidden="true">📋</span>' +
                '<span class="name">' + esc(t.name) + '</span>' +
                scheduleChipFor('task', t.id) +
                wrench('task', t.name) + '</div>';
            if (tOpen) html += renderSteps(t, t.steps || [], 0, null, []);
        });
    });
    treeEl.innerHTML = html || '<div class="empty">No collections yet — record something or press “+ add collection”.</div>';
    wireTree();
    ensureFocusKey(hadFocus);
}

/// The indent column for one nesting level — half-way into the step it belongs to, which is where
/// a line reads as "this row hangs off that one".
function railLeft(level) { return 27 + level * 14; }

/// The vertical guides for a row, one per ancestor level.
///
/// `rails` describes each ancestor: whether it has a later sibling, and whether it is a branch. A
/// level's line is drawn when that ancestor continues below this row — otherwise the line would run
/// past the last thing it owns — EXCEPT for the immediate parent, whose line is always drawn,
/// because that is the one that says "this row belongs to the step above". Decorative and
/// aria-hidden: nesting is already carried properly by aria-level.
function railsHtml(rails) {
    var out = '';
    for (var k = 0; k < rails.length; k++) {
        if (k !== rails.length - 1 && !rails[k].continues) continue;
        out += '<i class="rail' + (rails[k].branch ? ' rail-branch' : '') +
            '" style="left:' + railLeft(k) + 'px"></i>';
    }
    return out ? '<span class="rails" aria-hidden="true">' + out + '</span>' : '';
}

/// The quiet line that closes a branch or a loop.
///
/// Not a tree item: no role, no data-key, not focusable — exactly like an insert zone, so the ARIA
/// tree, the roving tabindex and every `.node.step` selector are untouched. It exists so a reader
/// can see where a branch STOPS, which indentation alone never says.
function branchEnd(text, depth, rails) {
    return '<div class="branch-end" aria-hidden="true" style="padding-left:' +
        (34 + depth * 14) + 'px">' + railsHtml(rails) + '<span>' + esc(text) + '</span></div>';
}

function renderSteps(task, steps, depth, parentId, rails) {
    var html = '';
    var pid = parentId || '';
    rails = rails || [];
    steps.forEach(function (s, i) {
        html += insertZone(task.id, pid, i, depth, rails);
        var status = state.stepStatus[s.id] || '';
        var sel = state.sel.stepId === s.id;
        var kids = s.children || [];
        var key = 's:' + s.id;
        var before = steps[i - 1];
        var after = steps[i + 1];
        // An `otherwise` that no longer follows the `if` it was written for. The engine refuses to
        // run it; saying so on the row means finding out before pressing Run rather than after.
        var orphaned = s.action === 'else' && !(before && before.action === 'if'
            && (!s.pairedIfId || s.pairedIfId === before.id));
        var flags =
            (orphaned ? ' <span class="flag broken" role="img" aria-label="This Otherwise does not follow its If" data-tooltip="This “Otherwise” does not follow the “if” it belongs to — the run will refuse it">⚠</span>' : '') +
            (s.isCommitPoint ? ' <span class="flag commit" role="img" aria-label="Commit point — writes permanent data" data-tooltip="Commits a permanent write (submit/save/purchase)">◆</span>' : '') +
            (s.pauseForUser ? ' <span class="flag pause" role="img" aria-label="Pauses for user" data-tooltip="Pauses for user">⏸</span>' : '');
        html += '<div class="node step' + (sel ? ' selected' : '') + (status ? ' st-' + status : '') +
            '" role="treeitem" draggable="true" data-key="' + key + '" data-kind="step"' +
            ' data-step="' + s.id + '" data-task="' + task.id + '" data-parent="' + pid + '"' +
            ' data-action="' + esc(s.action || '') + '"' +
            ' data-depth="' + depth + '"' +
            (orphaned ? ' data-orphaned="true"' : '') +
            ' tabindex="' + tabIndexFor(key) + '" aria-level="' + (3 + depth) + '"' +
            ' aria-posinset="' + (i + 1) + '" aria-setsize="' + steps.length + '"' +
            (kids.length ? ' aria-expanded="true"' : '') +
            ' aria-selected="' + sel + '"' +
            ' style="padding-left:' + (34 + depth * 14) + 'px">' +
            railsHtml(rails) +
            '<span class="status" role="img" aria-label="' + esc(status || 'not run') + '">' +
            (STATUS_GLYPH[status] || '▫') + '</span>' +
            '<span class="name">' + esc(phraseFor(s, state.collections)) + '</span>' + flags +
            wrench('step', phraseFor(s, state.collections)) + '</div>';
        var mine = rails.concat([{
            continues: i < steps.length - 1,
            branch: s.action === 'if' || s.action === 'else',
        }]);
        html += renderSteps(task, kids, depth + 1, s.id, mine);

        // A branch closes after its `otherwise`, or after the `if` when it has none; a loop closes
        // after its body. Only when there is a body to close — an empty one needs no full stop.
        if (kids.length) {
            if (s.action === 'forEach') html += branchEnd('end of loop', depth, rails);
            else if (s.action === 'else') html += branchEnd('end of branch', depth, rails);
            else if (s.action === 'if' && !(after && after.action === 'else')) {
                html += branchEnd('end of branch', depth, rails);
            }
        }
    });
    if (steps.length) html += insertZone(task.id, pid, steps.length, depth, rails);
    return html;
}

// The gap between two step rows — a constant-height strip at all times (no layout shift);
// hovering just recolors it to signal "create a new step here". Clicking opens the action
// picker and inserts the new step exactly there.
//
// Deliberately aria-hidden and unfocusable: at 10px tall it cannot meet WCAG 2.2 SC 2.5.8's
// 24x24 pointer-target minimum, and growing it would add ~14px to every step row. It relies
// on the criterion's "Equivalent" exception instead — the step's own menu, opened from a 24x24
// wrench, offers "Insert a step after this one" and is fully keyboard-operable — so this remains
// a pure mouse shortcut rather than a second, undersized focus stop per gap.
function insertZone(taskId, parentId, index, depth, rails) {
    var active = state.gapInsert && state.gapInsert.taskId === taskId &&
        (state.gapInsert.parentId || '') === parentId && state.gapInsert.index === index;
    // The gaps are full rows between the steps, so a guide drawn only on the steps would break the
    // vertical line at every one of them.
    return '<div class="insert-zone' + (active ? ' gap-active' : '') + '" aria-hidden="true"' +
        ' data-task="' + taskId + '" data-parent="' + parentId + '" data-index="' + index +
        '" style="padding-left:' + (34 + depth * 14) + 'px">' +
        railsHtml(rails || []) +
        '<span>＋ add step here</span></div>';
}
// What a row's operations DO. One definition each, called both by the row menu and by anything
// else that needs the same effect — a shortcut, a test, a future context menu — so there is never
// a version of "delete task" that skips the confirmation because it was reached another way.

function collectionOp(cid, op) {
    if (op === 'run-collection') { state.expanded[cid] = true; post('runCollection', { collectionId: cid }); return; }
    if (op === 'add-task') { post('createTask', { collectionId: cid, name: 'New task' }); return; }
    if (op === 'ren-collection') {
        var col = findCollection(cid);
        openRenameModal('Rename collection', col ? col.name : '', function (name) {
            post('renameCollection', { id: cid, name: name });
        });
        return;
    }
    if (op === 'collection-settings') { openScopedSettings('collection', { collectionId: cid }); return; }
    if (op === 'dup-collection') { post('duplicateCollection', { id: cid }); return; }
    if (op === 'del-collection') {
        var delCol = findCollection(cid);
        openConfirmModal('Delete collection',
            'Are you sure you want to delete "' + (delCol ? delCol.name : 'this collection') +
            '" and all of its tasks?', 'Delete',
            function () { post('deleteCollection', { id: cid }); });
    }
}

function taskOp(cid, tid, op) {
    if (op === 'run-task') { post('runTask', { taskId: tid, allowRepair: $('allow-repair').checked }); return; }
    if (op === 'add-step') { addStepInside(tid, null); return; }
    if (op === 'ren-task') {
        var task = findTask(tid);
        openRenameModal('Rename task', task ? task.name : '', function (name) {
            post('renameTask', { id: tid, name: name });
        });
        return;
    }
    if (op === 'move-task') { openMoveTaskModal(tid); return; }
    if (op === 'task-inputs') { openTaskInputs(tid); return; }
    if (op === 'task-feature') { post('getFeature', { taskId: tid }); return; }
    if (op === 'task-settings') { openScopedSettings('task', { collectionId: cid, taskId: tid }); return; }
    if (op === 'dup-task') { post('duplicateTask', { id: tid }); return; }
    if (op === 'del-task') {
        var delTask = findTask(tid);
        openConfirmModal('Delete task',
            'Are you sure you want to delete "' + (delTask ? delTask.name : 'this task') + '"?',
            'Delete',
            function () { post('deleteTask', { id: tid }); });
    }
}

function stepOp(tid, sid, op) {
    var task = findTask(tid);
    var here = task && locateStep(task.steps, sid, null);

    // Every one of these routes through the action picker rather than dropping in a `click` step
    // and making the user go and change it — the child's action is chosen once, in the place they
    // are already looking.
    if (op === 'add-inside') { addStepInside(tid, sid); return; }

    if (op === 'add-otherwise') {
        if (!here) return;
        // No dialog on this path, so nothing else would put focus back in the tree — the row menu
        // closes without restoring it.
        ui.pendingFocus = true;
        // createStepAt records which `if` it belongs to, because the step before it is this one.
        createStepAt(tid, here.parentId, here.index + 1, 'else');
        return;
    }

    if (op === 'repair-pair') {
        if (!task || !here) return;
        var previous = siblingList(task, here.parentId)[here.index - 1];
        var mine = findStep(task.steps, sid);
        if (!previous || !mine || previous.action !== 'if') return;
        mine.pairedIfId = previous.id;
        // Without this a keyboard user is left on the document after the menu closes: closeRowMenu
        // does not restore focus, and only the modal paths set this for themselves.
        ui.pendingFocus = true;
        saveTask(task);
        return;
    }

    if (op === 'indent' || op === 'outdent') {
        if (task) moveStep(task, sid, op);
        return;
    }

    if (op === 'del-step') {
        var task = findTask(tid);
        var step = task && findStep(task.steps, sid);
        openConfirmModal('Delete step',
            'Are you sure you want to delete "' +
            (step ? phraseFor(step, state.collections) : 'this step') + '"?',
            'Delete',
            function () {
                if (!task) return;
                removeStep(task.steps, sid);
                if (state.sel.stepId === sid) state.sel.stepId = null;
                saveTask(task);
            });
        return;
    }
    // Keyboard-operable equivalent of clicking the hover gap below this row
    // (WCAG 2.2 SC 2.5.8 "Equivalent" exception — see insertZone).
    if (op === 'ins-after') {
        var t2 = findTask(tid);
        var loc = t2 && locateStep(t2.steps, sid, null);
        if (!loc) return;
        openActionPicker(function (action) {
            if (action === '__record') beginRecordAtGap(tid, loc.parentId, loc.index + 1);
            else createStepAt(tid, loc.parentId, loc.index + 1, action);
        });
    }
}

function wireTree() {
    treeEl.querySelectorAll('.node.collection').forEach(function (el) {
        var cid = el.getAttribute('data-collection');
        el.addEventListener('click', function (e) {
            var op = e.target.getAttribute && e.target.getAttribute('data-op');
            if (op === 'menu') {
                var c = findCollection(cid);
                openRowMenu(e.target, 'Actions for collection ' + (c ? c.name : ''),
                    COLLECTION_MENU, function (picked) { collectionOp(cid, picked); });
                return;
            }
            if (op) { collectionOp(cid, op); return; }
            if (e.target.classList.contains('twist')) {
                state.expanded[cid] = state.expanded[cid] === false;
                ui.pendingFocus = true;
                render(); return;
            }
            state.sel = { collectionId: cid, taskId: null, stepId: null };
            render();
        });
        el.addEventListener('dblclick', function () { inlineRename(el, 'renameCollection', cid); });
        el.addEventListener('dragover', function (e) {
            if (dragCtx && dragCtx.type === 'task') { e.preventDefault(); el.classList.add('drop-into'); }
        });
        el.addEventListener('dragleave', function () { el.classList.remove('drop-into'); });
        el.addEventListener('drop', function (e) {
            e.preventDefault(); el.classList.remove('drop-into');
            if (dragCtx && dragCtx.type === 'task') post('moveTask', { taskId: dragCtx.id, toCollectionId: cid });
        });
    });

    treeEl.querySelectorAll('.node.task').forEach(function (el) {
        var tid = el.getAttribute('data-task'), cid = el.getAttribute('data-collection');
        el.addEventListener('click', function (e) {
            var op = e.target.getAttribute && e.target.getAttribute('data-op');
            if (op === 'menu') {
                var t = findTask(tid);
                openRowMenu(e.target, 'Actions for task ' + (t ? t.name : ''),
                    TASK_MENU, function (picked) { taskOp(cid, tid, picked); });
                return;
            }
            if (op) { taskOp(cid, tid, op); return; }
            if (e.target.classList.contains('twist')) {
                state.expanded[tid] = state.expanded[tid] !== true;
                ui.pendingFocus = true;
                render(); return;
            }
            state.sel = { collectionId: cid, taskId: tid, stepId: null };
            state.expanded[tid] = true;
            render();
        });
        el.addEventListener('dblclick', function () { inlineRename(el, 'renameTask', tid); });
        el.addEventListener('dragstart', function () { dragCtx = { type: 'task', id: tid }; });
        el.addEventListener('dragend', function () { dragCtx = null; });
    });

    treeEl.querySelectorAll('.node.step').forEach(function (el) {
        var sid = el.getAttribute('data-step'), tid = el.getAttribute('data-task');
        el.addEventListener('click', function (e) {
            var op = e.target.getAttribute && e.target.getAttribute('data-op');
            if (op === 'menu') {
                var t = findTask(tid);
                var st = t && findStep(t.steps, sid);
                var loc = t && locateStep(t.steps, sid, null);
                if (!st || !loc) return;
                openRowMenu(e.target, 'Actions for step ' + phraseFor(st, state.collections),
                    stepMenuFor(t, st, loc), function (picked) { stepOp(tid, sid, picked); });
                return;
            }
            if (op) { stepOp(tid, sid, op); return; }
            var selTask = findTask(tid);
            state.sel = { collectionId: state.sel.collectionId, taskId: tid, stepId: sid };
            if (selTask) state.expanded[tid] = true;
            render();
        });
        el.addEventListener('dragstart', function () { dragCtx = { type: 'step', id: sid, taskId: tid }; });
        el.addEventListener('dragend', function () { dragCtx = null; });
        el.addEventListener('dragover', function (e) {
            if (!dragCtx || dragCtx.type !== 'step' || dragCtx.taskId !== tid || dragCtx.id === sid) return;
            e.preventDefault();
            var r = el.getBoundingClientRect();
            var frac = (e.clientY - r.top) / r.height;
            el.classList.remove('drop-before', 'drop-after', 'drop-into');
            el.classList.add(frac < 0.33 ? 'drop-before' : frac > 0.66 ? 'drop-after' : 'drop-into');
        });
        el.addEventListener('dragleave', function () {
            el.classList.remove('drop-before', 'drop-after', 'drop-into');
        });
        el.addEventListener('drop', function (e) {
            e.preventDefault();
            var where = el.classList.contains('drop-before') ? 'before'
                : el.classList.contains('drop-into') ? 'into' : 'after';
            el.classList.remove('drop-before', 'drop-after', 'drop-into');
            if (!dragCtx || dragCtx.type !== 'step' || dragCtx.taskId !== tid || dragCtx.id === sid) return;
            var task = findTask(tid);
            if (!task) return;
            var dragged = findStep(task.steps, dragCtx.id);
            if (!dragged || containsStep(dragged, sid)) return;  // never drop into own subtree
            removeStep(task.steps, dragCtx.id);
            insertStepRelative(task, sid, dragged, where);
            saveTask(task);
        });
    });

    treeEl.querySelectorAll('.insert-zone').forEach(function (el) {
        el.addEventListener('click', function () {
            var tid = el.getAttribute('data-task');
            var pid = el.getAttribute('data-parent') || null;
            var idx = parseInt(el.getAttribute('data-index'), 10) || 0;
            openActionPicker(function (action) {
                if (action === '__record') beginRecordAtGap(tid, pid, idx);
                else createStepAt(tid, pid, idx, action);
            });
        });
    });
}
// ---- tree keyboard navigation (ARIA tree pattern) ------------------------------------------

function treeRows() {
    return Array.prototype.slice.call(treeEl.querySelectorAll('.node[data-key]'));
}
// Called after every renderTree: keeps exactly one row tabbable, re-homing the roving index when
// the previously focused row no longer exists, and re-taking DOM focus when the tree HAD it — which
// is either because a keyboard action caused this render (`ui.pendingFocus`) or because a row was
// simply focused when some unrelated update arrived (`hadFocus`). Focus that was somewhere else is
// left alone: the tree must never steal it from whatever the user is actually typing in.
function ensureFocusKey(hadFocus) {
    var rows = treeRows();
    if (!rows.length) { state.focusKey = null; ui.pendingFocus = false; return; }
    var current = rowByKey(state.focusKey);
    if (!current) {
        var selKey = state.sel.stepId ? 's:' + state.sel.stepId
            : state.sel.taskId ? 't:' + state.sel.taskId
            : state.sel.collectionId ? 'c:' + state.sel.collectionId : null;
        current = rowByKey(selKey) || rows[0];
        state.focusKey = current.getAttribute('data-key');
    }
    rows.forEach(function (r) { r.setAttribute('tabindex', r === current ? '0' : '-1'); });
    var take = ui.pendingFocus || hadFocus;
    ui.pendingFocus = false;
    if (take) current.focus();
}

function focusRow(el) {
    if (!el) return;
    treeRows().forEach(function (r) { r.setAttribute('tabindex', r === el ? '0' : '-1'); });
    state.focusKey = el.getAttribute('data-key');
    el.focus();
}

function rowLevel(el) { return parseInt(el.getAttribute('aria-level'), 10) || 1; }

function parentRowOf(rows, i) {
    var level = rowLevel(rows[i]);
    for (var j = i - 1; j >= 0; j--) if (rowLevel(rows[j]) < level) return rows[j];
    return rows[i];
}

function toggleRow(el, open) {
    var kind = el.getAttribute('data-kind');
    var id = kind === 'collection' ? el.getAttribute('data-collection')
        : kind === 'task' ? el.getAttribute('data-task') : null;
    if (!id) return;
    state.expanded[id] = open;
    ui.pendingFocus = true;
    render();
}

// ---- step rearranging without a mouse (WCAG 2.2 SC 2.5.7) ----------------------------------

function siblingList(task, parentId) {
    return parentId ? ((findStep(task.steps, parentId) || {}).children || []) : (task.steps || []);
}

function reorderStep(task, stepId, delta) {
    var loc = locateStep(task.steps, stepId, null);
    if (!loc) return false;
    var list = siblingList(task, loc.parentId);
    var to = loc.index + delta;
    if (to < 0 || to >= list.length) return false;
    list.splice(to, 0, list.splice(loc.index, 1)[0]);
    return true;
}

// Becomes the last child of the sibling directly above it — the keyboard form of the
// drag-and-drop "into" drop zone.
function indentStep(task, stepId) {
    var loc = locateStep(task.steps, stepId, null);
    if (!loc || loc.index === 0) return false;
    var list = siblingList(task, loc.parentId);
    var prev = list[loc.index - 1];
    var moved = list.splice(loc.index, 1)[0];
    prev.children = prev.children || [];
    prev.children.push(moved);
    return true;
}

// Becomes the next sibling of its own parent, one level shallower.
function outdentStep(task, stepId) {
    var loc = locateStep(task.steps, stepId, null);
    if (!loc || !loc.parentId) return false;
    var parentLoc = locateStep(task.steps, loc.parentId, null);
    if (!parentLoc) return false;
    var moved = siblingList(task, loc.parentId).splice(loc.index, 1)[0];
    siblingList(task, parentLoc.parentId).splice(parentLoc.index + 1, 0, moved);
    return true;
}

function moveWord(how) {
    return how === 'indent' ? 'into the step above'
        : how === 'outdent' ? 'out to its parent level'
        : how < 0 ? 'up' : 'down';
}

function nudgeStep(row, how) {
    var task = findTask(row.getAttribute('data-task'));
    if (task) moveStep(task, row.getAttribute('data-step'), how);
}

/// Moving a step, announced and saved. One definition, so the keyboard shortcut and the row menu
/// cannot come to mean different things.
function moveStep(task, sid, how) {
    var ok = how === 'indent' ? indentStep(task, sid)
        : how === 'outdent' ? outdentStep(task, sid)
        : reorderStep(task, sid, how);
    if (!ok) { announce('Cannot move this step ' + moveWord(how) + '.'); return; }
    var loc = locateStep(task.steps, sid, null);
    announce('Moved step ' + moveWord(how) + ' — now ' + (loc.index + 1) + ' of ' +
        siblingList(task, loc.parentId).length +
        (loc.parentId ? ' inside its parent step.' : ' at the top level.'));
    ui.pendingFocus = true;
    saveTask(task);
}

var typeAheadBuf = '', typeAheadAt = 0;

function onTreeKeydown(e) {
    var row = e.target.closest ? e.target.closest('.node[data-key]') : null;
    // Keys pressed while a row's own button has focus belong to that button, not the tree.
    if (!row || e.target !== row) return;
    var rows = treeRows();
    var i = rows.indexOf(row);
    var kind = row.getAttribute('data-kind');

    if (e.altKey && !e.ctrlKey && !e.shiftKey) {
        if (kind !== 'step') return;
        var how = e.key === 'ArrowUp' ? -1 : e.key === 'ArrowDown' ? 1
            : e.key === 'ArrowRight' ? 'indent' : e.key === 'ArrowLeft' ? 'outdent' : null;
        if (how === null) return;
        e.preventDefault();
        nudgeStep(row, how);
        return;
    }

    if (e.ctrlKey && e.shiftKey && (e.key === 'M' || e.key === 'm')) {
        if (kind !== 'task') return;
        e.preventDefault();
        openMoveTaskModal(row.getAttribute('data-task'));
        return;
    }

    switch (e.key) {
        case 'ArrowDown': e.preventDefault(); focusRow(rows[Math.min(i + 1, rows.length - 1)]); return;
        case 'ArrowUp': e.preventDefault(); focusRow(rows[Math.max(i - 1, 0)]); return;
        case 'Home': e.preventDefault(); focusRow(rows[0]); return;
        case 'End': e.preventDefault(); focusRow(rows[rows.length - 1]); return;
        case 'ArrowRight':
            e.preventDefault();
            if (row.getAttribute('aria-expanded') === 'false') toggleRow(row, true);
            else if (rows[i + 1] && rowLevel(rows[i + 1]) > rowLevel(row)) focusRow(rows[i + 1]);
            return;
        case 'ArrowLeft':
            e.preventDefault();
            // A step's substeps are always rendered, so its aria-expanded describes the tree
            // rather than a toggle — only collections and tasks actually collapse.
            if (kind !== 'step' && row.getAttribute('aria-expanded') === 'true') toggleRow(row, false);
            else focusRow(parentRowOf(rows, i));
            return;
        case 'Enter':
        case ' ':
            e.preventDefault(); row.click(); return;
        case 'F10':
            if (!e.shiftKey) return;
            e.preventDefault(); openRowActions(row); return;
        case 'ContextMenu':
            e.preventDefault(); openRowActions(row); return;
    }

    // Type-ahead: seek forward to the next row whose name starts with what was typed.
    if (e.key.length === 1 && !e.ctrlKey && !e.altKey && !e.metaKey) {
        var now = Date.now();
        typeAheadBuf = (now - typeAheadAt < 800) ? typeAheadBuf + e.key : e.key;
        typeAheadAt = now;
        var needle = typeAheadBuf.toLowerCase();
        for (var n = 1; n <= rows.length; n++) {
            var cand = rows[(i + n) % rows.length];
            var nameEl = cand.querySelector('.name');
            var nm = nameEl ? nameEl.textContent : '';
            if (nm.toLowerCase().indexOf(needle) === 0) { e.preventDefault(); focusRow(cand); return; }
        }
    }
}

treeEl.addEventListener('keydown', onTreeKeydown);

// Clicking a row focuses it (a div with tabindex takes focus), so keep the roving index in
// step with whatever the mouse just did.
treeEl.addEventListener('focusin', function (e) {
    var row = e.target.closest ? e.target.closest('.node[data-key]') : null;
    if (!row) return;
    var key = row.getAttribute('data-key');
    if (key === state.focusKey) return;
    state.focusKey = key;
    treeRows().forEach(function (r) { r.setAttribute('tabindex', r === row ? '0' : '-1'); });
});

// Shift+F10 and the Context Menu key open the row's own menu — the same one the wrench opens,
// not a parallel list built from it. When these were a strip of icon buttons this had to
// assemble a picker by reading their labels back off the DOM; now there is one menu and both
// gestures reach it, so they cannot drift apart because there is nothing to drift from.
function openRowActions(row) {
    var btn = row.querySelector('.node-btns [data-op="menu"]');
    if (btn) btn.click();
}

// Non-dragging path for "move this task to another collection", which drag-and-drop was the
// only way to do (WCAG 2.2 SC 2.5.7).
function openMoveTaskModal(taskId) {
    var task = findTask(taskId);
    if (!task) return;
    var here = null;
    state.collections.forEach(function (c) {
        if ((c.tasks || []).some(function (t) { return t.id === taskId; })) here = c.id;
    });
    var targets = state.collections
        .filter(function (c) { return c.id !== here; })
        .map(function (c) {
            return { value: c.id, label: c.name, detail: (c.tasks || []).length + ' task(s)' };
        });
    if (!targets.length) {
        openInfoModal('Move task', 'There is no other collection to move “' + task.name +
            '” into. Create another collection first.', null);
        return;
    }
    openListPicker('Move task', 'Move “' + task.name + '” into which collection?', targets,
        function (cid) { post('moveTask', { taskId: taskId, toCollectionId: cid }); });
}
function createStepAt(taskId, parentStepId, index, action) {
    var task = findTask(taskId);
    if (!task) return;
    // The label is a snapshot of the derived sentence, not an independent name — the tree derives
    // its own text every render, and this copy is what the host puts in the run log.
    var step = { id: newId(), action: action, label: '', children: [] };
    step.label = phraseFor(step, state.collections);
    if (!spliceStepsAt(task, parentStepId, index, [step])) return;
    // An `otherwise` records which `if` it is the other half of at the moment it is made, while the
    // answer is unambiguous. Adjacency alone cannot tell that apart from one that merely ended up
    // next to a different condition after a later edit.
    if (action === 'else') {
        var list = parentStepId ? (findStep(task.steps, parentStepId) || {}).children : task.steps;
        var before = (list || [])[index - 1];
        step.pairedIfId = before && before.action === 'if' ? before.id : null;
    }
    state.sel = { collectionId: state.sel.collectionId, taskId: taskId, stepId: step.id };
    state.expanded[taskId] = true;
    saveTask(task);
}

// Runs the task up to this gap (pausing right before whatever occupies it, or to completion
// if it's the last slot in the whole tree), then arms the recorder — the next physical
// action(s) become the new step(s) spliced in via onGapRecorded below.
function beginRecordAtGap(taskId, parentId, index) {
    var task = findTask(taskId);
    if (!task) return;
    var nextStepId = nextStepIdAfterGap(task, parentId, index);
    state.gapInsert = { taskId: taskId, parentId: parentId, index: index };
    render();
    post('recordAtGap', { taskId: taskId, parentStepId: parentId, index: index, nextStepId: nextStepId });
}
function inlineRename(nodeEl, action, id) {
    var nameEl = nodeEl.querySelector('.name');
    var input = document.createElement('input');
    input.type = 'text';
    input.value = nameEl.textContent;
    input.className = 'rename';
    nameEl.replaceWith(input);
    input.focus();
    input.select();
    function commit() {
        var data = { id: id, name: input.value.trim() || 'Unnamed' };
        post(action, data);
    }
    input.addEventListener('keydown', function (e) {
        if (e.key === 'Enter') commit();
        if (e.key === 'Escape') render();
    });
    input.addEventListener('blur', commit);
}

/// Adds a step at the END of a task, or of another step's children — and asks what kind.
///
/// It used to drop in a `click` step called "New step" and leave the user to go and change it in
/// the editor, which is two places to look for one decision. The action picker is the same one the
/// insert gaps use, Record included, so there is one way to answer "what should this step do".
export function addStepInside(taskId, parentStepId) {
    var task = findTask(taskId);
    if (!task) return;
    var list = parentStepId ? (findStep(task.steps, parentStepId) || {}).children : task.steps;
    if (!list) return;
    var at = list.length;
    openActionPicker(function (action) {
        if (action === '__record') beginRecordAtGap(taskId, parentStepId || null, at);
        else createStepAt(taskId, parentStepId || null, at, action);
    });
}
