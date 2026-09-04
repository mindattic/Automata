// The WYSIWYG step editor: a typed form over the selected step, committing on change so the
// host echoes the tree back and the UI re-renders from it.

import {
    $, editorEl, esc, state, selectedTask, findStep, removeStep, saveTask, ALL_ACTIONS,
} from './core.js';
import { openConfirmModal } from './modal.js';
import { addStep } from './tree.js';
import { openScopedSettings } from './scoped-settings.js';
import { fieldControlHtml, openBindingPicker } from './binding-field.js';
import {
    flowFieldsHtml, waitNeedsCondition, waitConditionHtml, commitFlowFields, wireFlowFields,
} from './flow-fields.js';

// Steps that act on the page need an element; control-flow steps act on the run.
var NEEDS_TARGET = {
    navigate: false, group: false, wait: false,
    if: false, forEach: false, runTask: false, writeDataset: false,
    // A harvest's rows come from a picked set selector, not from a single-element
    // fingerprint, so the ordinary target box would be a second, contradictory answer.
    extractAll: false,
    // Zoom is about the whole page, not an element in it.
    setZoom: false,
    // An aggregate reads a dataset, never the page.
    aggregate: false,
};
var NEEDS_VALUE = { typeText: 'Text to type', setValue: 'Value to set', selectOption: 'Option text',
    uploadFile: 'Local file path', assertElement: 'Expected text (optional)' };

// Bindable fields, keyed by the editor input id. The key is the name the engine looks up in
// step.bindings, so these two must stay in step.
var BINDABLE = { 'ed-value': 'Value', 'ed-url': 'Url' };

function bindingFor(step, id) {
    return step.bindings ? step.bindings[BINDABLE[id]] || null : null;
}

function setBinding(step, id, binding) {
    var field = BINDABLE[id];
    if (binding) {
        step.bindings = step.bindings || {};
        step.bindings[field] = binding;
        return;
    }
    if (!step.bindings) return;
    delete step.bindings[field];
    // An empty bindings object would make an unbound step look bound in its JSON.
    if (!Object.keys(step.bindings).length) step.bindings = null;
}

function outputName(step) {
    return step.outputs && step.outputs.length ? step.outputs[0].name || '' : '';
}

export function renderEditor() {
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
        '<div class="section-head"><h2 class="section-label">Step editor</h2>' +
        '<span class="node-btns">' +
        '<button class="mini" id="ed-add-sub">+ substep</button>' +
        '<button class="mini" id="ed-settings" aria-label="Engine settings for this step">⚙ settings</button>' +
        '<button class="mini" id="ed-delete">🗑 delete</button>' +
        '</span></div>' +
        '<div class="field"><span>Action</span>' +
        '<select id="ed-action" aria-label="Step action">' +
        ALL_ACTIONS.map(function (a) {
            return '<option value="' + a + '"' + (a === step.action ? ' selected' : '') + '>' + a + '</option>';
        }).join('') + '</select></div>' +
        '<div class="field"><span>Label</span><input type="text" id="ed-label" aria-label="Step label" value="' +
        esc(step.label) + '" /></div>' +
        (step.action === 'navigate'
            ? '<div class="field"><span>URL</span>' +
              fieldControlHtml('ed-url', 'URL to navigate to', step.url, '', bindingFor(step, 'ed-url')) +
              '</div>' : '') +
        (valuePh
            ? '<div class="field"><span>Value</span>' +
              fieldControlHtml('ed-value', valuePh, step.value, valuePh, bindingFor(step, 'ed-value')) +
              '</div>' : '') +
        (step.action === 'extractText'
            ? '<div class="field"><span>Save as</span><input type="text" id="ed-output"' +
              ' aria-label="Name later steps use to bind to this captured value"' +
              ' placeholder="e.g. total" value="' + esc(outputName(step)) + '" /></div>' : '') +
        (step.action === 'wait' ? waitFieldsHtml(step) : '') +
        (waitNeedsCondition(step) ? waitConditionHtml(step) : '') +
        flowFieldsHtml(step, task) +
        '<div class="field checks">' +
        '<label class="inline"><input type="checkbox" id="ed-pause"' + (step.pauseForUser ? ' checked' : '') + ' /> pause for user</label>' +
        '<label class="inline"><input type="checkbox" id="ed-commit"' + (step.isCommitPoint ? ' checked' : '') + ' /> commit point</label>' +
        '<label class="inline"><input type="checkbox" id="ed-masked"' + (step.masked ? ' checked' : '') +
        ' /> masked</label>' +
        '<span class="timeout"><span>timeout ms</span><input type="number" id="ed-timeout" aria-label="Step timeout in milliseconds" value="' +
        (step.timeoutMs || '') + '" placeholder="10000" /></span>' +
        '</div>' +
        (needsTarget
            ? '<details class="target"><summary>Target: <code>' + esc(targetSummary) + '</code></summary>' +
              tgtField('id', t.id) + tgtField('cssSelector', t.cssSelector) + tgtField('xPath', t.xPath) +
              tgtField('tag', t.tag) + tgtField('nameAttr', t.nameAttr) + tgtField('typeAttr', t.typeAttr) +
              tgtField('visibleText', t.visibleText) + tgtField('ariaRole', t.ariaRole) +
              tgtField('ariaLabel', t.ariaLabel) + tgtField('nearbyLabelText', t.nearbyLabelText) +
              tgtField('placeholder', t.placeholder) +
              '<div class="field"><span>classList</span><input type="text" data-tgt="classList"' +
              ' aria-label="Target class list, comma-separated" value="' +
              esc((t.classList || []).join(', ')) + '" placeholder="comma-separated" /></div>' +
              '</details>'
            : '');

    // Only the two local wait modes are offered. Until-condition and until-signal need run state
    // the replay engine does not have, so offering them here would be offering a control that
    // fails at run time.
    function waitFieldsHtml(s) {
        var spec = s.wait || {};
        var mode = spec.mode || 'duration';
        return '<div class="field"><span>Wait for</span>' +
            '<select id="ed-wait-mode" aria-label="What this step waits for">' +
            '<option value="duration"' + (mode === 'duration' ? ' selected' : '') + '>a fixed duration</option>' +
            '<option value="untilTimeOfDay"' + (mode === 'untilTimeOfDay' ? ' selected' : '') + '>until a time of day</option>' +
            '<option value="untilCondition"' + (mode === 'untilCondition' ? ' selected' : '') + '>until a condition holds</option>' +
            '</select></div>' +
            (mode === 'untilTimeOfDay'
                ? '<div class="field"><span>Time</span>' +
                  '<input type="time" id="ed-wait-time" aria-label="Time of day to wait until" value="' +
                  esc(spec.timeOfDay ? String(spec.timeOfDay).slice(0, 5) : '') + '" />' +
                  '<input type="text" id="ed-wait-zone" aria-label="Time zone id, blank for this machine"' +
                  ' placeholder="this machine" value="' + esc(spec.timeZoneId) + '" /></div>'
                : mode === 'untilCondition' ? ''
                : '<div class="field"><span>Duration</span>' +
                  '<input type="number" id="ed-wait-ms" min="0" aria-label="Milliseconds to wait" value="' +
                  esc(spec.durationMs != null ? spec.durationMs : 1000) + '" /><span class="unit">ms</span></div>');
    }

    function tgtField(key, val) {
        return '<div class="field"><span>' + key + '</span><input type="text" data-tgt="' + key +
            '" aria-label="Target ' + key + '" value="' + esc(val) + '" /></div>';
    }

    function commitEditor() {
        step.action = $('ed-action').value;
        step.label = $('ed-label').value;
        // A bound field renders a chip rather than an input, so there is nothing to read back —
        // and the literal underneath is deliberately preserved, so unbinding restores it.
        if ($('ed-url')) step.url = $('ed-url').value || null;
        if ($('ed-value')) step.value = $('ed-value').value || null;
        step.pauseForUser = $('ed-pause').checked;
        step.isCommitPoint = $('ed-commit').checked;
        step.masked = $('ed-masked').checked;

        if ($('ed-output')) {
            var name = $('ed-output').value.trim();
            step.outputs = name ? [{ name: name, type: 'string' }] : null;
        }

        if ($('ed-wait-mode')) {
            var mode = $('ed-wait-mode').value;
            var wait = Object.assign({}, step.wait || {}, { mode: mode });
            if (mode === 'untilCondition') {
                // The condition itself is read by commitFlowFields below.
                wait.pollMs = wait.pollMs || 2000;
            } else if (mode === 'duration') {
                var waitMs = parseInt(($('ed-wait-ms') || {}).value, 10);
                wait.durationMs = isNaN(waitMs) ? 1000 : waitMs;
            } else {
                var time = ($('ed-wait-time') || {}).value || '';
                wait.timeOfDay = time ? time + ':00' : null;
                wait.timeZoneId = (($('ed-wait-zone') || {}).value || '').trim() || null;
            }
            step.wait = wait;
        }

        commitFlowFields(step);

        // Affix inputs live beside a bound chip; they wrap the resolved value.
        editorEl.querySelectorAll('.affix').forEach(function (inp) {
            var binding = bindingFor(step, inp.getAttribute('data-bind'));
            if (binding) binding[inp.getAttribute('data-affix')] = inp.value || null;
        });
        var ms = parseInt($('ed-timeout').value, 10);
        step.timeoutMs = isNaN(ms) ? null : ms;
        var tgtInputs = editorEl.querySelectorAll('[data-tgt]');
        if (tgtInputs.length) {
            var tgt = {};
            var hasAny = false;
            tgtInputs.forEach(function (inp) {
                var key = inp.getAttribute('data-tgt');
                if (key === 'classList') {
                    tgt.classList = inp.value.split(',').map(function (s) { return s.trim(); }).filter(Boolean);
                    if (tgt.classList.length) hasAny = true;
                } else {
                    tgt[key] = inp.value || null;
                    if (inp.value) hasAny = true;
                }
            });
            // An all-empty fingerprint stays null so the engine reports "step has no target
            // fingerprint" instead of a misleading resolve-timeout on a blank identity.
            step.target = hasAny ? tgt : null;
        }
        saveTask(task);
    }

    // Commit on blur/change of any field — the host echoes state back and the UI re-renders.
    editorEl.querySelectorAll('input, select').forEach(function (inp) {
        inp.addEventListener('change', commitEditor);
    });
    // 🔗 on a literal field, or the chip itself once bound — both open the same source picker.
    editorEl.querySelectorAll('.binding-toggle, .chip.bound').forEach(function (el) {
        function open() {
            var id = el.getAttribute('data-bind');
            openBindingPicker(task, step, BINDABLE[id], bindingFor(step, id), function (binding) {
                setBinding(step, id, binding);
                saveTask(task);
            });
        }
        el.addEventListener('click', open);
        el.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); }
        });
    });

    wireFlowFields(editorEl, task, step, function () { saveTask(task); });

    $('ed-add-sub').addEventListener('click', function () { addStep(task.id, step.id); });
    $('ed-settings').addEventListener('click', function () {
        openScopedSettings('step', {
            collectionId: state.sel.collectionId, taskId: task.id, stepId: step.id,
        });
    });
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
