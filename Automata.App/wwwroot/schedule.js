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

export function cronFor(draft) {
    var t = hourMinute(draft.time);
    if (draft.when === 'daily') return t.m + ' ' + t.h + ' * * *';
    if (draft.when === 'weekdays') return t.m + ' ' + t.h + ' * * 1-5';
    if (draft.when === 'weekly') return t.m + ' ' + t.h + ' * * ' + (draft.dayOfWeek == null ? 1 : draft.dayOfWeek);
    if (draft.when === 'hourly') return (draft.minute == null ? 0 : draft.minute) + ' * * * *';
    return String(draft.cron || '').trim();
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

function primaryTrigger(entry) {
    return (entry.triggers || [])[0] || { kind: 'manual' };
}

function draftFrom(entry) {
    var trigger = primaryTrigger(entry);
    var draft = {
        id: entry.id,
        name: entry.name || '',
        enabled: entry.enabled !== false,
        target: entry.target || 'collection',
        targetId: entry.targetId || '',
        when: 'daily',
        time: '09:00',
        dayOfWeek: 1,
        minute: 0,
        everyMinutes: 15,
        onceLocal: '',
        afterEntryId: '',
        requiredOutcome: 'succeeded',
        timeZoneId: trigger.timeZoneId || '',
        catchUp: trigger.catchUp === 'runOnceImmediately',
        // Kept so an interval that is not being changed stays on its original grid instead of
        // silently re-anchoring to whenever the entry was last edited.
        anchorUtc: trigger.anchorUtc || null,
        nameTouched: !!entry.id,
    };

    if (trigger.kind === 'cron') Object.assign(draft, shapeOfCron(trigger.cronExpression));
    else if (trigger.kind === 'interval') {
        draft.when = 'minutes';
        draft.everyMinutes = Math.max(1, Math.round((trigger.intervalSeconds || 900) / 60));
    } else if (trigger.kind === 'oneShot') {
        draft.when = 'once';
        draft.onceLocal = toLocalInput(trigger.fireAtUtc);
    } else if (trigger.kind === 'afterEntry') {
        draft.when = 'after';
        draft.afterEntryId = trigger.afterEntryId || '';
        draft.requiredOutcome = trigger.requiredOutcome || 'succeeded';
    }
    return draft;
}

function triggerFrom(draft) {
    if (draft.when === 'minutes') {
        return {
            kind: 'interval',
            enabled: true,
            intervalSeconds: Math.max(1, parseInt(draft.everyMinutes, 10) || 1) * 60,
            anchorUtc: draft.anchorUtc || new Date().toISOString(),
            catchUp: draft.catchUp ? 'runOnceImmediately' : 'skip',
        };
    }
    if (draft.when === 'once') {
        return { kind: 'oneShot', enabled: true, fireAtUtc: fromLocalInput(draft.onceLocal), catchUp: 'skip' };
    }
    if (draft.when === 'after') {
        return {
            kind: 'afterEntry',
            enabled: true,
            afterEntryId: draft.afterEntryId,
            requiredOutcome: draft.requiredOutcome,
            catchUp: 'skip',
        };
    }
    return {
        kind: 'cron',
        enabled: true,
        cronExpression: cronFor(draft),
        timeZoneId: draft.timeZoneId || null,
        catchUp: draft.catchUp ? 'runOnceImmediately' : 'skip',
    };
}

function entryFrom(draft) {
    var entry = {
        name: (draft.name || '').trim(),
        enabled: !!draft.enabled,
        target: draft.target,
        targetId: draft.targetId,
        triggers: [triggerFrom(draft)],
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

// Glyph, word and colour — never colour alone — for what starts an entry.
function kindGlyph(entry) {
    if (entry.enabled === false) return '⏸';
    var kind = primaryTrigger(entry).kind;
    if (kind === 'afterEntry') return '⛓';
    if (kind === 'manual') return '▫';
    return '⏰';
}

function whatStartsIt(entry) {
    var trigger = primaryTrigger(entry);
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

    function whenFields() {
        if (draft.when === 'minutes') {
            return field('Every', '<input type="number" min="1" max="10080" data-input="everyMinutes"' +
                ' value="' + esc(draft.everyMinutes) + '" aria-label="Minutes between runs" />', 'minutes');
        }
        if (draft.when === 'once') {
            return field('At', '<input type="datetime-local" data-input="onceLocal" value="' +
                esc(draft.onceLocal) + '" aria-label="Date and time to run once at" />', 'your local time');
        }
        if (draft.when === 'after') {
            var others = (state.schedule || [])
                .filter(function (e) { return e.id !== draft.id; })
                .map(function (e) { return { value: e.id, label: e.name }; });
            if (!others.length) {
                return '<div class="diagnostics warn"><ul><li>There is no other schedule to follow ' +
                    'yet. Add one with a time first, then chain this one after it.</li></ul></div>';
            }
            return field('After', '<select data-input="afterEntryId" aria-label="The schedule this one follows">' +
                optionsHtml(others, draft.afterEntryId || others[0].value) + '</select>') +
                field('When', '<select data-input="requiredOutcome" aria-label="Which outcome starts this">' +
                    optionsHtml(OUTCOMES, draft.requiredOutcome) + '</select>');
        }
        if (draft.when === 'cron') {
            return field('Expression', '<input type="text" data-input="cron" value="' + esc(draft.cron || '') +
                '" placeholder="0 9 * * 1-5" aria-label="Cron expression" />',
                'minute hour day-of-month month day-of-week');
        }
        var rows = '';
        if (draft.when === 'hourly') {
            rows += field('At', '<input type="number" min="0" max="59" data-input="minute" value="' +
                esc(draft.minute) + '" aria-label="Minutes past the hour" />', 'minutes past the hour');
        } else {
            if (draft.when === 'weekly') {
                rows += field('On', '<select data-input="dayOfWeek" aria-label="Day of the week">' +
                    optionsHtml(DAY_NAMES.map(function (d, i) { return { value: String(i), label: d }; }),
                        String(draft.dayOfWeek)) + '</select>');
            }
            rows += field('At', '<input type="time" data-input="time" value="' + esc(draft.time) +
                '" aria-label="Time of day" />');
        }
        return rows;
    }

    // A shape's own fields get a workable default the moment it is chosen, so a picker never
    // renders a control whose displayed value disagrees with what a Save would send.
    function normalize() {
        if (draft.when === 'after' && !draft.afterEntryId) {
            var first = (state.schedule || []).filter(function (e) { return e.id !== draft.id; })[0];
            draft.afterEntryId = first ? first.id : '';
        }
        if (draft.when === 'once' && !draft.onceLocal) {
            draft.onceLocal = toLocalInput(new Date(Date.now() + 3600000).toISOString());
        }
    }

    function draw() {
        normalize();
        var focusedKey = document.activeElement && form.contains(document.activeElement)
            ? document.activeElement.getAttribute('data-input') : null;
        var choices = targetChoices();
        var selectedTarget = draft.targetId ? draft.target + ':' + draft.targetId : '';
        var isClock = draft.when !== 'after';
        var usesZone = draft.when !== 'after' && draft.when !== 'minutes' && draft.when !== 'once';
        var zones = [{ value: '', label: 'This machine (' + (state.localTimeZoneId || 'local') + ')' }]
            .concat((state.timeZones || []).map(function (z) { return { value: z.id, label: z.label }; }));

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
            field('Starts', '<select data-input="when" aria-label="What starts this schedule">' +
                optionsHtml(WHEN, draft.when) + '</select>') +
            whenFields() +
            (usesZone
                ? field('Time zone', '<select data-input="timeZoneId" aria-label="Time zone the time is read in">' +
                    optionsHtml(zones, draft.timeZoneId) + '</select>')
                : '') +
            (isClock
                ? field('If missed', '<label class="inline"><input type="checkbox" data-input="catchUp"' +
                    (draft.catchUp ? ' checked' : '') + ' aria-label="Run once if a firing was missed" /> ' +
                    'run once as soon as possible</label>',
                    'otherwise a firing missed while nothing was running is skipped')
                : '') +
            field('Enabled', '<label class="inline"><input type="checkbox" data-input="enabled"' +
                (draft.enabled ? ' checked' : '') + ' aria-label="Enabled" /> ' +
                '<span id="sched-enabled-word">' + (draft.enabled ? 'on' : 'paused') + '</span></label>') +
            // The compiled expression is shown, never hidden: it is what gets stored, what the CLI
            // prints, and what travels if the schedule is moved to another machine.
            (isClock && draft.when !== 'minutes' && draft.when !== 'once'
                ? '<p class="scope-note">Stored as cron <code id="sched-cron-note">' +
                  esc(cronFor(draft) || '—') + '</code>.</p>'
                : '');
        wire();

        // Focus belongs inside the dialog: on the control that had it before a reshape, and on the
        // first control when the dialog has just opened. Without this a redraw would drop a
        // keyboard user at the top of the document and leave the Tab trap with nothing to trap.
        var back = (focusedKey && form.querySelector('[data-input="' + focusedKey + '"]'))
            || form.querySelector('select, input, textarea');
        if (back) back.focus();
    }

    // Only two choices reshape the form — what to run, and what starts it. Everything else edits
    // a control that is already on screen, so it updates the draft in place and leaves the DOM
    // alone: rebuilding the form under a half-typed field is how you lose a caret.
    function wire() {
        form.querySelectorAll('[data-input]').forEach(function (input) {
            var key = input.getAttribute('data-input');
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
                } else if (key === 'name') {
                    draft.name = input.value;
                    draft.nameTouched = true;
                } else if (input.type === 'checkbox') {
                    draft[key] = input.checked;
                } else if (input.type === 'number' || key === 'dayOfWeek') {
                    var n = parseInt(input.value, 10);
                    if (!isNaN(n)) draft[key] = n;
                } else {
                    draft[key] = input.value;
                }
                // Changing the shape or the cadence re-anchors an interval on purpose: the old
                // anchor belonged to a grid that no longer exists.
                if (key === 'when' || key === 'everyMinutes') draft.anchorUtc = null;
                if (key === 'target' || key === 'when') { draw(); return; }
                refresh();
            });
        });
    }

    // The parts of the form that describe other parts of the form, kept truthful without a
    // rebuild.
    function refresh() {
        var note = $('sched-cron-note');
        if (note) note.textContent = cronFor(draft) || '—';
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
