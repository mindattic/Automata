// Per-scope engine settings: global → collection → task → step.
//
// The presentation rule this module exists to enforce: an inherited value is NEVER a pre-filled,
// editable-looking control. That is the classic trap — a user edits what looks like this scope's
// value and either changes something broader than they meant, or believes they scoped a change
// they did not. So an inherited setting renders as read-only text naming the scope it came from,
// with an explicit Override button; only then does a real control appear, paired with an explicit
// Reset. Both directions are named actions, never "clear the field and hope".
//
// Changes commit as they are made, matching the step editor's convention, so the dialog needs no
// Save button — only Done.

import { $, esc, post, state, saveTask, findCollection, findTask, findStep } from './core.js';
import { openFormModal } from './modal.js';

// Only settings the engine honours TODAY are offered. MaxConcurrency, Isolation, BrowserProfile
// and ScreenshotOnFailure exist in the model but are inert until the phases that make them real,
// and a control that silently does nothing is worse than no control at all.
var FIELDS = [
    { key: 'defaultStepTimeoutMs', label: 'Step timeout', type: 'number', unit: 'ms', min: 100 },
    { key: 'selfHeal', label: 'Self-heal selectors', type: 'bool' },
    { key: 'allowLlmRepair', label: 'Allow LLM repair', type: 'bool' },
    { key: 'retry', label: 'Retry a failed step', type: 'retry' },
    { key: 'continueOnStepError', label: 'Continue after a failed step', type: 'bool' },
    {
        key: 'continueOnTaskError', label: 'Continue after a failed task', type: 'bool',
        // Only a collection runs tasks, so the setting is meaningless deeper than that.
        levels: ['global', 'collection'],
    },
    { key: 'llmProvider', label: 'LLM provider', type: 'select', options: ['claude', 'openai', 'gemini', 'kimi'] },
];

var LEVEL_NAMES = { global: 'Global', collection: 'Collection', task: 'Task', step: 'Step' };

// Entities are looked up fresh on every access rather than captured: the host echoes a whole new
// collections tree after each save, so anything captured when the dialog opened is detached by
// the time the second edit lands.
function scopeContext(level, ids) {
    function collection() { return ids.collectionId ? findCollection(ids.collectionId) : null; }
    function task() { return ids.taskId ? findTask(ids.taskId) : null; }
    function step() {
        var t = task();
        return t && ids.stepId ? findStep(t.steps, ids.stepId) : null;
    }

    // The scopes ABOVE this one, outermost first, so the deepest match wins.
    function above() {
        var chain = [];
        if (level !== 'global') chain.push({ name: 'Global', settings: state.engineDefaults });
        if (level === 'task' || level === 'step') {
            var c = collection();
            chain.push({ name: 'Collection', settings: c && c.settings });
        }
        if (level === 'step') {
            var t = task();
            chain.push({ name: 'Task', settings: t && t.settings });
        }
        return chain;
    }

    function own() {
        if (level === 'global') return state.engineDefaults;
        var owner = level === 'collection' ? collection() : level === 'task' ? task() : step();
        return owner ? owner.settings : null;
    }

    function persist(next) {
        // An override that overrides nothing is dropped rather than stored, so a scope nobody has
        // configured never looks configured. The host prunes it too — this is the client half.
        var value = next && Object.keys(next).length ? next : null;
        if (level === 'global') {
            state.engineDefaults = value;
            post('saveSettings', { engineDefaults: value || {} });
            return;
        }
        if (level === 'collection') {
            var c = collection();
            if (!c) return;
            c.settings = value;
            post('saveCollectionSettings', { id: c.id, settings: value || {} });
            return;
        }
        var t = task();
        if (!t) return;
        if (level === 'task') t.settings = value;
        else {
            var s = step();
            if (!s) return;
            s.settings = value;
        }
        saveTask(t);
    }

    function title() {
        if (level === 'global') return 'Global';
        var named = level === 'collection' ? collection() : level === 'task' ? task() : step();
        var name = named ? (named.name || named.label || named.action) : '';
        return LEVEL_NAMES[level] + (name ? ' “' + name + '”' : '');
    }

    return { level: level, above: above, own: own, persist: persist, title: title };
}

function floorValue(key) {
    return state.engineFloor ? state.engineFloor[key] : null;
}

// Walks outward-in and keeps the last scope that actually states a value, so the label names the
// NEAREST ancestor that set it — not always the immediate parent.
function inheritedFor(field, chain) {
    var found = { from: 'Default', value: floorValue(field.key) };
    chain.forEach(function (link) {
        if (link.settings && link.settings[field.key] != null) {
            found = { from: link.name, value: link.settings[field.key] };
        }
    });
    return found;
}

function describe(field, value) {
    if (value == null) return '—';
    if (field.type === 'bool') return value ? 'On' : 'Off';
    if (field.type === 'retry') {
        var attempts = value.maxAttempts != null ? value.maxAttempts : 1;
        return attempts <= 1
            ? 'No retry'
            : attempts + ' attempts, ' + (value.delayMs != null ? value.delayMs : 2000) + 'ms apart';
    }
    if (field.type === 'number') return value + (field.unit ? ' ' + field.unit : '');
    return String(value);
}

function controlHtml(field, value) {
    var name = esc(field.label);
    if (field.type === 'bool') {
        return '<label class="inline"><input type="checkbox" data-input="' + field.key + '"' +
            (value ? ' checked' : '') + ' aria-label="' + name + '" /> ' +
            (value ? 'on' : 'off') + '</label>';
    }
    if (field.type === 'number') {
        return '<input type="number" data-input="' + field.key + '" value="' + esc(value) +
            '" min="' + (field.min || 0) + '" aria-label="' + name + '" />' +
            (field.unit ? '<span class="unit">' + esc(field.unit) + '</span>' : '');
    }
    if (field.type === 'select') {
        return '<select data-input="' + field.key + '" aria-label="' + name + '">' +
            field.options.map(function (o) {
                return '<option value="' + o + '"' + (o === value ? ' selected' : '') + '>' + o + '</option>';
            }).join('') + '</select>';
    }
    // retry: two numbers that are overridden together, because "3 attempts" and "how long
    // between them" are one decision, not two.
    var v = value || {};
    return '<input type="number" data-input="retry.maxAttempts" min="1" value="' +
        esc(v.maxAttempts != null ? v.maxAttempts : 1) + '" aria-label="Attempts, including the first" />' +
        '<span class="unit">attempts</span>' +
        '<input type="number" data-input="retry.delayMs" min="0" value="' +
        esc(v.delayMs != null ? v.delayMs : 2000) + '" aria-label="Delay between attempts in milliseconds" />' +
        '<span class="unit">ms apart</span>';
}

export function openScopedSettings(level, ids) {
    var ctx = scopeContext(level, ids);
    var form = openFormModal(
        'Settings — ' + ctx.title(),
        'Anything you have not overridden is inherited from the scope named beside it.');
    draw();

    function fields() {
        return FIELDS.filter(function (f) { return !f.levels || f.levels.indexOf(level) >= 0; });
    }

    function draw() {
        var own = ctx.own() || {};
        var chain = ctx.above();
        form.innerHTML = fields().map(function (field) {
            var overridden = own[field.key] != null;
            var inherited = inheritedFor(field, chain);
            return '<div class="field settings-field" data-key="' + field.key + '">' +
                '<span>' + esc(field.label) + '</span>' +
                '<div class="settings-value ' + (overridden ? 'overridden' : 'inherited') + '">' +
                (overridden
                    ? controlHtml(field, own[field.key]) +
                      '<button class="mini" data-op="reset" aria-label="Reset ' + esc(field.label) +
                      ' to the inherited value">↺ Reset</button>'
                    : '<span class="inherited-value">' + esc(describe(field, inherited.value)) +
                      ' <em>(from ' + esc(inherited.from) + ')</em></span>' +
                      '<button class="mini" data-op="override" aria-label="Override ' + esc(field.label) +
                      ' at this scope">Override</button>') +
                '</div></div>';
        }).join('') +
            '<p class="scope-note">Global → collection → task → step. The nearest scope that sets a ' +
            'value wins, and changes apply from the next run.</p>';
        wire();
    }

    function mutate(change) {
        var next = Object.assign({}, ctx.own() || {});
        change(next);
        Object.keys(next).forEach(function (k) { if (next[k] == null) delete next[k]; });
        ctx.persist(next);
        draw();
    }

    function wire() {
        form.querySelectorAll('[data-op="override"]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var key = btn.closest('.settings-field').getAttribute('data-key');
                var field = fields().filter(function (f) { return f.key === key; })[0];
                // Seed the override with whatever was already in effect, so clicking Override
                // never changes behaviour by itself — it only takes ownership of the value.
                var seed = inheritedFor(field, ctx.above()).value;
                if (field.type === 'retry') {
                    seed = {
                        maxAttempts: seed && seed.maxAttempts != null ? seed.maxAttempts : 1,
                        delayMs: seed && seed.delayMs != null ? seed.delayMs : 2000,
                    };
                }
                mutate(function (next) { next[key] = seed == null ? '' : seed; });
            });
        });

        form.querySelectorAll('[data-op="reset"]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var key = btn.closest('.settings-field').getAttribute('data-key');
                mutate(function (next) { delete next[key]; });
            });
        });

        form.querySelectorAll('[data-input]').forEach(function (input) {
            input.addEventListener('change', function () {
                var path = input.getAttribute('data-input').split('.');
                var raw = input.type === 'checkbox' ? input.checked
                    : input.type === 'number' ? parseInt(input.value, 10)
                    : input.value;
                if (input.type === 'number' && isNaN(raw)) return;
                mutate(function (next) {
                    if (path.length === 1) { next[path[0]] = raw; return; }
                    next[path[0]] = Object.assign({}, next[path[0]] || {});
                    next[path[0]][path[1]] = raw;
                });
            });
        });
    }
}
