// The Schedule tab: what runs on its own, and what starts it.
//
// Two rules shape this module.
//
// First, nothing here works out WHEN something fires. Every due time, every "next in 3h", and
// every chain preview is computed by the host with the same TriggerEvaluator the runner's `tick`
// obeys, and pushed down already resolved. A sidebar that did its own cron arithmetic could
// disagree with the thing that actually runs, and a schedule preview that lies is worse than none.
//
// Second, a schedule is assembled from pickers, not typed. "Every weekday at 09:00" is a choice
// plus a time, and it COMPILES to `0 9 * * 1-5`; the expression is shown, read-only, so the
// standard stays visible and importable, but nobody has to know cron to schedule a nightly run.
// Custom cron is still there for people who do — one entry in the same picker.

import { $, esc, post, state, announce } from './core.js';
import { openFormModal, openConfirmModal } from './modal.js';

// The shapes offered, in the order a first-time user is likely to want them. The first five
// compile to cron; the rest map to the other trigger kinds one-for-one.
var WHEN = [
    { value: 'daily', label: 'Every day at…' },
    { value: 'weekdays', label: 'Every weekday at…' },
    { value: 'weekly', label: 'Every week on…' },
    { value: 'hourly', label: 'Every hour at…' },
    { value: 'minutes', label: 'Every few minutes' },
    { value: 'once', label: 'Once, at a set time' },
    { value: 'after', label: 'After another schedule finishes' },
    { value: 'cron', label: 'Custom cron expression' },
];

var DAY_NAMES = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

var OUTCOMES = [
    { value: 'succeeded', label: 'it succeeds' },
    { value: 'failed', label: 'it fails' },
    { value: 'completed', label: 'it finishes, either way' },
];

// The entry being edited, kept while the dialog is open so a save the host refuses can be
// reopened with the same values and the reason why — nothing the user typed is lost. `submitted`
// is what ties a refusal to the editor: a pause or a delete posts through the same host action,
// and a refusal there must not pop open an editor nobody asked for.
var editing = null;
var submitted = false;

// ---- cron: compile out of the picker, and read back in -------------------------------------

function hourMinute(time) {
    var parts = String(time || '09:00').split(':');
    var h = parseInt(parts[0], 10), m = parseInt(parts[1], 10);
    return { h: isNaN(h) ? 9 : Math.min(23, Math.max(0, h)), m: isNaN(m) ? 0 : Math.min(59, Math.max(0, m)) };
}

// Takes ONE trigger's draft, not the whole entry — an entry can carry several, each compiling to
// its own expression.
export function cronFor(trigger) {
    var at = hourMinute(trigger.time);
    if (trigger.when === 'daily') return at.m + ' ' + at.h + ' * * *';
    if (trigger.when === 'weekdays') return at.m + ' ' + at.h + ' * * 1-5';
    if (trigger.when === 'weekly')
        return at.m + ' ' + at.h + ' * * ' + (trigger.dayOfWeek == null ? 1 : trigger.dayOfWeek);
    if (trigger.when === 'hourly') return (trigger.minute == null ? 0 : trigger.minute) + ' * * * *';
    return String(trigger.cron || '').trim();
}

// The inverse, so editing an entry reopens on the shape it was built with rather than dropping
// everyone into the raw expression. Anything these patterns don't recognise — including a cron
// expression that came in from the CLI or a hand-edited schedule.json — is honestly shown as
// custom rather than approximated into the nearest picker.
function shapeOfCron(expression) {
    var text = String(expression || '').trim();
    var pad = function (n) { return (n < 10 ? '0' : '') + n; };
    var daily = /^(\d{1,2}) (\d{1,2}) \* \* \*$/.exec(text);
    if (daily) return { when: 'daily', time: pad(+daily[2]) + ':' + pad(+daily[1]) };
    var weekdays = /^(\d{1,2}) (\d{1,2}) \* \* 1-5$/.exec(text);
    if (weekdays) return { when: 'weekdays', time: pad(+weekdays[2]) + ':' + pad(+weekdays[1]) };
    var weekly = /^(\d{1,2}) (\d{1,2}) \* \* ([0-6])$/.exec(text);
    if (weekly) return { when: 'weekly', time: pad(+weekly[2]) + ':' + pad(+weekly[1]), dayOfWeek: +weekly[3] };
    var hourly = /^(\d{1,2}) \* \* \* \*$/.exec(text);
    if (hourly) return { when: 'hourly', minute: +hourly[1] };
    return { when: 'cron', cron: text };
}

// ---- local time <-> the UTC instants the model stores ---------------------------------------

function toLocalInput(iso) {
    if (!iso) return '';
    var d = new Date(iso);
    if (isNaN(d.getTime())) return '';
    var pad = function (n) { return (n < 10 ? '0' : '') + n; };
    return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) +
        'T' + pad(d.getHours()) + ':' + pad(d.getMinutes());
}

function fromLocalInput(value) {
    if (!value) return null;
    var d = new Date(value);          // a bare 'YYYY-MM-DDTHH:MM' is read as local time
    return isNaN(d.getTime()) ? null : d.toISOString();
}

function whenText(iso) {
    if (!iso) return '';
    var d = new Date(iso);
    if (isNaN(d.getTime())) return '';
    var today = new Date();
    var time = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    return d.toDateString() === today.toDateString() ? time : d.toLocaleDateString() + ' ' + time;
}

// ---- draft <-> entry -------------------------------------------------------------------------

// An entry may be started by several things at once — "every weekday at 09:00 AND once the ingest
// has finished". The model has always been a list and the evaluator has always taken the soonest
// firing across it; the editor is what used to write exactly one.
var MAX_TRIGGERS = 8;

function blankTrigger() {
    return {
        when: 'daily',
        time: '09:00',
        dayOfWeek: 1,
        minute: 0,
        everyMinutes: 15,
        onceLocal: '',
        afterEntryId: '',
        requiredOutcome: 'succeeded',
        cron: '',
        timeZoneId: '',
        catchUp: false,
        // Kept so an interval that is not being changed stays on its original grid instead of
        // silently re-anchoring to whenever the entry was last edited.
        anchorUtc: null,
    };
}

function triggerDraftFrom(trigger) {
    var t = blankTrigger();
    t.timeZoneId = trigger.timeZoneId || '';
    t.catchUp = trigger.catchUp === 'runOnceImmediately';
    t.anchorUtc = trigger.anchorUtc || null;

    if (trigger.kind === 'cron') Object.assign(t, shapeOfCron(trigger.cronExpression));
    else if (trigger.kind === 'interval') {
        t.when = 'minutes';
        t.everyMinutes = Math.max(1, Math.round((trigger.intervalSeconds || 900) / 60));
    } else if (trigger.kind === 'oneShot') {
        t.when = 'once';
        t.onceLocal = toLocalInput(trigger.fireAtUtc);
    } else if (trigger.kind === 'afterEntry') {
        t.when = 'after';
        t.afterEntryId = trigger.afterEntryId || '';
        t.requiredOutcome = trigger.requiredOutcome || 'succeeded';
    }
    return t;
}

function draftFrom(entry) {
    var triggers = (entry.triggers || []).map(triggerDraftFrom);
    return {
        id: entry.id,
        name: entry.name || '',
        enabled: entry.enabled !== false,
        target: entry.target || 'collection',
        targetId: entry.targetId || '',
        // A schedule with no trigger at all runs only by hand, which is not a thing anyone opens
        // this dialog to build — so a new entry starts with one.
        triggers: triggers.length ? triggers : [blankTrigger()],
        nameTouched: !!entry.id,
    };
}

function triggerFrom(t) {
    if (t.when === 'minutes') {
        return {
            kind: 'interval',
            enabled: true,
            intervalSeconds: Math.max(1, parseInt(t.everyMinutes, 10) || 1) * 60,
            anchorUtc: t.anchorUtc || new Date().toISOString(),
            catchUp: t.catchUp ? 'runOnceImmediately' : 'skip',
        };
    }
    if (t.when === 'once') {
        return { kind: 'oneShot', enabled: true, fireAtUtc: fromLocalInput(t.onceLocal), catchUp: 'skip' };
    }
    if (t.when === 'after') {
        return {
            kind: 'afterEntry',
            enabled: true,
            afterEntryId: t.afterEntryId,
            requiredOutcome: t.requiredOutcome,
            catchUp: 'skip',
        };
    }
    return {
        kind: 'cron',
        enabled: true,
        cronExpression: cronFor(t),
        timeZoneId: t.timeZoneId || null,
        catchUp: t.catchUp ? 'runOnceImmediately' : 'skip',
    };
}

function entryFrom(draft) {
    var entry = {
        name: (draft.name || '').trim(),
        enabled: !!draft.enabled,
        target: draft.target,
        targetId: draft.targetId,
        triggers: draft.triggers.map(triggerFrom),
    };
    // A new entry deliberately carries NO id, so the model mints one. Sending an empty string
    // instead would give every new schedule the same blank id, and the second one saved would
    // overwrite the first rather than joining it.
    if (draft.id) entry.id = draft.id;
    return entry;
}

// ---- the list --------------------------------------------------------------------------------

function entryById(id) {
    return (state.schedule || []).filter(function (e) { return e.id === id; })[0] || null;
}

// Glyph, word and colour — never colour alone — for what starts an entry. With several triggers
// the clock wins the glyph: "it runs on its own" is the thing worth seeing at a glance, and a
// chain is only half the story once a time is involved too.
function kindGlyph(entry) {
    if (entry.enabled === false) return '⏸';
    var kinds = (entry.triggers || []).map(function (t) { return t.kind; });
    if (!kinds.length) return '▫';
    if (kinds.some(function (k) { return k !== 'afterEntry' && k !== 'manual'; })) return '⏰';
    if (kinds.indexOf('afterEntry') >= 0) return '⛓';
    return '▫';
}

// Every trigger, not just the first. "or" rather than "and" because the evaluator takes the
// SOONEST firing across them — any one of them is enough to start the run.
function whatStartsIt(entry) {
    var described = (entry.triggers || []).map(describeTrigger);
    return described.length ? described.join(', or ') : 'only when started by hand';
}

function describeTrigger(trigger) {
    if (trigger.kind === 'cron') {
        var shape = shapeOfCron(trigger.cronExpression);
        var label = shape.when === 'daily' ? 'every day at ' + shape.time
            : shape.when === 'weekdays' ? 'every weekday at ' + shape.time
            : shape.when === 'weekly' ? 'every ' + DAY_NAMES[shape.dayOfWeek] + ' at ' + shape.time
            : shape.when === 'hourly' ? 'every hour at :' + (shape.minute < 10 ? '0' : '') + shape.minute
            : 'cron ' + shape.cron;
        return label + (trigger.timeZoneId ? ' (' + trigger.timeZoneId + ')' : '');
    }
    if (trigger.kind === 'interval') {
        var minutes = Math.max(1, Math.round((trigger.intervalSeconds || 60) / 60));
        return 'every ' + minutes + ' minute' + (minutes === 1 ? '' : 's');
    }
    if (trigger.kind === 'oneShot') return 'once, at ' + whenText(trigger.fireAtUtc);
    if (trigger.kind === 'afterEntry') {
        var upstream = entryById(trigger.afterEntryId);
        var outcome = OUTCOMES.filter(function (o) { return o.value === trigger.requiredOutcome; })[0];
        return 'after ' + (upstream ? '“' + upstream.name + '”' : 'a deleted schedule') +
            ' ' + ((outcome && outcome.label) || 'succeeds');
    }
    return 'only when started by hand';
}

// Which control had focus, in a form that survives the panel being rebuilt from scratch.
function focusMark(view) {
    var el = document.activeElement;
    if (!el || !view.contains(el)) return null;
    return el.id
        ? '#' + el.id
        : '[data-op="' + el.getAttribute('data-op') + '"][data-entry="' + el.getAttribute('data-entry') + '"]';
}

export function renderSchedule() {
    var view = $('view-schedule');
    if (!view) return;

    // Replacing the panel's innerHTML drops DOM focus, which for a keyboard user means landing
    // back at the top of the document after every pause, save and delete. Focus is handed to the
    // replacement of whatever had it.
    var mark = focusMark(view);
    var entries = state.schedule || [];
    var head =
        '<div class="section-head"><h2 class="section-label">Schedule</h2>' +
        '<span class="node-btns">' +
        '<button class="mini" id="btn-new-schedule" aria-label="Add a schedule"' +
        ' data-tooltip="Run a collection or task on a clock, or after another schedule">+ add schedule</button>' +
        '<button class="mini" id="btn-refresh-schedule" aria-label="Re-read the schedule from disk"' +
        ' data-tooltip="Re-read from disk">⟳</button>' +
        '</span></div>';

    var note =
        '<p class="scope-note">Schedules are kept apart from the collections themselves, and are ' +
        'run by <code>automata-runner tick</code>. Register it once with ' +
        '<code>automata-runner install</code> and Windows will call it every few minutes; until ' +
        'then, nothing here fires on its own. A browser needs a desktop to draw on, so the ' +
        'registered task runs only while you are logged on.</p>';

    if (!entries.length) {
        view.innerHTML = head +
            '<p class="empty-state">Nothing is scheduled. A schedule runs a collection at a set ' +
            'time, or once another collection has finished.</p>' + note;
    } else {
        view.innerHTML = head +
            '<div id="schedule-list" role="list" aria-label="Schedules">' +
            entries.map(function (e) {
                var missing = !e.targetName;
                var last = e.lastOutcome ? ' · last run ' + e.lastOutcome : '';
                var chain = (e.chain || []).map(function (id) {
                    var d = entryById(id);
                    return d ? d.name : null;
                }).filter(Boolean);
                return '<div class="sched-row' + (e.enabled === false ? ' off' : '') +
                    (missing ? ' broken' : '') + '" role="listitem" data-entry="' + esc(e.id) + '">' +
                    '<span class="status" role="img" aria-label="' + esc(whatStartsIt(e)) + '">' +
                    kindGlyph(e) + '</span>' +
                    '<span class="name">' + esc(e.name) + '</span>' +
                    '<span class="sched-meta">' + esc(e.reason) + esc(last) + '</span>' +
                    '<span class="node-btns">' +
                    miniOp(e.id, 'run', '▶', 'Run “' + e.name + '” now') +
                    miniOp(e.id, 'edit', '✎', 'Edit “' + e.name + '”') +
                    miniOp(e.id, 'toggle', e.enabled === false ? 'Resume' : 'Pause',
                        (e.enabled === false ? 'Resume' : 'Pause') + ' “' + e.name + '”') +
                    miniOp(e.id, 'delete', '🗑', 'Delete “' + e.name + '”') +
                    '</span></div>' +
                    '<div class="sched-detail">' +
                    (missing
                        ? '<b>Its ' + esc(e.target) + ' has been deleted</b> — nothing will run until you point it somewhere.'
                        : esc(e.target) + ' “' + esc(e.targetName) + '” · ' + esc(whatStartsIt(e))) +
                    (chain.length
                        ? '<span class="sched-chain">then ' + chain.map(esc).join(' → ') + '</span>'
                        : '') +
                    '</div>';
            }).join('') +
            '</div>' + note;
    }

    var add = $('btn-new-schedule');
    if (add) add.addEventListener('click', function () { openScheduleEditor(null); });
    var refresh = $('btn-refresh-schedule');
    if (refresh) refresh.addEventListener('click', function () { post('getSchedule'); });

    view.querySelectorAll('.sched-row [data-op]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            rowOp(btn.getAttribute('data-entry'), btn.getAttribute('data-op'));
        });
    });

    // The row a delete removed has no replacement, so fall back to the add button rather than
    // dropping focus entirely.
    if (mark) {
        var again = view.querySelector(mark) || $('btn-new-schedule');
        if (again) again.focus();
    }
}

function miniOp(entryId, op, glyph, label) {
    return '<button class="mini" data-op="' + op + '" data-entry="' + esc(entryId) +
        '" aria-label="' + esc(label) + '" data-tooltip="' + esc(label) + '">' + esc(glyph) + '</button>';
}

function rowOp(entryId, op) {
    var entry = entryById(entryId);
    if (!entry) return;
    if (op === 'edit') { openScheduleEditor(entry); return; }
    if (op === 'toggle') {
        // Enabled is the only field this touches, so the entry goes back exactly as it came
        // down — a pause must not quietly rewrite a trigger the host would then re-evaluate.
        post('saveScheduleEntry', {
            entry: {
                id: entry.id, name: entry.name, enabled: entry.enabled === false,
                target: entry.target, targetId: entry.targetId, triggers: entry.triggers,
            },
        });
        return;
    }
    if (op === 'run') {
        // Runs it here and now, in this window's browser pane — the same path the tree's ▶ takes.
        // The schedule is untouched: this is a manual run, not a firing.
        if (entry.target === 'task') post('runTask', { taskId: entry.targetId });
        else post('runCollection', { collectionId: entry.targetId });
        announce('Running ' + entry.name + ' now.');
        return;
    }
    if (op === 'delete') {
        openConfirmModal('Delete schedule',
            'Stop running “' + entry.name + '” on a schedule? The ' + entry.target +
            ' itself is not deleted.', 'Delete',
            function () { post('deleteScheduleEntry', { id: entry.id }); });
    }
}

// ---- the editor ------------------------------------------------------------------------------

function targetChoices() {
    var collections = [], tasks = [];
    (state.collections || []).forEach(function (c) {
        collections.push({ value: 'collection:' + c.id, label: c.name });
        (c.tasks || []).forEach(function (t) {
            tasks.push({ value: 'task:' + t.id, label: c.name + ' › ' + t.name });
        });
    });
    return { collections: collections, tasks: tasks };
}

function optionsHtml(items, selected) {
    return items.map(function (it) {
        return '<option value="' + esc(it.value) + '"' + (it.value === selected ? ' selected' : '') +
            '>' + esc(it.label) + '</option>';
    }).join('');
}

export function openScheduleEditor(entry, error) {
    var draft = editing && error ? editing : draftFrom(entry || {});
    editing = draft;
    var form = openFormModal(
        entry || draft.id ? 'Edit schedule' : 'New schedule',
        'Pick what to run and what starts it. Nothing runs until ' +
        'automata-runner tick is registered with Windows.',
        { okText: 'Save', cancel: true, onCommit: submit });
    draw();

    function field(label, controlHtml, hint) {
        return '<div class="field sched-field">' +
            '<span>' + esc(label) + '</span>' +
            '<div class="sched-control">' + controlHtml +
            (hint ? '<span class="unit">' + esc(hint) + '</span>' : '') +
            '</div></div>';
    }

    // The fields one trigger's chosen shape needs. `index` scopes every control to its own
    // trigger: data-trigger is what tells the change handler which one to mutate, and the
    // accessible names carry the number too, so a form with three time pickers does not offer a
    // screen reader three controls called "Time of day".
    function whenFields(t, index) {
        var scope = ' data-trigger="' + index + '"';
        var of = ' (trigger ' + (index + 1) + ')';

        if (t.when === 'minutes') {
            return field('Every', '<input type="number" min="1" max="10080" data-input="everyMinutes"' +
                scope + ' value="' + esc(t.everyMinutes) + '" aria-label="Minutes between runs' + of + '" />',
                'minutes');
        }
        if (t.when === 'once') {
            return field('At', '<input type="datetime-local" data-input="onceLocal"' + scope +
                ' value="' + esc(t.onceLocal) + '" aria-label="Date and time to run once at' + of + '" />',
                'your local time');
        }
        if (t.when === 'after') {
            var others = (state.schedule || [])
                .filter(function (e) { return e.id !== draft.id; })
                .map(function (e) { return { value: e.id, label: e.name }; });
            if (!others.length) {
                return '<div class="diagnostics warn"><ul><li>There is no other schedule to follow ' +
                    'yet. Add one with a time first, then chain this one after it.</li></ul></div>';
            }
            return field('After', '<select data-input="afterEntryId"' + scope +
                ' aria-label="The schedule this one follows' + of + '">' +
                optionsHtml(others, t.afterEntryId || others[0].value) + '</select>') +
                field('When', '<select data-input="requiredOutcome"' + scope +
                    ' aria-label="Which outcome starts this' + of + '">' +
                    optionsHtml(OUTCOMES, t.requiredOutcome) + '</select>');
        }
        if (t.when === 'cron') {
            return field('Expression', '<input type="text" data-input="cron"' + scope +
                ' value="' + esc(t.cron || '') + '" placeholder="0 9 * * 1-5"' +
                ' aria-label="Cron expression' + of + '" />',
                'minute hour day-of-month month day-of-week');
        }

        var rows = '';
        if (t.when === 'hourly') {
            rows += field('At', '<input type="number" min="0" max="59" data-input="minute"' + scope +
                ' value="' + esc(t.minute) + '" aria-label="Minutes past the hour' + of + '" />',
                'minutes past the hour');
        } else {
            if (t.when === 'weekly') {
                rows += field('On', '<select data-input="dayOfWeek"' + scope +
                    ' aria-label="Day of the week' + of + '">' +
                    optionsHtml(DAY_NAMES.map(function (d, i) { return { value: String(i), label: d }; }),
                        String(t.dayOfWeek)) + '</select>');
            }
            rows += field('At', '<input type="time" data-input="time"' + scope +
                ' value="' + esc(t.time) + '" aria-label="Time of day' + of + '" />');
        }
        return rows;
    }

    // One trigger, boxed and numbered. Numbered only when there is more than one: a single
    // trigger is the overwhelmingly common case and should not be dressed up as a list.
    function triggerBlock(t, index) {
        var scope = ' data-trigger="' + index + '"';
        var of = ' (trigger ' + (index + 1) + ')';
        var many = draft.triggers.length > 1;
        var isClock = t.when !== 'after';
        var usesZone = t.when !== 'after' && t.when !== 'minutes' && t.when !== 'once';
        var zones = [{ value: '', label: 'This machine (' + (state.localTimeZoneId || 'local') + ')' }]
            .concat((state.timeZones || []).map(function (z) { return { value: z.id, label: z.label }; }));

        return '<div class="trigger-block"' + scope + '>' +
            (many
                ? '<div class="section-head"><h3 class="section-label">Trigger ' + (index + 1) +
                  ' of ' + draft.triggers.length + '</h3>' +
                  '<button class="mini" data-op="remove-trigger"' + scope +
                  ' aria-label="Remove trigger ' + (index + 1) +
                  '" data-tooltip="Remove this trigger">✕</button></div>'
                : '') +
            field('Starts', '<select data-input="when"' + scope +
                ' aria-label="What starts this schedule' + of + '">' +
                optionsHtml(WHEN, t.when) + '</select>') +
            whenFields(t, index) +
            (usesZone
                ? field('Time zone', '<select data-input="timeZoneId"' + scope +
                    ' aria-label="Time zone the time is read in' + of + '">' +
                    optionsHtml(zones, t.timeZoneId) + '</select>')
                : '') +
            (isClock
                ? field('If missed', '<label class="inline"><input type="checkbox" data-input="catchUp"' +
                    scope + (t.catchUp ? ' checked' : '') +
                    ' aria-label="Run once if a firing was missed' + of + '" /> ' +
                    'run once as soon as possible</label>',
                    'otherwise a firing missed while nothing was running is skipped')
                : '') +
            // The compiled expression is shown, never hidden: it is what gets stored, what the CLI
            // prints, and what travels if the schedule is moved to another machine.
            (isClock && t.when !== 'minutes' && t.when !== 'once'
                ? '<p class="scope-note">Stored as cron <code id="sched-cron-note-' + index + '">' +
                  esc(cronFor(t) || '—') + '</code>.</p>'
                : '') +
            '</div>';
    }

    // A shape's own fields get a workable default the moment it is chosen, so a picker never
    // renders a control whose displayed value disagrees with what a Save would send.
    function normalize() {
        draft.triggers.forEach(function (t) {
            if (t.when === 'after' && !t.afterEntryId) {
                var first = (state.schedule || []).filter(function (e) { return e.id !== draft.id; })[0];
                t.afterEntryId = first ? first.id : '';
            }
            if (t.when === 'once' && !t.onceLocal) {
                t.onceLocal = toLocalInput(new Date(Date.now() + 3600000).toISOString());
            }
        });
    }

    function draw() {
        normalize();
        var focused = document.activeElement && form.contains(document.activeElement)
            ? document.activeElement : null;
        var focusedKey = focused ? focused.getAttribute('data-input') : null;
        var focusedTrigger = focused ? focused.getAttribute('data-trigger') : null;
        var choices = targetChoices();
        var selectedTarget = draft.targetId ? draft.target + ':' + draft.targetId : '';
        var atCap = draft.triggers.length >= MAX_TRIGGERS;

        form.innerHTML =
            (error ? '<div class="diagnostics" role="alert"><ul><li>' + esc(error) + '</li></ul></div>' : '') +
            field('Run', '<select data-input="target" aria-label="What this schedule runs">' +
                '<option value=""' + (selectedTarget ? '' : ' selected') + '>— pick one —</option>' +
                (choices.collections.length
                    ? '<optgroup label="Collections">' + optionsHtml(choices.collections, selectedTarget) + '</optgroup>'
                    : '') +
                (choices.tasks.length
                    ? '<optgroup label="Tasks">' + optionsHtml(choices.tasks, selectedTarget) + '</optgroup>'
                    : '') +
                '</select>') +
            field('Called', '<input type="text" data-input="name" value="' + esc(draft.name) +
                '" aria-label="Name for this schedule" />') +
            draft.triggers.map(triggerBlock).join('') +
            '<div class="row trigger-add">' +
            (atCap
                ? '<span class="unit">' + MAX_TRIGGERS + ' triggers is the limit — plenty for ' +
                  '"whichever comes first", and past that a schedule is easier to read as two.</span>'
                : '<button class="mini" data-op="add-trigger" aria-label="Add another trigger"' +
                  ' data-tooltip="Start this on a second clock, or after another schedule too">' +
                  '+ add trigger</button>') +
            '</div>' +
            field('Enabled', '<label class="inline"><input type="checkbox" data-input="enabled"' +
                (draft.enabled ? ' checked' : '') + ' aria-label="Enabled" /> ' +
                '<span id="sched-enabled-word">' + (draft.enabled ? 'on' : 'paused') + '</span></label>') +
            (draft.triggers.length > 1
                ? '<p class="scope-note">Any one of these starts the run — whichever comes first. ' +
                  'They are not steps and they do not wait for each other.</p>'
                : '');
        wire();

        // Focus belongs inside the dialog: on the control that had it before a reshape, and on the
        // first control when the dialog has just opened. Without this a redraw would drop a
        // keyboard user at the top of the document and leave the Tab trap with nothing to trap.
        // The trigger index is part of the identity — three "when" selects are not interchangeable.
        var selector = focusedKey
            ? '[data-input="' + focusedKey + '"]' +
              (focusedTrigger == null ? '' : '[data-trigger="' + focusedTrigger + '"]')
            : null;
        var back = (selector && form.querySelector(selector))
            || form.querySelector('select, input, textarea');
        if (back) back.focus();
    }

    // Only two choices reshape the form — what to run, and what starts a trigger. Everything else
    // edits a control that is already on screen, so it updates the draft in place and leaves the
    // DOM alone: rebuilding the form under a half-typed field is how you lose a caret.
    function wire() {
        form.querySelectorAll('[data-input]').forEach(function (input) {
            var key = input.getAttribute('data-input');
            var at = input.getAttribute('data-trigger');
            var t = at == null ? null : draft.triggers[Number(at)];
            var live = input.type === 'text' || input.type === 'number'
                || input.type === 'time' || input.type === 'datetime-local';

            input.addEventListener(live ? 'input' : 'change', function () {
                if (key === 'target') {
                    var parts = String(input.value || '').split(':');
                    draft.target = parts[0] || 'collection';
                    draft.targetId = parts[1] || '';
                    // The name follows the target until someone types their own, so the common
                    // case needs no naming step at all.
                    if (!draft.nameTouched) {
                        var chosen = input.options[input.selectedIndex];
                        draft.name = draft.targetId ? chosen.textContent : '';
                    }
                    draw();
                    return;
                }
                if (key === 'name') {
                    draft.name = input.value;
                    draft.nameTouched = true;
                    return;
                }
                if (key === 'enabled') {
                    draft.enabled = input.checked;
                    refresh();
                    return;
                }
                if (!t) return;

                if (input.type === 'checkbox') t[key] = input.checked;
                else if (input.type === 'number' || key === 'dayOfWeek') {
                    var n = parseInt(input.value, 10);
                    if (!isNaN(n)) t[key] = n;
                } else {
                    t[key] = input.value;
                }
                // Changing the shape or the cadence re-anchors an interval on purpose: the old
                // anchor belonged to a grid that no longer exists.
                if (key === 'when' || key === 'everyMinutes') t.anchorUtc = null;
                if (key === 'when') { draw(); return; }
                refresh();
            });
        });

        var add = form.querySelector('[data-op="add-trigger"]');
        if (add) {
            add.addEventListener('click', function () {
                draft.triggers.push(blankTrigger());
                draw();
            });
        }
        form.querySelectorAll('[data-op="remove-trigger"]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                // The last one is never removable — an entry with no trigger runs only by hand,
                // which is a different thing from a schedule and not what this dialog builds.
                if (draft.triggers.length <= 1) return;
                draft.triggers.splice(Number(btn.getAttribute('data-trigger')), 1);
                draw();
            });
        });
    }

    // The parts of the form that describe other parts of the form, kept truthful without a
    // rebuild.
    function refresh() {
        draft.triggers.forEach(function (t, index) {
            var note = $('sched-cron-note-' + index);
            if (note) note.textContent = cronFor(t) || '—';
        });
        var word = $('sched-enabled-word');
        if (word) word.textContent = draft.enabled ? 'on' : 'paused';
    }

    function submit() {
        var pending = entryFrom(draft);
        editing = draft;
        submitted = true;
        post('saveScheduleEntry', { entry: pending });
    }
}

// Called by the host push. A save the host refused reopens the editor with the same values and
// the reason, so nothing typed is lost; a save it accepted clears the draft.
export function onSchedulePushed(error) {
    var wasSubmitted = submitted;
    submitted = false;
    if (error && wasSubmitted && editing) openScheduleEditor(null, error);
    else if (!error) editing = null;
}

// The chip a tree row shows when something schedules it — one glance tells you a collection runs
// on its own, without opening another tab. Empty for anything unscheduled, so a store with no
// schedule renders exactly as it did before.
export function scheduleChipFor(target, targetId) {
    var entries = (state.schedule || []).filter(function (e) {
        return e.target === target && e.targetId === targetId;
    });
    if (!entries.length) return '';
    return entries.map(function (e) {
        var label = e.name + ' — ' + whatStartsIt(e) +
            (e.enabled === false ? ' (paused)' : '') + '; ' + e.reason;
        return ' <span class="chip sched' + (e.enabled === false ? ' off' : '') +
            '" role="img" aria-label="' + esc('Scheduled: ' + label) + '"' +
            ' data-tooltip="' + esc(label) + '">' + kindGlyph(e) + '</span>';
    }).join('');
}
