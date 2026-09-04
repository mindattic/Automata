// Turning a literal field into a bound one, and back.
//
// The rule that shapes this file: a user SELECTS a source, never types a reference. Every valid
// source is enumerated from the task itself — the outputs earlier steps declare — so a binding
// cannot name something that does not exist, and the editor never becomes a place where you have
// to know a syntax. The one exception is an environment variable, whose name only the user knows;
// it is asked for through the ordinary rename dialog rather than a free-text formula box.
//
// Composition beyond "this source, optionally wrapped in a literal" is deliberately out of scope
// here. That belongs to the authoring layer, not to a dropdown.

import { esc, findStep } from './core.js';
import { openListPicker, openRenameModal, openInfoModal } from './modal.js';

function flatten(steps, out) {
    (steps || []).forEach(function (s) {
        out.push(s);
        flatten(s.children, out);
    });
    return out;
}

// Every output published by a step that runs BEFORE this one. Ordering is the tree's own
// depth-first order, which is exactly the order the engine runs it in — so anything offered here
// is genuinely available by the time this step runs.
export function sourcesFor(task, step) {
    var all = flatten(task.steps, []);
    var index = all.indexOf(step);
    if (index < 0) index = all.length;
    var sources = [];
    all.slice(0, index).forEach(function (s) {
        (s.outputs || []).forEach(function (o) {
            if (!o.name) return;
            sources.push({
                stepId: s.id,
                output: o.name,
                label: (s.label || s.action) + ' → ' + o.name,
            });
        });
    });
    return sources;
}

export function describeBinding(binding) {
    if (!binding) return '';
    if (binding.label) return binding.label;
    if (binding.kind === 'envVar') return 'env: ' + (binding.envVarName || '?');
    if (binding.kind === 'taskInput') return 'input: ' + (binding.parameterName || '?');
    if (binding.kind === 'stepOutput') return binding.outputField || 'output';
    return binding.kind;
}

/// Renders the control for one bindable field: a plain input when it holds a literal, a chip plus
/// its optional prefix/suffix when it is bound.
export function fieldControlHtml(id, label, value, placeholder, binding) {
    if (!binding) {
        return '<input type="text" id="' + id + '" aria-label="' + esc(label) + '"' +
            (placeholder ? ' placeholder="' + esc(placeholder) + '"' : '') +
            ' value="' + esc(value) + '" />' +
            '<button type="button" class="mini binding-toggle" data-bind="' + id +
            '" aria-label="Bind ' + esc(label) + ' to an earlier step’s output instead of typing it"' +
            ' data-tooltip="Use an earlier step’s output instead of a fixed value">🔗</button>';
    }
    return '<span class="chip bound" role="button" tabindex="0" data-bind="' + id + '"' +
        ' aria-label="' + esc(label) + ' is bound to ' + esc(describeBinding(binding)) +
        '. Activate to change it." data-tooltip="Bound — activate to change or unbind">' +
        '🔗 ' + esc(describeBinding(binding)) + '</span>' +
        '<input type="text" class="affix" data-affix="prefix" data-bind="' + id + '"' +
        ' aria-label="Text before the bound value" placeholder="before"' +
        ' value="' + esc(binding.prefix) + '" />' +
        '<input type="text" class="affix" data-affix="suffix" data-bind="' + id + '"' +
        ' aria-label="Text after the bound value" placeholder="after"' +
        ' value="' + esc(binding.suffix) + '" />';
}

/// Opens the source picker for one field. `onCommit(bindingOrNull)` receives the new binding, or
/// null when the user chooses to go back to a plain value.
export function openBindingPicker(task, step, fieldLabel, current, onCommit) {
    var sources = sourcesFor(task, step);

    var inputs = (task.inputs || []).filter(function (i) { return i.name; });

    if (!sources.length && !inputs.length && !current) {
        openInfoModal('Nothing to bind to yet',
            'A binding reuses a value an earlier step captured, or one this task takes from ' +
            'whoever runs it. Add an "extractText" step before this one and name its output, or ' +
            'give the task an input, and it will appear here.', null);
        return;
    }

    var items = sources.map(function (s) {
        return {
            value: 'step:' + s.stepId + ':' + s.output,
            label: s.label,
            detail: 'captured by an earlier step in this task',
        };
    });
    // The task's own inputs — the answer to "the same task, run for a different search term".
    // Offered, never typed: a hand-written {{placeholder}} is a syntax nothing can check.
    inputs.forEach(function (i) {
        items.push({
            value: 'input:' + i.name,
            label: 'input: ' + i.name,
            detail: i.description ||
                (i.default != null ? 'defaults to “' + i.default + '”' : 'supplied when this task runs'),
        });
    });
    items.push({
        value: 'env',
        label: 'Environment variable…',
        detail: 'read from the machine at run time — the way to keep a secret out of the store',
    });
    if (current) {
        items.push({
            value: 'clear',
            label: 'Use a fixed value instead',
            detail: 'unbind this field and go back to typing it',
        });
    }

    openListPicker('Bind ' + fieldLabel, 'Where should this value come from?', items, function (choice) {
        if (choice === 'clear') { onCommit(null); return; }
        if (choice === 'env') {
            openRenameModal('Environment variable', current && current.envVarName ? current.envVarName : '',
                function (name) {
                    onCommit({
                        kind: 'envVar',
                        envVarName: name,
                        label: 'env: ' + name,
                        prefix: current ? current.prefix : null,
                        suffix: current ? current.suffix : null,
                    });
                });
            return;
        }
        if (choice.indexOf('input:') === 0) {
            var inputName = choice.slice('input:'.length);
            onCommit({
                kind: 'taskInput',
                parameterName: inputName,
                label: 'input: ' + inputName,
                prefix: current ? current.prefix : null,
                suffix: current ? current.suffix : null,
            });
            return;
        }
        var parts = choice.split(':');
        var sourceStep = findStep(task.steps, parts[1]);
        onCommit({
            kind: 'stepOutput',
            sourceStepId: parts[1],
            outputField: parts[2],
            label: (sourceStep ? (sourceStep.label || sourceStep.action) : 'step') + ' → ' + parts[2],
            prefix: current ? current.prefix : null,
            suffix: current ? current.suffix : null,
        });
    });
}
