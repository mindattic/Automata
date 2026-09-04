// Editors for the control-flow steps: if, forEach, runTask, writeDataset, and a wait on a
// condition.
//
// Every source is still SELECTED — datasets and tasks come from lists the host pushed, and a
// condition's operands go through the same binding picker as any other value. The only free text
// is a literal you were always going to type anyway.

import { $, esc, state } from './core.js';
import { openBindingPicker, describeBinding } from './binding-field.js';

var OPS = [
    { value: 'equals', label: 'is exactly' },
    { value: 'notEquals', label: 'is not' },
    { value: 'contains', label: 'contains' },
    { value: 'greaterThan', label: 'is greater than' },
    { value: 'lessThan', label: 'is less than' },
    { value: 'notEmpty', label: 'has any value' },
    { value: 'empty', label: 'is empty' },
    { value: 'isTrue', label: 'is true' },
    { value: 'isFalse', label: 'is false' },
];

// Operators that take no right-hand side; showing an inert box beside them would invite a value
// that is silently ignored.
var UNARY = ['notEmpty', 'empty', 'isTrue', 'isFalse'];

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
        '<select id="ed-cond-op" aria-label="Comparison">' +
        OPS.map(function (o) {
            return '<option value="' + o.value + '"' + (o.value === op ? ' selected' : '') + '>' +
                esc(o.label) + '</option>';
        }).join('') + '</select>' +
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

function taskOptions(currentTaskId, selected) {
    var options = ['<option value="">(choose a task)</option>'];
    (state.collections || []).forEach(function (c) {
        (c.tasks || []).forEach(function (t) {
            // A task calling itself is caught at run time too, but not offering it is kinder.
            if (t.id === currentTaskId) return;
            options.push('<option value="' + esc(t.id) + '"' + (t.id === selected ? ' selected' : '') + '>' +
                esc(c.name + ' / ' + t.name) + '</option>');
        });
    });
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

        case 'runTask':
            return '<div class="field"><span>Run task</span>' +
                '<select id="ed-runtask" aria-label="Task to run from here">' +
                taskOptions(task.id, step.runTaskId) + '</select></div>';

        case 'writeDataset': {
            var w = step.writeDataset || {};
            var columns = w.columns || {};
            var names = Object.keys(columns);
            return '<div class="field"><span>Write to</span>' +
                '<input type="text" id="ed-write-name" aria-label="Dataset file to write"' +
                ' placeholder="results.csv" value="' + esc(w.datasetName || '') + '" />' +
                '<label class="inline"><input type="checkbox" id="ed-write-append"' +
                (w.append === false ? '' : ' checked') + ' /> append</label></div>' +
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

        default:
            return '';
    }
}

/// True when this step's wait needs a condition editor rather than a duration or a clock time.
export function waitNeedsCondition(step) {
    return step.action === 'wait' && step.wait && step.wait.mode === 'untilCondition';
}

export function waitConditionHtml(step) {
    return conditionHtml((step.wait || {}).condition);
}

/// Reads the flow fields back onto the step. Returns nothing; mutates in place, like the rest of
/// the editor.
export function commitFlowFields(step) {
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
    } else if (step.action === 'writeDataset') {
        var spec = Object.assign({ format: 'csv' }, step.writeDataset);
        spec.datasetName = (($('ed-write-name') || {}).value || '').trim();
        spec.append = ($('ed-write-append') || {}).checked !== false;
        spec.columns = readColumns(spec.columns || {});
        step.writeDataset = spec;
    }
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
}

function currentOperand(step, slot) {
    if (slot.indexOf('col:') === 0) {
        return ((step.writeDataset || {}).columns || {})[slot.slice(4)] || null;
    }
    var condition = step.action === 'if' ? step.condition : (step.wait || {}).condition;
    var ref = condition ? condition[slot] : null;
    return isBound(ref) ? ref : null;
}

function setOperand(step, slot, binding) {
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
