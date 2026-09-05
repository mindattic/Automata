// Editors for the control-flow steps: if, forEach, runTask, writeDataset, and a wait on a
// condition.
//
// Every source is still SELECTED — datasets and tasks come from lists the host pushed, and a
// condition's operands go through the same binding picker as any other value. The only free text
// is a literal you were always going to type anyway.

import { $, esc, state, post, OPS, UNARY, describeBinding, LIVE_WAIT_OUTPUT, waitWatches } from './core.js';
import { openBindingPicker } from './binding-field.js';


/// Options for a select built from a fixed list, ALWAYS including whatever the step currently holds.
///
/// A select that cannot represent its own value does not fail — the browser reports the first
/// option instead, and the next commit writes that back over the real one. Every field in this
/// editor commits on `change`, so merely opening a step and touching something unrelated is enough.
/// That is exactly how a guard using `exists` turned into `is exactly`: the operator was added to
/// the engine and the Gherkin vocabulary but not to OPS, so the select had nothing to show for it.
///
/// `datasetOptions` below already had this guard for dataset names. This is the same rule for every
/// other fixed list, so the failure cannot come back through a different select.
function optionsFor(choices, selected, keptNote) {
    var value = selected == null ? '' : String(selected);
    var list = choices.some(function (c) { return String(c.value) === value; }) || value === ''
        ? choices
        : choices.concat([{ value: value, label: value + (keptNote || ' — kept as it is') }]);
    return list.map(function (c) {
        return '<option value="' + esc(String(c.value)) + '"' +
            (String(c.value) === value ? ' selected' : '') + '>' + esc(c.label) + '</option>';
    }).join('');
}

export { optionsFor };

function isBound(ref) {
    return !!ref && ref.kind && ref.kind !== 'literal';
}

/// One operand of a condition: a chip when it is bound, a plain input when it is a literal.
function operandHtml(slot, label, ref) {
    if (isBound(ref)) {
        return '<span class="chip bound" role="button" tabindex="0" data-operand="' + slot + '"' +
            ' aria-label="' + esc(label) + ' is bound to ' + esc(describeBinding(ref)) +
            '. Activate to change it." data-tooltip="Bound — activate to change">' +
            '🔗 ' + esc(describeBinding(ref)) + '</span>';
    }
    return '<input type="text" class="operand" data-operand-literal="' + slot + '"' +
        ' aria-label="' + esc(label) + '" placeholder="a fixed value" value="' +
        esc(ref ? ref.literal : '') + '" />' +
        '<button type="button" class="mini binding-toggle" data-operand="' + slot + '"' +
        ' aria-label="Bind ' + esc(label) + ' to a captured value" data-tooltip="Use a captured value">🔗</button>';
}

function conditionHtml(condition) {
    var c = condition || {};
    var op = c.op || 'notEmpty';
    return '<div class="field"><span>When</span>' +
        '<div class="condition-row">' +
        operandHtml('left', 'The value to test', c.left) +
        '<select id="ed-cond-op" aria-label="Comparison">' + optionsFor(OPS, op) + '</select>' +
        (UNARY.indexOf(op) >= 0 ? '' : operandHtml('right', 'The value to compare against', c.right)) +
        '</div></div>';
}

function datasetOptions(selected) {
    var names = (state.datasets || []).map(function (d) { return d.name; });
    if (selected && names.indexOf(selected) < 0) names = names.concat([selected]);
    if (!names.length) return '<option value="">(no datasets yet — add a file to the Data tab)</option>';
    return names.map(function (n) {
        return '<option value="' + esc(n) + '"' + (n === selected ? ' selected' : '') + '>' + esc(n) + '</option>';
    }).join('');
}

/// The task a runTask step names, wherever it lives — the step's fields describe the CALLEE.
function findTaskById(id) {
    var found = null;
    (state.collections || []).forEach(function (c) {
        (c.tasks || []).forEach(function (t) { if (t.id === id) found = t; });
    });
    return found;
}

function taskOptions(currentTaskId, selected) {
    var options = ['<option value="">(choose a task)</option>'];
    var found = false;
    (state.collections || []).forEach(function (c) {
        (c.tasks || []).forEach(function (t) {
            // A task calling itself is caught at run time too, but not offering it is kinder.
            if (t.id === currentTaskId) return;
            if (t.id === selected) found = true;
            options.push('<option value="' + esc(t.id) + '"' + (t.id === selected ? ' selected' : '') + '>' +
                esc(c.name + ' / ' + t.name) + '</option>');
        });
    });
    // A reference to a task that is not in this list — deleted, or not loaded — is kept rather than
    // quietly nulled by the select reporting "(choose a task)" on the next commit. The run says
    // plainly that the task is missing; the editor must not erase the evidence first.
    if (selected && !found) {
        options.push('<option value="' + esc(selected) + '" selected>' +
            esc(selected) + ' — no longer in this workspace</option>');
    }
    return options.join('');
}

/// The action-specific fields, or '' for a step that has none.
export function flowFieldsHtml(step, task) {
    switch (step.action) {
        case 'if':
            return conditionHtml(step.condition);

        case 'forEach': {
            var spec = step.forEach || {};
            var source = spec.source || {};
            return '<div class="field"><span>For each row in</span>' +
                '<select id="ed-foreach-dataset" aria-label="Dataset to iterate">' +
                datasetOptions(source.datasetName) + '</select></div>' +
                '<div class="field"><span>Row named</span>' +
                '<input type="text" id="ed-foreach-var" aria-label="Name the substeps use for the current row"' +
                ' placeholder="row" value="' + esc(spec.rowVariableName || '') + '" /></div>';
        }

        case 'runTask': {
            // Only the called task's OWN inputs are offered, read from the task it names. A step
            // that could pass anything to anything would be a function call with no signature.
            var called = findTaskById(step.runTaskId);
            var declared = (called && called.inputs || []).filter(function (i) { return i.name; });
            return '<div class="field"><span>Run task</span>' +
                '<select id="ed-runtask" aria-label="Task to run from here">' +
                taskOptions(task.id, step.runTaskId) + '</select></div>' +
                // The rule used to be invisible: a called task starts on whatever page the caller
                // left open, and the only place that was written down was one example's
                // description. Now the step says which it is, either way round.
                '<div class="field"><span>Starts on</span>' +
                '<label class="inline"><input type="checkbox" id="ed-runtask-starturl"' +
                (step.runTaskOpensStartUrl ? ' checked' : '') +
                ' /> open that task’s own start page first</label>' +
                '<span class="scope-note">' +
                (step.runTaskOpensStartUrl
                    ? 'It will navigate before running, so it does not matter where this task left off.'
                    : 'Otherwise it starts on whatever page this task has open.') +
                '</span></div>' +
                (declared.length
                    ? '<div class="field"><span>With</span><div class="column-list">' +
                      declared.map(function (i) {
                          return '<div class="column-row" data-input="' + esc(i.name) + '">' +
                              '<span class="input-name">' + esc(i.name) + '</span>' +
                              operandHtml('in:' + i.name, 'Value for input ' + i.name,
                                  (step.runTaskInputs || {})[i.name]) +
                              '</div>';
                      }).join('') +
                      '<p class="scope-note">Anything left blank uses that input’s default.</p>' +
                      '</div></div>'
                    : '');
        }

        case 'writeDataset': {
            var w = step.writeDataset || {};
            var columns = w.columns || {};
            var names = Object.keys(columns);
            return '<div class="field"><span>Write to</span>' +
                '<input type="text" id="ed-write-name" aria-label="Dataset file to write"' +
                ' placeholder="results.csv" value="' + esc(w.datasetName || '') + '" />' +
                '<label class="inline"><input type="checkbox" id="ed-write-append"' +
                (w.append === false ? '' : ' checked') + ' /> append</label>' +
                // Only meaningful alongside append: without it the write replaces every time
                // anyway, so offering "start fresh" would be offering the same thing twice.
                '<label class="inline"' + (w.append === false ? ' hidden' : '') +
                ' id="ed-write-reset-row"><input type="checkbox" id="ed-write-reset"' +
                (w.resetOnFirstWrite ? ' checked' : '') +
                ' /> start fresh each run</label></div>' +
                '<div class="field"><span>Columns</span><div class="column-list">' +
                (names.length
                    ? names.map(function (n) {
                        return '<div class="column-row" data-column="' + esc(n) + '">' +
                            '<input type="text" class="column-name" aria-label="Column name" value="' + esc(n) + '" />' +
                            operandHtml('col:' + n, 'Value for column ' + n, columns[n]) +
                            '<button type="button" class="mini" data-drop-column="' + esc(n) +
                            '" aria-label="Remove column ' + esc(n) + '">✕</button>' +
                            '</div>';
                    }).join('')
                    : '<p class="scope-note">No columns yet — every run would write a blank row.</p>') +
                '<button type="button" class="mini" id="ed-add-column">+ column</button>' +
                '</div></div>';
        }

        case 'extractAll':
            return harvestHtml(step);

        case 'else':
            // No fields: an else has no condition of its own, it has the one above it. What it
            // does need is to say so, because a step with an empty editor reads as unfinished.
            return '<p class="scope-note">Runs when the <b>if</b> directly above this step did ' +
                'not. Move it away from that step and it has nothing to be the other half of — ' +
                'the run says so rather than guessing.</p>';

        case 'aggregate': {
            var agg = step.aggregate || {};
            // The answer always lands under one name, so there is nothing to type and nothing for
            // a binding to get wrong — the same shape as a harvest's "count".
            return '<div class="field"><span>Work out</span>' +
                '<select id="ed-agg-op" aria-label="What to work out">' +
                optionsFor(AGGREGATE_OPS, agg.op || 'sum') + '</select></div>' +
                '<div class="field"><span>Of column</span>' +
                '<input type="text" id="ed-agg-column" aria-label="Dataset column to work out"' +
                ' placeholder="price" value="' + esc(agg.columnName || '') + '" /></div>' +
                '<div class="field"><span>In dataset</span>' +
                '<select id="ed-agg-dataset" aria-label="Dataset to read">' +
                datasetOptions(agg.datasetName) + '</select>' +
                '<p class="scope-note">Publishes its answer as <code>value</code>, for a later ' +
                'step to bind to.</p></div>';
        }

        case 'setZoom': {
            var pct = step.zoomPercent || 100;
            // The browser's own levels, offered rather than typed. A free number box would invite
            // 6 for 60 and a page nobody can automate, and there is no zoom between these that
            // anybody actually wants.
            return '<div class="field"><span>Zoom to</span>' +
                '<select id="ed-zoom" aria-label="Zoom level for the page">' +
                optionsFor(ZOOM_LEVELS.map(function (z) {
                    return { value: z, label: z + '%' + (z === 100 ? ' (normal)' : '') };
                }), pct, '% — kept as it is') + '</select>' +
                '<p class="scope-note">Stays until another step changes it, and is re-applied ' +
                'after a navigation.</p></div>';
        }

        default:
            return '';
    }
}

/// The five reductions an aggregate step offers. Five, and no more: this is the one place
/// arithmetic enters the step model, and it enters as a closed list rather than a formula.
var AGGREGATE_OPS = [
    { value: 'sum', label: 'the total' },
    { value: 'count', label: 'how many' },
    { value: 'min', label: 'the smallest' },
    { value: 'max', label: 'the largest' },
    { value: 'average', label: 'the average' },
];

/// The levels a browser's own zoom menu offers, and the range the engine accepts.
var ZOOM_LEVELS = [25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500];

var SOURCES = [
    { value: 'text', label: 'its text' },
    { value: 'href', label: 'where its link goes' },
    { value: 'attribute', label: 'one of its attributes' },
];

/// The harvest editor. Nothing here is a selector anyone typed: the rows and every column come
/// from clicking an example in the page, and what the click resolved to is shown back as a count
/// so it can be believed before it is saved.
function harvestHtml(step) {
    var h = step.harvest || {};
    var fields = h.fields || [];
    var picked = !!h.itemSelector;

    var rows = picked
        ? '<p class="scope-note">Matching <b>' + (h.expectedCount || '?') + '</b> item(s) on the page ' +
          'the harvest was built against.<br /><code>' + esc(h.itemSelector) + '</code></p>'
        : '<p class="scope-note">Nothing picked yet. Open the list or results page in the browser ' +
          'pane, then pick one item — everything like it becomes the harvest.</p>';

    return '<div class="field"><span>Rows</span><div class="harvest-rows">' + rows +
        '<button type="button" class="mini" id="ed-harvest-pick-row">' +
        (picked ? 'pick a different item' : 'pick an item in the page') + '</button></div></div>' +

        '<div class="field"><span>Columns</span><div class="column-list">' +
        (fields.length
            ? fields.map(function (f, i) {
                return '<div class="column-row" data-harvest-field="' + i + '">' +
                    '<input type="text" class="column-name" aria-label="Column name" value="' +
                    esc(f.name || '') + '" />' +
                    '<select class="harvest-source" aria-label="What to read for column ' +
                    esc(f.name || '') + '">' +
                    SOURCES.map(function (o) {
                        return '<option value="' + o.value + '"' +
                            (f.source === o.value ? ' selected' : '') + '>' + esc(o.label) + '</option>';
                    }).join('') + '</select>' +
                    (f.source === 'attribute'
                        ? '<input type="text" class="harvest-attr" aria-label="Attribute name for column ' +
                          esc(f.name || '') + '" placeholder="data-id" value="' + esc(f.attributeName || '') + '" />'
                        : '') +
                    '<span class="scope-note harvest-where">' +
                    (f.selector ? esc(f.selector) : 'the item itself') + '</span>' +
                    '<button type="button" class="mini" data-harvest-repick="' + i +
                    '" aria-label="Pick the element for column ' + esc(f.name || '') + ' again">◎</button>' +
                    '<button type="button" class="mini" data-drop-harvest-field="' + i +
                    '" aria-label="Remove column ' + esc(f.name || '') + '">✕</button>' +
                    '</div>';
            }).join('')
            : '<p class="scope-note">No columns yet — a harvest with no columns would write empty rows.</p>') +
        '<button type="button" class="mini" id="ed-harvest-add-field"' + (picked ? '' : ' disabled') +
        '>+ pick a column in the page</button>' +
        '</div></div>' +

        '<div class="field"><span>Write to</span>' +
        '<input type="text" id="ed-harvest-name" aria-label="Dataset file to write"' +
        ' placeholder="products.csv" value="' + esc(h.datasetName || '') + '" />' +
        '<label class="inline"><input type="checkbox" id="ed-harvest-append"' +
        (h.append ? ' checked' : '') + ' /> add to what is already there</label></div>' +

        '<div class="field"><span>No duplicates by</span>' +
        '<select id="ed-harvest-dedupe" aria-label="Column to de-duplicate rows by">' +
        '<option value=""' + (h.dedupeBy ? '' : ' selected') + '>keep every row</option>' +
        fields.map(function (f) {
            return '<option value="' + esc(f.name) + '"' +
                (h.dedupeBy === f.name ? ' selected' : '') + '>' + esc(f.name) + '</option>';
        }).join('') + '</select></div>';
}

export { LIVE_WAIT_OUTPUT, waitWatches };

/// True when this step's wait needs a condition editor rather than a duration or a clock time.
export function waitNeedsCondition(step) {
    return step.action === 'wait' && step.wait && step.wait.mode === 'untilCondition';
}

export function waitConditionHtml(step) {
    return conditionHtml((step.wait || {}).condition);
}

/// Reads the flow fields back onto the step. Returns nothing; mutates in place, like the rest of
/// the editor.
export function commitFlowFields(step, root) {
    root = root || document;
    if (step.action === 'if') {
        step.condition = readCondition(step.condition);
    } else if (waitNeedsCondition(step)) {
        step.wait = Object.assign({}, step.wait, { condition: readCondition((step.wait || {}).condition) });
    } else if (step.action === 'forEach') {
        var name = ($('ed-foreach-dataset') || {}).value || '';
        step.forEach = Object.assign({}, step.forEach, {
            source: { kind: 'datasetRow', datasetName: name },
            rowVariableName: (($('ed-foreach-var') || {}).value || '').trim() || 'row',
        });
    } else if (step.action === 'runTask') {
        step.runTaskId = (($('ed-runtask') || {}).value || '') || null;
        step.runTaskOpensStartUrl = !!($('ed-runtask-starturl') || {}).checked;
        var passed = {};
        root.querySelectorAll('[data-input]').forEach(function (row) {
            var name = row.getAttribute('data-input');
            var literal = row.querySelector('[data-operand-literal]');
            var existing = (step.runTaskInputs || {})[name];
            if (literal) {
                // A blank literal means "use the default", so it is left out rather than passed
                // as an empty string — those are different answers.
                if (literal.value) passed[name] = { kind: 'literal', literal: literal.value };
            } else if (existing) {
                passed[name] = existing;
            }
        });
        step.runTaskInputs = Object.keys(passed).length ? passed : null;
    } else if (step.action === 'writeDataset') {
        var spec = Object.assign({ format: 'csv' }, step.writeDataset);
        spec.datasetName = (($('ed-write-name') || {}).value || '').trim();
        spec.append = ($('ed-write-append') || {}).checked !== false;
        spec.resetOnFirstWrite = spec.append && ($('ed-write-reset') || {}).checked === true;
        spec.columns = readColumns(spec.columns || {});
        step.writeDataset = spec;
    } else if (step.action === 'aggregate') {
        step.aggregate = {
            datasetName: (($('ed-agg-dataset') || {}).value || ''),
            columnName: (($('ed-agg-column') || {}).value || '').trim(),
            op: (($('ed-agg-op') || {}).value || 'sum'),
        };
        // Declared here rather than left to the user, so the binding picker can offer it the
        // moment the step exists. One step, one answer, one name.
        step.outputs = [{ name: 'value', type: 'string' }];
    } else if (step.action === 'setZoom') {
        step.zoomPercent = parseInt((($('ed-zoom') || {}).value || '100'), 10) || 100;
    } else if (step.action === 'extractAll') {
        var h = Object.assign({ format: 'csv' }, step.harvest);
        h.datasetName = (($('ed-harvest-name') || {}).value || '').trim();
        h.append = ($('ed-harvest-append') || {}).checked === true;
        h.fields = readHarvestFields(h.fields || []);
        var dedupe = (($('ed-harvest-dedupe') || {}).value || '').trim();
        // A de-duplication column that no longer exists would be refused by the host on the way to
        // storage, so it is dropped here rather than saved and then rejected.
        h.dedupeBy = h.fields.some(function (f) { return f.name === dedupe; }) ? dedupe : null;
        step.harvest = h;
    }
}

/// Field rows keep their POSITION, so renaming one keeps the selector that was picked for it.
function readHarvestFields(existing) {
    var next = [];
    document.querySelectorAll('[data-harvest-field]').forEach(function (row) {
        var i = Number(row.getAttribute('data-harvest-field'));
        var was = existing[i] || {};
        var name = ((row.querySelector('.column-name') || {}).value || '').trim();
        if (!name) return;
        var source = (row.querySelector('.harvest-source') || {}).value || 'text';
        var attr = ((row.querySelector('.harvest-attr') || {}).value || '').trim();
        next.push({
            name: name,
            selector: was.selector || null,
            source: source,
            attributeName: source === 'attribute' ? attr : null,
        });
    });
    return next;
}

function readCondition(existing) {
    var condition = Object.assign({ op: 'notEmpty' }, existing);
    var opEl = $('ed-cond-op');
    if (opEl) condition.op = opEl.value;
    condition.left = readOperand('left', condition.left);
    condition.right = UNARY.indexOf(condition.op) >= 0 ? null : readOperand('right', condition.right);
    return condition;
}

function readOperand(slot, existing) {
    var input = document.querySelector('[data-operand-literal="' + slot + '"]');
    if (input) return { kind: 'literal', literal: input.value };
    return existing || { kind: 'literal', literal: '' };
}

/// Column rows are keyed by their ORIGINAL name so renaming one keeps its binding.
function readColumns(existing) {
    var next = {};
    document.querySelectorAll('.column-row').forEach(function (row) {
        var original = row.getAttribute('data-column');
        var name = (row.querySelector('.column-name') || {}).value || '';
        name = name.trim();
        if (!name) return;
        var literal = row.querySelector('[data-operand-literal]');
        next[name] = literal
            ? { kind: 'literal', literal: literal.value }
            : existing[original] || { kind: 'literal', literal: '' };
    });
    return next;
}

/// Wires the operand chips and toggles, the column add/remove buttons. `onChange` re-renders and
/// saves, exactly as the rest of the editor does.
export function wireFlowFields(root, task, step, onChange) {
    root.querySelectorAll('[data-operand]').forEach(function (el) {
        function open() {
            var slot = el.getAttribute('data-operand');
            openBindingPicker(task, step, slot === 'left' ? 'the value to test' : 'this value',
                currentOperand(step, slot), function (binding) {
                    setOperand(step, slot, binding || { kind: 'literal', literal: '' });
                    onChange();
                });
        }
        el.addEventListener('click', open);
        el.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); }
        });
    });

    var add = $('ed-add-column');
    if (add) {
        add.addEventListener('click', function () {
            var spec = Object.assign({ format: 'csv', append: true, columns: {} }, step.writeDataset);
            spec.columns = Object.assign({}, spec.columns);
            var n = 1;
            while (spec.columns['column' + n] !== undefined) n++;
            spec.columns['column' + n] = { kind: 'literal', literal: '' };
            step.writeDataset = spec;
            onChange();
        });
    }

    root.querySelectorAll('[data-drop-column]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var spec = Object.assign({}, step.writeDataset);
            spec.columns = Object.assign({}, spec.columns);
            delete spec.columns[btn.getAttribute('data-drop-column')];
            step.writeDataset = spec;
            onChange();
        });
    });

    // "Start fresh each run" only means anything alongside append, so it appears and disappears
    // with it rather than sitting there greyed out saying nothing.
    var append = $('ed-write-append');
    var resetRow = $('ed-write-reset-row');
    if (append && resetRow) {
        append.addEventListener('change', function () {
            resetRow.hidden = !append.checked;
            if (!append.checked) $('ed-write-reset').checked = false;
        });
    }

    wireHarvestFields(root, task, step, onChange);
}

/// Escape gets out of an armed pick. Arming one puts the TARGET pane into a one-shot listening
/// state, and until this there was no way back out of it: the host had a cancel handler waiting
/// and nothing in the panel ever sent to it, so changing your mind meant picking something you did
/// not want. Registered once, at load, rather than per editor render.
document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape' || !state.harvestPick) return;
    state.harvestPick = null;
    post('cancelHarvestPick');
    window.ssPanel.onLog('Picking cancelled.');
});

/// A pick is a round trip through the target pane: the panel arms it, the user clicks in the page,
/// and the answer comes back through onHarvestPick. `state.harvestPick` remembers which step and
/// which column the answer belongs to, because by then this editor has been re-rendered.
function wireHarvestFields(root, task, step, onChange) {
    var pickRow = $('ed-harvest-pick-row');
    if (pickRow) {
        pickRow.addEventListener('click', function () {
            state.harvestPick = { taskId: task.id, stepId: step.id, mode: 'row' };
            post('pickHarvest', { mode: 'row' });
        });
    }

    var addField = $('ed-harvest-add-field');
    if (addField) {
        addField.addEventListener('click', function () {
            state.harvestPick = { taskId: task.id, stepId: step.id, mode: 'field', index: null };
            post('pickHarvest', { mode: 'field', itemSelector: (step.harvest || {}).itemSelector || '' });
        });
    }

    root.querySelectorAll('[data-harvest-repick]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            state.harvestPick = {
                taskId: task.id, stepId: step.id, mode: 'field',
                index: Number(btn.getAttribute('data-harvest-repick')),
            };
            post('pickHarvest', { mode: 'field', itemSelector: (step.harvest || {}).itemSelector || '' });
        });
    });

    root.querySelectorAll('[data-drop-harvest-field]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var h = Object.assign({}, step.harvest);
            h.fields = (h.fields || []).slice();
            var gone = h.fields.splice(Number(btn.getAttribute('data-drop-harvest-field')), 1)[0];
            if (gone && h.dedupeBy === gone.name) h.dedupeBy = null;
            step.harvest = h;
            onChange();
        });
    });

    // The attribute box only exists for an attribute column, so the row is re-rendered on change
    // rather than trying to grow a box in place.
    root.querySelectorAll('.harvest-source').forEach(function (sel) {
        sel.addEventListener('change', onChange);
    });
}

function currentOperand(step, slot) {
    if (slot.indexOf('col:') === 0) {
        return ((step.writeDataset || {}).columns || {})[slot.slice(4)] || null;
    }
    if (slot.indexOf('in:') === 0) {
        return (step.runTaskInputs || {})[slot.slice(3)] || null;
    }
    var condition = step.action === 'if' ? step.condition : (step.wait || {}).condition;
    var ref = condition ? condition[slot] : null;
    return isBound(ref) ? ref : null;
}

function setOperand(step, slot, binding) {
    if (slot.indexOf('in:') === 0) {
        step.runTaskInputs = Object.assign({}, step.runTaskInputs);
        step.runTaskInputs[slot.slice(3)] = binding;
        return;
    }
    if (slot.indexOf('col:') === 0) {
        var spec = Object.assign({ format: 'csv', append: true }, step.writeDataset);
        spec.columns = Object.assign({}, spec.columns);
        spec.columns[slot.slice(4)] = binding;
        step.writeDataset = spec;
        return;
    }
    if (step.action === 'if') {
        step.condition = Object.assign({ op: 'notEmpty' }, step.condition);
        step.condition[slot] = binding;
        return;
    }
    var wait = Object.assign({}, step.wait);
    wait.condition = Object.assign({ op: 'notEmpty' }, wait.condition);
    wait.condition[slot] = binding;
    step.wait = wait;
}
