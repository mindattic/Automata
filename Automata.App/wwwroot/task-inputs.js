// Declaring what a task takes from whoever runs it.
//
// This is the whole of "templating", and it is deliberately a list of named things rather than
// `{{query}}` typed into a value box. A hand-written placeholder is an expression language arriving
// one string at a time: nothing can enumerate it, nothing can check it, and a typo in it fails at
// run time as a value that quietly stayed literal. A declared input appears in the binding picker,
// can be supplied by a runTask step or by `--input` on the command line, and is refused BY NAME
// when nothing supplies it.
//
// Committed as it is edited, like the scoped-settings editor — so the only button is Done.

import { $, esc, saveTask, findTask } from './core.js';
import { openFormModal } from './modal.js';

export function openTaskInputs(taskId) {
    var task = findTask(taskId);
    if (!task) return;

    var form = openFormModal('Task inputs',
        'Values this task takes from whoever runs it. A step inside it binds to one instead of ' +
        'holding a fixed value.', {});

    render();

    function render() {
        var inputs = task.inputs || [];
        form.innerHTML =
            '<div class="column-list">' +
            (inputs.length
                ? inputs.map(function (input, index) {
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
                        '</div>';
                }).join('')
                : '<p class="scope-note">No inputs — this task runs the same way every time.</p>') +
            '<button type="button" class="mini" id="ti-add">+ input</button>' +
            '</div>' +
            '<p class="scope-note">A blank default makes the input required: a run that does not ' +
            'supply it fails at the step that needed it, naming it.</p>';
        wire();
    }

    function wire() {
        $('ti-add').addEventListener('click', function () {
            task.inputs = (task.inputs || []).concat([{ name: nextName(), description: null, default: '' }]);
            commit();
        });

        form.querySelectorAll('[data-drop-input]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var index = Number(btn.getAttribute('data-drop-input'));
                task.inputs = (task.inputs || []).filter(function (_, i) { return i !== index; });
                commit();
            });
        });

        form.querySelectorAll('[data-field]').forEach(function (el) {
            el.addEventListener('change', function () {
                var row = el.closest('[data-input-index]');
                var index = Number(row.getAttribute('data-input-index'));
                var input = (task.inputs || [])[index];
                if (!input) return;
                if (el.getAttribute('data-field') === 'name') input.name = el.value.trim();
                // Blank is "required", which is a different answer from "defaults to empty" — so
                // it is stored as null rather than as "".
                else input.default = el.value === '' ? null : el.value;
                commit();
            });
        });
    }

    function commit() {
        saveTask(task);
        render();
    }

    function nextName() {
        var taken = (task.inputs || []).map(function (i) { return i.name; });
        for (var n = 1; ; n++) {
            var candidate = n === 1 ? 'input' : 'input' + n;
            if (taken.indexOf(candidate) < 0) return candidate;
        }
    }
}
