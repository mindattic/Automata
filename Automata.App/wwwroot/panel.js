// Sidebar logic: collections/tasks/steps tree, WYSIWYG step editor, drag-and-drop, record and
// replay controls. Single source of truth is `state`; the host pushes the authoritative tree via
// window.ssPanel.onState after every mutation, and the whole UI re-renders from it.
(function () {
    'use strict';

    var state = {
        collections: [],          // [{id, name, description, tasks:[TaskDefinition...]}]
        sel: { collectionId: null, taskId: null, stepId: null },
        recording: false,
        running: false,
        pausedStepId: null,
        stepStatus: {},           // stepId -> running|passed|failed|healed|skipped|paused
        expanded: {},             // collectionId / taskId -> bool
    };
    var dragCtx = null;           // {type:'step'|'task', id, taskId}

    var $ = function (id) { return document.getElementById(id); };
    var treeEl = $('tree'), editorEl = $('editor'), logEl = $('log');

    function post(action, data) {
        window.chrome.webview.postMessage(Object.assign({ action: action }, data || {}));
    }

    function esc(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function newId() {
        return (window.crypto && crypto.randomUUID)
            ? crypto.randomUUID().replace(/-/g, '')
            : 'id' + Date.now() + Math.floor(Math.random() * 1e6);
    }

    // ---- model lookups -------------------------------------------------------------------------

    function findCollection(id) {
        return state.collections.find(function (c) { return c.id === id; }) || null;
    }

    function findTask(taskId) {
        for (var i = 0; i < state.collections.length; i++) {
            var t = (state.collections[i].tasks || []).find(function (t) { return t.id === taskId; });
            if (t) return t;
        }
        return null;
    }

    function findStep(steps, stepId) {
        for (var i = 0; i < (steps || []).length; i++) {
            if (steps[i].id === stepId) return steps[i];
            var inChild = findStep(steps[i].children, stepId);
            if (inChild) return inChild;
        }
        return null;
    }

    function removeStep(steps, stepId) {
        for (var i = 0; i < (steps || []).length; i++) {
            if (steps[i].id === stepId) return steps.splice(i, 1)[0];
            var removed = removeStep(steps[i].children, stepId);
            if (removed) return removed;
        }
        return null;
    }

    function containsStep(step, stepId) {
        if (step.id === stepId) return true;
        return (step.children || []).some(function (c) { return containsStep(c, stepId); });
    }

    function insertStepRelative(task, targetId, step, where) {
        function walk(steps) {
            for (var i = 0; i < steps.length; i++) {
                if (steps[i].id === targetId) {
                    if (where === 'into') {
                        steps[i].children = steps[i].children || [];
                        steps[i].children.push(step);
                    } else {
                        steps.splice(where === 'before' ? i : i + 1, 0, step);
                    }
                    return true;
                }
                if (walk(steps[i].children || [])) return true;
            }
            return false;
        }
        if (!walk(task.steps)) task.steps.push(step);
    }

    function saveTask(task) { post('saveTask', { task: task }); }

    function selectedTask() { return state.sel.taskId ? findTask(state.sel.taskId) : null; }

    // ---- rendering -----------------------------------------------------------------------------

    var ACTIONS = ['navigate', 'click', 'typeText', 'setValue', 'pressEnter', 'check', 'uncheck',
        'selectRadio', 'selectOption', 'uploadFile', 'waitForElement', 'assertElement', 'extractText', 'group'];

    var ACTION_INFO = {
        navigate: 'Load a URL',
        click: 'Click an element (trusted mouse click)',
        typeText: 'Type text with real keystrokes',
        setValue: 'Set a field’s value directly',
        pressEnter: 'Press the Enter key (submit a search/form)',
        check: 'Tick a checkbox',
        uncheck: 'Untick a checkbox',
        selectRadio: 'Select a radio option',
        selectOption: 'Pick a dropdown option by its text',
        uploadFile: 'Attach a local file to a file input',
        waitForElement: 'Wait until an element appears',
        assertElement: 'Fail the run unless an element/text is present',
        extractText: 'Read an element’s text into the log',
        group: 'A container that groups substeps',
    };

    var STATUS_GLYPH = { running: '⟳', passed: '✓', healed: '✓♻', failed: '✗', skipped: '▷', paused: '⏸' };

    function render() {
        renderToolbar();
        renderTree();
        renderEditor();
    }

    function renderToolbar() {
        $('btn-record').disabled = state.recording || state.running;
        $('btn-stop').disabled = !state.recording;
        $('btn-run').disabled = !state.sel.taskId || state.running || state.recording;
        $('btn-continue').disabled = !state.pausedStepId;
        $('btn-cancel-run').disabled = !state.running;
        $('btn-export').disabled = !state.sel.taskId && !state.sel.collectionId;
    }

    function renderTree() {
        var html = '';
        state.collections.forEach(function (c) {
            var open = state.expanded[c.id] !== false;
            var selCls = state.sel.collectionId === c.id && !state.sel.taskId ? ' selected' : '';
            html += '<div class="node collection' + selCls + '" data-collection="' + c.id + '">' +
                '<span class="twist">' + (open ? '▾' : '▸') + '</span>' +
                '<span class="name">' + esc(c.name) + '</span>' +
                '<span class="node-btns">' +
                '<button class="mini" data-op="add-task" data-tooltip="New task">+task</button>' +
                '<button class="mini" data-op="ren-collection" data-tooltip="Rename collection">✎</button>' +
                '<button class="mini" data-op="dup-collection" data-tooltip="Duplicate collection">⧉</button>' +
                '<button class="mini" data-op="del-collection" data-tooltip="Delete collection">🗑</button>' +
                '</span></div>';
            if (open) {
                (c.tasks || []).forEach(function (t) {
                    var tOpen = state.expanded[t.id] === true;
                    var tSel = state.sel.taskId === t.id && !state.sel.stepId ? ' selected' : '';
                    html += '<div class="node task' + tSel + '" draggable="true" data-task="' + t.id +
                        '" data-collection="' + c.id + '">' +
                        '<span class="twist">' + (tOpen ? '▾' : '▸') + '</span>' +
                        '<span class="name">' + esc(t.name) + '</span>' +
                        '<span class="node-btns">' +
                        '<button class="mini" data-op="add-step" data-tooltip="Add step">+step</button>' +
                        '<button class="mini" data-op="ren-task" data-tooltip="Rename task">✎</button>' +
                        '<button class="mini" data-op="dup-task" data-tooltip="Duplicate task">⧉</button>' +
                        '<button class="mini" data-op="del-task" data-tooltip="Delete task">🗑</button>' +
                        '</span></div>';
                    if (tOpen) html += renderSteps(t, t.steps || [], 0);
                });
            }
        });
        treeEl.innerHTML = html || '<div class="empty">No collections yet — record something or press “+ collection”.</div>';
        wireTree();
    }

    function renderSteps(task, steps, depth, parentId) {
        var html = '';
        var pid = parentId || '';
        steps.forEach(function (s, i) {
            html += insertZone(task.id, pid, i, depth);
            var status = state.stepStatus[s.id] || '';
            var sel = state.sel.stepId === s.id ? ' selected' : '';
            var flags = (s.isCommitPoint ? ' <span class="flag commit" data-tooltip="Commits a permanent write (submit/save/purchase)">◆</span>' : '') +
                        (s.pauseForUser ? ' <span class="flag pause" data-tooltip="Pauses for user">⏸</span>' : '');
            html += '<div class="node step' + sel + (status ? ' st-' + status : '') +
                '" draggable="true" data-step="' + s.id + '" data-task="' + task.id +
                '" style="padding-left:' + (34 + depth * 14) + 'px">' +
                '<span class="status">' + (STATUS_GLYPH[status] || '') + '</span>' +
                '<span class="name">' + esc(s.label || s.action) + '</span>' + flags +
                '<span class="node-btns"><button class="mini" data-op="del-step" data-tooltip="Delete step">🗑</button></span>' +
                '</div>';
            html += renderSteps(task, s.children || [], depth + 1, s.id);
        });
        if (steps.length) html += insertZone(task.id, pid, steps.length, depth);
        return html;
    }

    // The clickable sliver between two step rows — hover reveals it, clicking opens the
    // action picker and inserts the new step exactly there.
    function insertZone(taskId, parentId, index, depth) {
        return '<div class="insert-zone" data-task="' + taskId + '" data-parent="' + parentId +
            '" data-index="' + index + '" style="padding-left:' + (34 + depth * 14) + 'px">' +
            '<span>＋ add step here</span></div>';
    }

    function wireTree() {
        treeEl.querySelectorAll('.node.collection').forEach(function (el) {
            var cid = el.getAttribute('data-collection');
            el.addEventListener('click', function (e) {
                var op = e.target.getAttribute && e.target.getAttribute('data-op');
                if (op === 'add-task') { post('createTask', { collectionId: cid, name: 'New task' }); return; }
                if (op === 'ren-collection') {
                    var col = findCollection(cid);
                    openRenameModal('Rename collection', col ? col.name : '', function (name) {
                        post('renameCollection', { id: cid, name: name });
                    });
                    return;
                }
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
                if (op === 'add-step') { addStep(tid, null); return; }
                if (op === 'ren-task') {
                    var task = findTask(tid);
                    openRenameModal('Rename task', task ? task.name : '', function (name) {
                        post('renameTask', { id: tid, name: name });
                    });
                    return;
                }
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
                if (e.target.getAttribute && e.target.getAttribute('data-op') === 'del-step') {
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
                openActionPicker(function (action) { createStepAt(tid, pid, idx, action); });
            });
        });
    }

    function createStepAt(taskId, parentStepId, index, action) {
        var task = findTask(taskId);
        if (!task) return;
        var step = { id: newId(), action: action, label: 'New ' + action + ' step', children: [] };
        var list = task.steps = task.steps || [];
        if (parentStepId) {
            var parent = findStep(task.steps, parentStepId);
            if (!parent) return;
            list = parent.children = parent.children || [];
        }
        list.splice(Math.max(0, Math.min(index, list.length)), 0, step);
        state.sel = { collectionId: state.sel.collectionId, taskId: taskId, stepId: step.id };
        state.expanded[taskId] = true;
        saveTask(task);
    }

    // ---- modal (rename / info / confirm / action-picker modes) ------------------------------

    var modalCommit = null;
    var modalMode = null;   // 'rename' | 'info' | 'confirm' | 'picker'

    // Reset to a clean base state; each open* then shows only what its mode needs.
    function prepareModal(mode, title) {
        modalMode = mode;
        modalCommit = null;
        $('modal-title').textContent = title;
        $('modal-msg').classList.add('hidden');
        $('modal-input').classList.add('hidden');
        $('modal-list').classList.add('hidden');
        $('modal-ok').classList.remove('hidden', 'danger');
        $('modal-cancel').classList.remove('hidden');
        $('modal').classList.remove('hidden');
    }

    function openRenameModal(title, currentName, onCommit) {
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
    function openInfoModal(title, message, onOk) {
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
    function openConfirmModal(title, message, confirmText, onConfirm) {
        prepareModal('confirm', title);
        $('modal-msg').textContent = message;
        $('modal-msg').classList.remove('hidden');
        $('modal-ok').textContent = confirmText || 'Delete';
        $('modal-ok').classList.add('danger');
        modalCommit = onConfirm;
        $('modal-cancel').focus();   // the safe choice gets the keyboard
    }

    // List of step actions; picking one commits immediately (no OK button).
    function openActionPicker(onPick) {
        prepareModal('picker', 'New step');
        $('modal-msg').textContent = 'Choose the action this step performs:';
        $('modal-msg').classList.remove('hidden');
        $('modal-ok').classList.add('hidden');
        var list = $('modal-list');
        list.classList.remove('hidden');
        list.innerHTML = ACTIONS.map(function (a) {
            return '<button class="action-pick" data-action="' + a + '"><b>' + a + '</b><span>' +
                esc(ACTION_INFO[a] || '') + '</span></button>';
        }).join('');
        list.querySelectorAll('.action-pick').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var action = btn.getAttribute('data-action');
                closeModal();
                onPick(action);
            });
        });
    }

    function closeModal() {
        $('modal').classList.add('hidden');
        modalMode = null;
        modalCommit = null;
    }

    function commitModal() {
        var mode = modalMode;
        var name = $('modal-input').value.trim();
        var commit = modalCommit;
        closeModal();
        if (!commit) return;
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
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && !$('modal').classList.contains('hidden')) dismissModal();
    });
    $('modal').addEventListener('mousedown', function (e) {
        if (e.target === $('modal')) dismissModal();
    });

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

    function addStep(taskId, parentStepId) {
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

    // ---- step editor ---------------------------------------------------------------------------

    var NEEDS_TARGET = { navigate: false, group: false };
    var NEEDS_VALUE = { typeText: 'Text to type', setValue: 'Value to set', selectOption: 'Option text',
        uploadFile: 'Local file path', assertElement: 'Expected text (optional)' };

    function renderEditor() {
        var task = selectedTask();
        var step = task && state.sel.stepId ? findStep(task.steps, state.sel.stepId) : null;
        if (!task || !step) {
            editorEl.classList.add('hidden');
            editorEl.innerHTML = '';
            return;
        }
        editorEl.classList.remove('hidden');

        var t = step.target || {};
        var needsTarget = NEEDS_TARGET[step.action] !== false;
        var valuePh = NEEDS_VALUE[step.action];
        var targetSummary = t.cssSelector || t.xPath || '(no target captured — fill the fields below)';

        editorEl.innerHTML =
            '<div class="section-head"><label>Step editor</label>' +
            '<span class="node-btns">' +
            '<button class="mini" id="ed-add-sub">+ substep</button>' +
            '<button class="mini" id="ed-delete">🗑 delete</button>' +
            '</span></div>' +
            '<div class="field"><span>Action</span><select id="ed-action">' +
            ACTIONS.map(function (a) {
                return '<option value="' + a + '"' + (a === step.action ? ' selected' : '') + '>' + a + '</option>';
            }).join('') + '</select></div>' +
            '<div class="field"><span>Label</span><input type="text" id="ed-label" value="' + esc(step.label) + '" /></div>' +
            (step.action === 'navigate'
                ? '<div class="field"><span>URL</span><input type="text" id="ed-url" value="' + esc(step.url) + '" /></div>' : '') +
            (valuePh
                ? '<div class="field"><span>Value</span><input type="text" id="ed-value" placeholder="' + esc(valuePh) +
                  '" value="' + esc(step.value) + '" /></div>' : '') +
            '<div class="field checks">' +
            '<label class="inline"><input type="checkbox" id="ed-pause"' + (step.pauseForUser ? ' checked' : '') + ' /> pause for user</label>' +
            '<label class="inline"><input type="checkbox" id="ed-commit"' + (step.isCommitPoint ? ' checked' : '') + ' /> commit point</label>' +
            '<span class="timeout"><span>timeout ms</span><input type="number" id="ed-timeout" value="' + (step.timeoutMs || '') + '" placeholder="10000" /></span>' +
            '</div>' +
            (needsTarget
                ? '<details class="target"><summary>Target: <code>' + esc(targetSummary) + '</code></summary>' +
                  tgtField('id', t.id) + tgtField('cssSelector', t.cssSelector) + tgtField('xPath', t.xPath) +
                  tgtField('tag', t.tag) + tgtField('nameAttr', t.nameAttr) + tgtField('typeAttr', t.typeAttr) +
                  tgtField('visibleText', t.visibleText) + tgtField('ariaRole', t.ariaRole) +
                  tgtField('ariaLabel', t.ariaLabel) + tgtField('nearbyLabelText', t.nearbyLabelText) +
                  tgtField('placeholder', t.placeholder) +
                  '<div class="field"><span>classList</span><input type="text" data-tgt="classList" value="' +
                  esc((t.classList || []).join(', ')) + '" placeholder="comma-separated" /></div>' +
                  '</details>'
                : '');

        function tgtField(key, val) {
            return '<div class="field"><span>' + key + '</span><input type="text" data-tgt="' + key +
                '" value="' + esc(val) + '" /></div>';
        }

        function commitEditor() {
            step.action = $('ed-action').value;
            step.label = $('ed-label').value;
            if ($('ed-url')) step.url = $('ed-url').value || null;
            if ($('ed-value')) step.value = $('ed-value').value || null;
            step.pauseForUser = $('ed-pause').checked;
            step.isCommitPoint = $('ed-commit').checked;
            var ms = parseInt($('ed-timeout').value, 10);
            step.timeoutMs = isNaN(ms) ? null : ms;
            var tgtInputs = editorEl.querySelectorAll('[data-tgt]');
            if (tgtInputs.length) {
                step.target = step.target || {};
                tgtInputs.forEach(function (inp) {
                    var key = inp.getAttribute('data-tgt');
                    if (key === 'classList') {
                        step.target.classList = inp.value.split(',').map(function (s) { return s.trim(); }).filter(Boolean);
                    } else {
                        step.target[key] = inp.value || null;
                    }
                });
            }
            saveTask(task);
        }

        // Commit on blur/change of any field — the host echoes state back and the UI re-renders.
        editorEl.querySelectorAll('input, select').forEach(function (inp) {
            inp.addEventListener('change', commitEditor);
        });
        $('ed-add-sub').addEventListener('click', function () { addStep(task.id, step.id); });
        $('ed-delete').addEventListener('click', function () {
            openConfirmModal('Delete step',
                'Are you sure you want to delete "' + (step.label || step.action) + '"?', 'Delete',
                function () {
                    removeStep(task.steps, step.id);
                    state.sel.stepId = null;
                    saveTask(task);
                });
        });
    }

    // ---- first-run tutorial ----------------------------------------------------------------

    // When the app opens onto an empty store, walk the user through the model by building a
    // real example in front of them: Collection → Task → Steps, one OK-gated popup per concept.
    // Evaluated only on the FIRST state push of the session, so deleting everything later
    // doesn't restart the tour mid-work.
    var tutorialStage = 0;
    var tutorialChecked = false;

    function tutorialSteps() {
        return [
            { id: newId(), action: 'navigate', label: 'Go to Google', url: 'https://www.google.com', children: [] },
            {
                id: newId(), action: 'typeText', label: "Type 'wolf tshirts' into Search", value: 'wolf tshirts',
                target: {
                    tag: 'textarea', nameAttr: 'q', ariaRole: 'combobox', ariaLabel: 'Search',
                    cssSelector: 'textarea[name="q"]', classList: [],
                }, children: [],
            },
            {
                // Pressing Enter beats clicking the search button — Google's suggestion overlay
                // makes the button unreliable, which is true of most search boxes.
                id: newId(), action: 'pressEnter', label: 'Press Enter to search',
                target: {
                    tag: 'textarea', nameAttr: 'q', ariaRole: 'combobox', ariaLabel: 'Search',
                    cssSelector: 'textarea[name="q"]', classList: [],
                }, children: [],
            },
            {
                id: newId(), action: 'waitForElement', label: 'Wait for results',
                target: { tag: 'div', id: 'search', cssSelector: '#search', classList: [] }, children: [],
            },
            {
                // The results page's "Images" tab — found by its visible link text.
                id: newId(), action: 'click', label: "Click 'Images'",
                target: { tag: 'a', visibleText: 'Images', classList: [] }, children: [],
            },
        ];
    }

    function maybeStartTutorial() {
        if (tutorialChecked) return;
        tutorialChecked = true;
        if (state.collections.length > 0) return;
        tutorialStage = 1;
        openInfoModal('Welcome to Automata',
            "A Collection is a group of Tasks. Everything you automate lives inside one. " +
            "Press OK to create your first Collection: 'Google Searches'.",
            function () { post('createCollection', { name: 'Google Searches' }); });
    }

    // Each stage waits for the host to echo the object it just created back through onState,
    // then shows the next popup — creation is visibly paused on each OK.
    function advanceTutorial() {
        if (!tutorialStage) return;

        if (tutorialStage === 1) {
            var col = state.collections.find(function (c) { return c.name === 'Google Searches'; });
            if (!col) return;
            tutorialStage = 2;
            state.sel = { collectionId: col.id, taskId: null, stepId: null };
            render();
            openInfoModal('Tasks',
                "A Task is a member of a Collection. A Task is a group of Steps that run in " +
                "order — each Step is one browser action (navigate, type, click, extract…). " +
                "Press OK to create the Task 'Wolf Tshirts'.",
                function () { post('createTask', { collectionId: col.id, name: 'Wolf Tshirts' }); });
            return;
        }

        if (tutorialStage === 2) {
            var col2 = state.collections.find(function (c) { return c.name === 'Google Searches'; });
            var task = col2 && (col2.tasks || []).find(function (t) { return t.name === 'Wolf Tshirts'; });
            if (!task) return;
            tutorialStage = 3;
            task.steps = tutorialSteps();
            state.sel = { collectionId: col2.id, taskId: task.id, stepId: null };
            state.expanded[task.id] = true;
            saveTask(task);
            return;
        }

        if (tutorialStage === 3) {
            var done = state.sel.taskId && findTask(state.sel.taskId);
            if (!done || !(done.steps || []).length) return;
            tutorialStage = 0;
            render();
            openInfoModal('Run it',
                "Click Run to execute a Task's Steps. Click any Step to edit it, hover between " +
                "steps to insert a new one, or press ● Record to capture your own.",
                null);
        }
    }

    // ---- recording preview ---------------------------------------------------------------------

    function renderRecPreview(steps) {
        var box = $('rec-preview');
        if (!state.recording && (!steps || !steps.length)) { box.classList.add('hidden'); return; }
        box.classList.remove('hidden');
        $('rec-steps').innerHTML = (steps || []).map(function (s) {
            return '<div class="node step"><span class="name">' + esc(s.label || s.action) + '</span>' +
                (s.isCommitPoint ? ' <span class="flag commit">◆</span>' : '') + '</div>';
        }).join('') || '<div class="empty">…perform actions in the browser pane…</div>';
    }

    // ---- host-facing surface -------------------------------------------------------------------

    window.ssPanel = {
        onLog: function (line) {
            var div = document.createElement('div');
            div.className = 'log-line';
            div.textContent = line;
            logEl.appendChild(div);
            logEl.scrollTop = logEl.scrollHeight;
        },
        onRunState: function (running) {
            state.running = running;
            if (!running) state.pausedStepId = null;
            if (running) state.stepStatus = {};
            $('run').disabled = running;
            $('cancel').disabled = !running;
            render();
        },
        onState: function (model) {
            state.collections = (model && model.collections) || [];
            // Drop selections that no longer exist (deleted/moved elsewhere).
            if (state.sel.taskId && !findTask(state.sel.taskId)) state.sel.taskId = state.sel.stepId = null;
            if (state.sel.collectionId && !findCollection(state.sel.collectionId)) state.sel.collectionId = null;
            render();
            maybeStartTutorial();
            advanceTutorial();
        },
        onStepEvent: function (e) {
            state.stepStatus[e.stepId] = e.status;
            if (e.status !== 'paused' && state.pausedStepId === e.stepId) state.pausedStepId = null;
            render();
        },
        onPaused: function (stepId) {
            state.pausedStepId = stepId;
            render();
        },
        onRecordingState: function (recording) {
            state.recording = recording;
            if (!recording) renderRecPreview([]);
            render();
        },
        onRecordedSteps: function (steps) {
            renderRecPreview(steps);
        },
        onSettings: function (s) {
            var radius = (s && s.borderRadius != null) ? s.borderRadius : 5;
            document.documentElement.style.setProperty('--radius', radius + 'px');
            $('set-radius').value = radius;
            $('set-radius-value').textContent = radius + 'px';

            LLM_PROVIDERS.forEach(function (p) {
                $('llm-' + p).checked = (s && s.provider) === p;
                var info = s && s.keys && s.keys[p];
                // The key never crosses the bridge — the input's placeholder shows the status.
                $('key-' + p).placeholder = info ? info.hint : '';
            });
        },
    };

    // ---- toolbar wiring ------------------------------------------------------------------------

    $('go').addEventListener('click', function () {
        var url = $('url').value.trim();
        if (url) post('navigate', { url: /^[a-z]+:\/\//i.test(url) ? url : 'https://' + url });
    });
    $('btn-record').addEventListener('click', function () { post('record'); });
    $('btn-stop').addEventListener('click', function () {
        post('stopRecord', { collectionId: state.sel.collectionId || '' });
    });
    $('btn-run').addEventListener('click', function () {
        post('runTask', { taskId: state.sel.taskId, allowRepair: $('allow-repair').checked });
    });
    $('btn-continue').addEventListener('click', function () { post('continueRun'); });
    $('btn-cancel-run').addEventListener('click', function () { post('cancelRun'); });
    $('btn-import').addEventListener('click', function () { post('import'); });
    $('btn-export').addEventListener('click', function () {
        if (state.sel.taskId) post('export', { taskId: state.sel.taskId });
        else if (state.sel.collectionId) post('export', { collectionId: state.sel.collectionId });
    });
    $('btn-new-collection').addEventListener('click', function () { post('createCollection', { name: 'New collection' }); });
    $('btn-folder').addEventListener('click', function () { post('openCollections'); });

    // Settings: LLM provider radios, per-provider BYO keys, border radius.
    var LLM_PROVIDERS = ['claude', 'openai', 'gemini', 'kimi'];

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

    // Advanced free-text LLM path (unchanged host protocol).
    $('run').addEventListener('click', function () {
        var task = $('task').value.trim();
        if (task) post('run', { task: task });
    });
    $('cancel').addEventListener('click', function () { post('cancel'); });

    post('ready');
    post('getState');
    post('getSettings');
    render();
})();
