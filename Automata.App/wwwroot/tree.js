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

var dragCtx = null;           // {type:'step'|'task', id, taskId}

// Roving tabindex (the ARIA tree pattern): exactly one row is tabbable, so a 400-step task
// adds one Tab stop instead of four hundred. Arrow keys move focus between rows.
function tabIndexFor(key) { return state.focusKey === key ? '0' : '-1'; }

// Icon-only row buttons need a real accessible name, not just a hover tooltip — the glyph
// alone tells a screen reader nothing. One string drives both.
function miniBtn(op, glyph, label) {
    return '<button class="mini" data-op="' + op + '" aria-label="' + esc(label) +
        '" data-tooltip="' + esc(label) + '">' + glyph + '</button>';
}

export function renderTree() {
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
            '<span class="node-btns">' +
            miniBtn('run-collection', '▶', 'Run every task in this collection, in order') +
            miniBtn('add-task', '+ add task', 'New task in this collection') +
            miniBtn('ren-collection', '✎', 'Rename collection') +
            miniBtn('dup-collection', '⧉', 'Duplicate collection') +
            miniBtn('collection-settings', '⚙', 'Engine settings for this collection') +
            miniBtn('del-collection', '🗑', 'Delete collection') +
            '</span></div>';
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
                '<span class="node-btns">' +
                miniBtn('run-task', '▶', "Run this task's steps") +
                miniBtn('add-step', '+ add step', 'Add a step at the end of this task') +
                miniBtn('ren-task', '✎', 'Rename task') +
                miniBtn('move-task', '⇄', 'Move task to another collection') +
                miniBtn('dup-task', '⧉', 'Duplicate task') +
                miniBtn('task-feature', '{ }', 'Read this task as a Gherkin feature') +
                miniBtn('task-settings', '⚙', 'Engine settings for this task') +
                miniBtn('del-task', '🗑', 'Delete task') +
                '</span></div>';
            if (tOpen) html += renderSteps(t, t.steps || [], 0);
        });
    });
    treeEl.innerHTML = html || '<div class="empty">No collections yet — record something or press “+ add collection”.</div>';
    wireTree();
    ensureFocusKey();
}

function renderSteps(task, steps, depth, parentId) {
    var html = '';
    var pid = parentId || '';
    steps.forEach(function (s, i) {
        html += insertZone(task.id, pid, i, depth);
        var status = state.stepStatus[s.id] || '';
        var sel = state.sel.stepId === s.id;
        var kids = s.children || [];
        var key = 's:' + s.id;
        var flags =
            (s.isCommitPoint ? ' <span class="flag commit" role="img" aria-label="Commit point — writes permanent data" data-tooltip="Commits a permanent write (submit/save/purchase)">◆</span>' : '') +
            (s.pauseForUser ? ' <span class="flag pause" role="img" aria-label="Pauses for user" data-tooltip="Pauses for user">⏸</span>' : '');
        html += '<div class="node step' + (sel ? ' selected' : '') + (status ? ' st-' + status : '') +
            '" role="treeitem" draggable="true" data-key="' + key + '" data-kind="step"' +
            ' data-step="' + s.id + '" data-task="' + task.id + '" data-parent="' + pid + '"' +
            ' tabindex="' + tabIndexFor(key) + '" aria-level="' + (3 + depth) + '"' +
            ' aria-posinset="' + (i + 1) + '" aria-setsize="' + steps.length + '"' +
            (kids.length ? ' aria-expanded="true"' : '') +
            ' aria-selected="' + sel + '"' +
            ' style="padding-left:' + (34 + depth * 14) + 'px">' +
            '<span class="status" role="img" aria-label="' + esc(status || 'not run') + '">' +
            (STATUS_GLYPH[status] || '▫') + '</span>' +
            '<span class="name">' + esc(s.label || s.action) + '</span>' + flags +
            '<span class="node-btns">' +
            miniBtn('ins-after', '＋', 'Insert a new step after this one') +
            miniBtn('del-step', '🗑', 'Delete step') +
            '</span></div>';
        html += renderSteps(task, kids, depth + 1, s.id);
    });
    if (steps.length) html += insertZone(task.id, pid, steps.length, depth);
    return html;
}

// The gap between two step rows — a constant-height strip at all times (no layout shift);
// hovering just recolors it to signal "create a new step here". Clicking opens the action
// picker and inserts the new step exactly there.
//
// Deliberately aria-hidden and unfocusable: at 10px tall it cannot meet WCAG 2.2 SC 2.5.8's
// 24x24 pointer-target minimum, and growing it would add ~14px to every step row. It relies
// on the criterion's "Equivalent" exception instead — the 24x24 ＋ button on each step row
// performs the same insertion and is fully keyboard-operable — so this remains a pure mouse
// shortcut rather than a second, undersized focus stop per gap.
function insertZone(taskId, parentId, index, depth) {
    var active = state.gapInsert && state.gapInsert.taskId === taskId &&
        (state.gapInsert.parentId || '') === parentId && state.gapInsert.index === index;
    return '<div class="insert-zone' + (active ? ' gap-active' : '') + '" aria-hidden="true"' +
        ' data-task="' + taskId + '" data-parent="' + parentId + '" data-index="' + index +
        '" style="padding-left:' + (34 + depth * 14) + 'px">' +
        '<span>＋ add step here</span></div>';
}
function wireTree() {
    treeEl.querySelectorAll('.node.collection').forEach(function (el) {
        var cid = el.getAttribute('data-collection');
        el.addEventListener('click', function (e) {
            var op = e.target.getAttribute && e.target.getAttribute('data-op');
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
                return;
            }
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
            if (op === 'run-task') { post('runTask', { taskId: tid, allowRepair: $('allow-repair').checked }); return; }
            if (op === 'add-step') { addStep(tid, null); return; }
            if (op === 'ren-task') {
                var task = findTask(tid);
                openRenameModal('Rename task', task ? task.name : '', function (name) {
                    post('renameTask', { id: tid, name: name });
                });
                return;
            }
            if (op === 'move-task') { openMoveTaskModal(tid); return; }
            if (op === 'task-feature') { post('getFeature', { taskId: tid }); return; }
            if (op === 'task-settings') { openScopedSettings('task', { collectionId: cid, taskId: tid }); return; }
            if (op === 'dup-task') { post('duplicateTask', { id: tid }); return; }
            if (op === 'del-task') {
                var delTask = findTask(tid);
                openConfirmModal('Delete task',
                    'Are you sure you want to delete "' + (delTask ? delTask.name : 'this task') + '"?',
                    'Delete',
                    function () { post('deleteTask', { id: tid }); });
                return;
            }
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
            if (op === 'del-step') {
                var task = findTask(tid);
                var step = task && findStep(task.steps, sid);
                openConfirmModal('Delete step',
                    'Are you sure you want to delete "' + (step ? (step.label || step.action) : 'this step') + '"?',
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
                return;
            }
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
// Called after every renderTree: keeps exactly one row tabbable, re-homing the roving index
// when the previously focused row no longer exists, and re-taking DOM focus when the render
// was caused by a keyboard action (otherwise focus would drop to the top of the document).
function ensureFocusKey() {
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
    if (ui.pendingFocus) { ui.pendingFocus = false; current.focus(); }
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
    var sid = row.getAttribute('data-step');
    if (!task) return;
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

// Keyboard/AT equivalent of the hover-revealed row buttons: offer exactly the actions this
// row already has, read straight off its own buttons so the two can never drift apart.
function openRowActions(row) {
    var btns = Array.prototype.slice.call(row.querySelectorAll('.node-btns .mini'));
    if (!btns.length) return;
    var nameEl = row.querySelector('.name');
    openListPicker('Row actions', 'Actions for “' + (nameEl ? nameEl.textContent : 'this row') + '”:',
        btns.map(function (b, ix) {
            return { value: String(ix), label: b.getAttribute('aria-label') || b.textContent };
        }),
        function (v) { btns[parseInt(v, 10)].click(); });
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
    var step = { id: newId(), action: action, label: 'New ' + action + ' step', children: [] };
    if (!spliceStepsAt(task, parentStepId, index, [step])) return;
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

export function addStep(taskId, parentStepId) {
    var task = findTask(taskId);
    if (!task) return;
    var step = { id: newId(), action: 'click', label: 'New step', children: [] };
    if (parentStepId) {
        var parent = findStep(task.steps, parentStepId);
        if (!parent) return;
        parent.children = parent.children || [];
        parent.children.push(step);
    } else {
        task.steps = task.steps || [];
        task.steps.push(step);
    }
    state.sel = { collectionId: state.sel.collectionId, taskId: taskId, stepId: step.id };
    state.expanded[taskId] = true;
    saveTask(task);
}
