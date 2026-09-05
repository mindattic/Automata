// What a task takes from whoever runs it, and what it hands on when it finishes.
//
// This is the whole of "templating", and it is deliberately a list of named things rather than
// `{{query}}` typed into a value box. A hand-written placeholder is an expression language arriving
// one string at a time: nothing can enumerate it, nothing can check it, and a typo in it fails at
// run time as a value that quietly stayed literal. A declared input appears in the binding picker,
// can be supplied by a runTask step or by `--input` on the command line, and is refused BY NAME
// when nothing supplies it.
//
// Outputs are the other half of that, and they are what makes a collection a pipeline: a task
// publishes a named value, and a later task in the same collection wires one of its inputs to it.
// Both ends of a wiring are PICKED — the task, its output, the step that produces one. Nothing here
// takes an id typed by hand, because a mistyped id is a wiring that silently does nothing and looks
// exactly like one that works.
//
// Committed as it is edited, like the scoped-settings editor — so the only button is Done.

import { $, esc, saveTask, findTask, findCollection } from './core.js';
import { openFormModal } from './modal.js';

/// One option value carrying two names. JSON rather than a joined string: a separator that could
/// appear inside a name would make some wirings unreadable, and which ones would depend on what
/// the user called things.
function pair(a, b) { return JSON.stringify([a, b]); }

export function openTaskInputs(taskId) {
    var task = findTask(taskId);
    if (!task) return;

    var form = openFormModal('Task inputs and outputs',
        'Values this task takes from whoever runs it, and values it hands to the tasks after it ' +
        'in its collection.', {});

    render();

    function render() {
        var inputs = task.inputs || [];
        var outputs = task.outputs || [];
        form.innerHTML =
            '<h3 class="section-label">Takes</h3>' +
            '<div class="column-list">' +
            (inputs.length
                ? inputs.map(inputRow).join('')
                : '<p class="scope-note">No inputs — this task runs the same way every time.</p>') +
            '<button type="button" class="mini" id="ti-add">+ input</button>' +
            '</div>' +
            '<p class="scope-note">A blank default makes the input required: a run that does not ' +
            'supply it fails at the step that needed it, naming it. A value supplied directly — ' +
            'from <code>--input</code>, or from a task that calls this one — always wins over a ' +
            'wiring.</p>' +

            '<h3 class="section-label">Publishes</h3>' +
            '<div class="column-list">' +
            (outputs.length
                ? outputs.map(outputRow).join('')
                : '<p class="scope-note">Nothing — this task keeps what it finds to itself.</p>') +
            '<button type="button" class="mini" id="to-add"' +
            (producers().length ? '' : ' disabled') + '>+ output</button>' +
            '</div>' +
            '<p class="scope-note">' +
            (producers().length
                ? 'A published value comes from a step that captures one. Later tasks in this ' +
                  'collection bind to the NAME, so re-recording the step behind it changes nothing ' +
                  'for them.'
                : 'Nothing in this task captures a value yet — add an “Extract text” step and it ' +
                  'can publish what that step reads.') +
            '</p>';
        wire();
    }

    // ---- takes -------------------------------------------------------------------------------

    function inputRow(input, index) {
        return '<div class="column-row" data-input-index="' + index + '">' +
            '<input type="text" class="input-name" data-field="name"' +
            ' aria-label="Input name" placeholder="query" value="' +
            esc(input.name || '') + '" />' +
            '<input type="text" class="operand" data-field="default"' +
            ' aria-label="Default value for ' + esc(input.name || 'this input') + '"' +
            ' placeholder="(required)" value="' + esc(input.default == null ? '' : input.default) +
            '" />' +
            '<button type="button" class="mini" data-drop-input="' + index +
            '" aria-label="Remove input ' + esc(input.name || '') + '">✕</button>' +
            '</div>' +
            '<div class="column-row" data-input-index="' + index + '">' +
            '<span class="scope-note">comes from</span>' +
            fromPicker(input, index) +
            '</div>';
    }

    /// Every output published by another task in THIS task's collection, as one flat list of
    /// options. Flat rather than grouped by task because a wiring is one choice, and the task's
    /// name is already in the label.
    function fromPicker(input, index) {
        var options = ['<option value="">nothing — use the default</option>'];
        var wiredTo = input.from ? pair(input.from.taskId, input.from.outputName) : '';
        var found = false;

        upstream().forEach(function (other) {
            (other.outputs || []).forEach(function (output) {
                var value = pair(other.id, output.name);
                if (value === wiredTo) found = true;
                options.push('<option value="' + esc(value) + '"' +
                    (value === wiredTo ? ' selected' : '') + '>' +
                    esc(other.name) + ' → ' + esc(output.name) + '</option>');
            });
        });

        // A wiring whose task or output has gone is kept and SHOWN as missing rather than
        // silently dropped: quietly unwiring it would turn a broken pipeline into one that runs
        // and reports success on a default nobody chose.
        if (wiredTo && !found) {
            options.push('<option value="' + esc(wiredTo) + '" selected>' +
                esc((input.from.taskName || input.from.taskId) + ' → ' + input.from.outputName) +
                ' (no longer published)</option>');
        }

        return '<select data-from="' + index + '" aria-label="Where ' +
            esc(input.name || 'this input') + ' comes from">' + options.join('') + '</select>';
    }

    /// The other tasks in this task's collection. Any of them, not only the ones before it: the
    /// order is the collection's to change, and a wiring that pointed backwards would simply fall
    /// back to its default and say so, which is a better answer than a picker that hides a task.
    function upstream() {
        var collection = findCollection(task.collectionId);
        return ((collection && collection.tasks) || []).filter(function (other) {
            return other.id !== task.id && (other.outputs || []).length > 0;
        });
    }

    // ---- publishes ---------------------------------------------------------------------------

    function outputRow(output, index) {
        return '<div class="column-row" data-output-index="' + index + '">' +
            '<input type="text" class="input-name" data-out-field="name"' +
            ' aria-label="Output name" placeholder="orderId" value="' +
            esc(output.name || '') + '" />' +
            sourcePicker(output, index) +
            '<button type="button" class="mini" data-drop-output="' + index +
            '" aria-label="Remove output ' + esc(output.name || '') + '">✕</button>' +
            '</div>';
    }

    function sourcePicker(output, index) {
        var chosen = pair(output.sourceStepId || '', output.sourceOutputField || '');
        var options = producers().map(function (source) {
            var value = pair(source.stepId, source.field);
            return '<option value="' + esc(value) + '"' +
                (value === chosen ? ' selected' : '') + '>' + esc(source.label) + '</option>';
        });
        return '<select data-source="' + index + '" aria-label="Which step ' +
            esc(output.name || 'this output') + ' comes from">' + options.join('') + '</select>';
    }

    /// Every (step, captured value) pair inside this task — the only things an output can name.
    /// Walks the tree, because a step inside a loop or an "if" captures values exactly as one at
    /// the top level does.
    function producers() {
        var found = [];
        (function walk(steps) {
            (steps || []).forEach(function (step) {
                (step.outputs || []).forEach(function (field) {
                    found.push({
                        stepId: step.id,
                        field: field.name,
                        label: (step.label || step.action || step.id) + ' → ' + field.name,
                    });
                });
                walk(step.children);
            });
        })(task.steps);
        return found;
    }

    // ---- wiring up ---------------------------------------------------------------------------

    function wire() {
        $('ti-add').addEventListener('click', function () {
            task.inputs = (task.inputs || []).concat([{ name: nextName('input', task.inputs), default: '' }]);
            commit();
        });

        var addOutput = $('to-add');
        if (addOutput && !addOutput.disabled) {
            addOutput.addEventListener('click', function () {
                var first = producers()[0];
                task.outputs = (task.outputs || []).concat([{
                    name: nextName('value', task.outputs),
                    sourceStepId: first.stepId,
                    sourceOutputField: first.field,
                }]);
                commit();
            });
        }

        form.querySelectorAll('[data-drop-input]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var index = Number(btn.getAttribute('data-drop-input'));
                task.inputs = (task.inputs || []).filter(function (_, i) { return i !== index; });
                commit();
            });
        });

        form.querySelectorAll('[data-drop-output]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var index = Number(btn.getAttribute('data-drop-output'));
                task.outputs = (task.outputs || []).filter(function (_, i) { return i !== index; });
                commit();
            });
        });

        form.querySelectorAll('[data-field]').forEach(function (el) {
            el.addEventListener('change', function () {
                var input = (task.inputs || [])[Number(el.closest('[data-input-index]')
                    .getAttribute('data-input-index'))];
                if (!input) return;
                if (el.getAttribute('data-field') === 'name') input.name = el.value.trim();
                // Blank is "required", which is a different answer from "defaults to empty" — so
                // it is stored as null rather than as "".
                else input.default = el.value === '' ? null : el.value;
                commit();
            });
        });

        form.querySelectorAll('[data-out-field]').forEach(function (el) {
            el.addEventListener('change', function () {
                var output = (task.outputs || [])[Number(el.closest('[data-output-index]')
                    .getAttribute('data-output-index'))];
                if (!output) return;
                output.name = el.value.trim();
                commit();
            });
        });

        form.querySelectorAll('[data-from]').forEach(function (el) {
            el.addEventListener('change', function () {
                var input = (task.inputs || [])[Number(el.getAttribute('data-from'))];
                if (!input) return;
                if (!el.value) {
                    delete input.from;
                } else {
                    var parts = JSON.parse(el.value);
                    var other = findTask(parts[0]);
                    // The name is stored beside the id so a wiring whose task was deleted can still
                    // say what it was pointing at. The id is what resolves it.
                    input.from = { taskId: parts[0], taskName: other ? other.name : null, outputName: parts[1] };
                }
                commit();
            });
        });

        form.querySelectorAll('[data-source]').forEach(function (el) {
            el.addEventListener('change', function () {
                var output = (task.outputs || [])[Number(el.getAttribute('data-source'))];
                if (!output) return;
                var parts = JSON.parse(el.value);
                output.sourceStepId = parts[0];
                output.sourceOutputField = parts[1] || null;
                commit();
            });
        });
    }

    function commit() {
        saveTask(task);
        render();
    }

    function nextName(stem, existing) {
        var taken = (existing || []).map(function (x) { return x.name; });
        for (var n = 1; ; n++) {
            var candidate = n === 1 ? stem : stem + n;
            if (taken.indexOf(candidate) < 0) return candidate;
        }
    }
}
