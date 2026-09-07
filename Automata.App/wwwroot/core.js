// Shared foundation for the sidebar modules: the single `state` object, the postMessage bridge
// out to the host, and the pure model lookups over the collection/task/step tree.
//
// Nothing here touches rendering, so every other module can depend on it without a cycle.

// The one source of truth. The host pushes the authoritative tree via window.ssPanel.onState
// after every mutation and the whole UI re-renders from it.
export const state = {
    collections: [],          // [{id, name, description, tasks:[TaskDefinition...]}]

    // The generated "Demos" collection, named by the host. Held so the first-run tutorial can
    // tell a store holding only generated examples from one the user has actually built in.
    demoCollectionId: null,

    // Per-example status from the host (onDemoSurvey): missing | current | stale | edited.
    demos: null,

    // An outstanding harvest pick: {taskId, stepId, mode, index}. The answer arrives from the
    // target pane long after this editor was re-rendered, so it has to say what asked.
    harvestPick: null,
    sel: { collectionId: null, taskId: null, stepId: null },
    recording: false,
    running: false,
    pausedStepId: null,
    stepStatus: {},           // stepId -> running|passed|failed|healed|skipped|paused
    expanded: {},             // collectionId / taskId -> bool
    gapInsert: null,          // {taskId, parentId, index} while a record-at-gap run is armed/in flight
    focusKey: null,           // 'c:<id>' | 't:<id>' | 's:<id>' - the tree's roving-tabindex row

    // Outermost link of the engine settings chain, plus the floor beneath it. Both are pushed by
    // the host (onSettings) rather than mirrored here, so there is one definition of the floor.
    engineDefaults: null,
    engineFloor: null,

    // Datasets available to for-each and write-dataset steps, pushed by the host.
    datasets: [],
    datasetRoot: '',

    // Recent runs, read from the run store rather than remembered here - so runs this window
    // never saw still show up.
    runs: [],
    runRoot: '',


    // The schedule. Every derived value on an entry - when it is next due, why, and what its
    // success sets off - is computed by the host with the same evaluator the runner's tick obeys,
    // so the sidebar never has a second opinion about when something fires.
    schedule: [],
    scheduleError: '',
    timeZones: [],
    localTimeZoneId: '',

    // The last drafted feature, so the preview and the Insert that follows agree.
    flowDraft: null,
};

// Cross-module mutable flags live on an object because an imported binding cannot be assigned
// to from another module. `pendingFocus` means "a keyboard action caused this render, so take
// DOM focus back once the tree is rebuilt".
export const ui = { pendingFocus: false };

export const $ = function (id) { return document.getElementById(id); };
export const treeEl = $('tree'), editorEl = $('editor'), logEl = $('log');

export function post(action, data) {
    window.chrome.webview.postMessage(Object.assign({ action: action }, data || {}));
}

export function esc(s) {
    return String(s == null ? '' : s)
        .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

export function newId() {
    return (window.crypto && crypto.randomUUID)
        ? crypto.randomUUID().replace(/-/g, '')
        : 'id' + Date.now() + Math.floor(Math.random() * 1e6);
}

export function rowByKey(key) {
    return key ? treeEl.querySelector('.node[data-key="' + key + '"]') : null;
}

// ---- model lookups -------------------------------------------------------------------------

export function findCollection(id) {
    return state.collections.find(function (c) { return c.id === id; }) || null;
}

export function findTask(taskId) {
    for (var i = 0; i < state.collections.length; i++) {
        var t = (state.collections[i].tasks || []).find(function (t) { return t.id === taskId; });
        if (t) return t;
    }
    return null;
}

/// Puts one task where it belongs in the tree the panel already has, and says whether it could.
///
/// The delta half of the host protocol: a step edit changes one task, so the host sends that task
/// rather than re-serialising every collection in the workspace (see PushTaskAsync). Everything
/// else the panel holds — which row is selected, which are expanded, which has focus — is untouched
/// by construction, because the tree object it hangs from is the same one.
///
/// Returns false when the task names a collection this panel has never seen, which is the one shape
/// a delta cannot resolve. The caller answers that by asking for the whole state: a protocol that
/// can say "I cannot apply this" is what makes the fast path safe to take.
export function applyTask(collectionId, task) {
    if (!task || !task.id) return false;
    var target = findCollection(collectionId || '');
    if (!target) return false;

    // Removed from wherever it WAS first, so a task that changed collections does not end up in
    // both — the host has already moved the file, and two rows for one task is worse than a
    // stale one.
    state.collections.forEach(function (c) {
        var list = c.tasks || [];
        var at = list.findIndex(function (t) { return t.id === task.id; });
        if (at < 0) return;
        if (c === target) list[at] = task;
        else list.splice(at, 1);
    });

    target.tasks = target.tasks || [];
    if (!target.tasks.some(function (t) { return t.id === task.id; })) target.tasks.push(task);
    return true;
}

export function findStep(steps, stepId) {
    for (var i = 0; i < (steps || []).length; i++) {
        if (steps[i].id === stepId) return steps[i];
        var inChild = findStep(steps[i].children, stepId);
        if (inChild) return inChild;
    }
    return null;
}

export function removeStep(steps, stepId) {
    for (var i = 0; i < (steps || []).length; i++) {
        if (steps[i].id === stepId) return steps.splice(i, 1)[0];
        var removed = removeStep(steps[i].children, stepId);
        if (removed) return removed;
    }
    return null;
}

export function containsStep(step, stepId) {
    if (step.id === stepId) return true;
    return (step.children || []).some(function (c) { return containsStep(c, stepId); });
}

export function insertStepRelative(task, targetId, step, where) {
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

// Finds a step anywhere in the tree and returns where it lives: {parentId, index}.
export function locateStep(steps, id, parentId) {
    for (var i = 0; i < (steps || []).length; i++) {
        if (steps[i].id === id) return { parentId: parentId, index: i };
        var found = locateStep(steps[i].children, id, steps[i].id);
        if (found) return found;
    }
    return null;
}

// The one place that inserts step(s) into a tree at a given (parentId, index) address —
// used for both a manually created step and step(s) delivered by record-at-gap.
export function spliceStepsAt(task, parentStepId, index, newSteps) {
    var list;
    if (parentStepId) {
        var parent = findStep(task.steps, parentStepId);
        if (!parent) return null;
        list = parent.children = parent.children || [];
    } else {
        list = task.steps = task.steps || [];
    }
    var at = Math.max(0, Math.min(index, list.length));
    Array.prototype.splice.apply(list, [at, 0].concat(newSteps));
    return list;
}

// The id of the step that would run right after this gap — walking up through parent scopes
// when a list is exhausted — or null only when the gap is the very last slot in the whole
// tree (nothing left to run after it).
export function nextStepIdAfterGap(task, parentId, index) {
    var list = parentId ? ((findStep(task.steps, parentId) || {}).children || []) : (task.steps || []);
    if (index < list.length) return list[index].id;
    if (!parentId) return null;
    var loc = locateStep(task.steps, parentId, null);
    return loc ? nextStepIdAfterGap(task, loc.parentId, loc.index + 1) : null;
}

export function saveTask(task) { post('saveTask', { task: task }); }

export function selectedTask() { return state.sel.taskId ? findTask(state.sel.taskId) : null; }
// ---- rendering -----------------------------------------------------------------------------

export const ACTIONS = ['navigate', 'click', 'typeText', 'setValue', 'pressEnter', 'check', 'uncheck',
    'selectRadio', 'selectOption', 'uploadFile', 'waitForElement', 'assertElement', 'extractText', 'group'];

export const ACTION_INFO = {
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
    wait: 'Pause for a duration, until a time of day, or until a condition holds',
    forEach: 'Repeat the substeps once per row of a dataset',
    if: 'Run the substeps only when a condition holds',
    else: 'Run the substeps when the "if" above did not',
    runTask: 'Run another task from here',
    writeDataset: 'Append a row to a CSV or JSON file',
    extractAll: 'Read every matching row off this page into a dataset',
    setZoom: 'Zoom the page in or out, so a later step can reach what was cut off',
    aggregate: 'Total, count, or average one column of a dataset',
    checkElement: 'Check whether an element is present right now, without failing the run — ' +
        'pair it with an "if" to branch on it',
};

// Everything added after the original fourteen lives apart from them and is offered in a
// collapsed group, never at the top level of the action picker: a new user building their first
// task must not have to step over any of it. Named for what the group IS rather than for what
// most of it happens to be — `setZoom` is not flow control, and a list whose name only fits some
// of its members is a list people stop adding to correctly.
export const ADVANCED_ACTIONS = [
    'wait', 'if', 'else', 'forEach', 'runTask', 'writeDataset', 'extractAll', 'setZoom',
    'aggregate', 'checkElement',
];

// Everything a step's action dropdown may show, including actions created elsewhere (imported,
// or authored) that the picker itself does not offer.
export const ALL_ACTIONS = ACTIONS.concat(ADVANCED_ACTIONS);

// How a comparison reads, in the order the picker offers them. Shared rather than owned by the
// condition editor, because the TREE has to say the same words the editor does — a row that
// described a guard differently from the form that built it would be two vocabularies for one
// record, and the one on the row is the one people read most.
export const OPS = [
    { value: 'equals', label: 'is exactly' },
    { value: 'notEquals', label: 'is not' },
    { value: 'contains', label: 'contains' },
    { value: 'greaterThan', label: 'is greater than' },
    { value: 'lessThan', label: 'is less than' },
    { value: 'notEmpty', label: 'has any value' },
    { value: 'empty', label: 'is empty' },
    // Presence, which is a different question from emptiness: a row of a ragged list may not carry
    // the column at all, and asking whether that is "empty" fails the run rather than answering.
    { value: 'exists', label: 'has a value at all' },
    { value: 'notExists', label: 'is missing' },
    { value: 'isTrue', label: 'is true' },
    { value: 'isFalse', label: 'is false' },
];

// Comparisons that take no right-hand side; showing an inert box beside one would invite a value
// that is silently ignored.
export const UNARY = ['notEmpty', 'empty', 'exists', 'notExists', 'isTrue', 'isFalse'];

/// How a bound value reads in one short phrase — `row.sku`, `input: term`, `env: TOKEN`.
/// The output a WATCHING wait publishes its live reading under. Matches
/// WorkflowEngine.LiveWaitOutput, and is the name that wait's own condition binds to.
export const LIVE_WAIT_OUTPUT = 'value';

/// What a condition wait gives up after when nobody chose a number. Matches
/// WaitSpec.DefaultConditionTimeoutMs — the engine floors a missing or non-positive timeout to the
/// same value, so a step saved here and a step hand-edited on disk end up waiting the same length.
export const DEFAULT_WAIT_TIMEOUT_MS = 300000;

/// How often a condition wait re-checks, when nobody chose. Shorter than the model's own default
/// because a wait authored in front of a live page is one somebody is watching.
export const DEFAULT_WAIT_POLL_MS = 2000;

/// True when this step watches an element rather than re-asking about captured values: a wait on a
/// condition, with a target, re-reads that target on every poll. It lives here rather than beside
/// the other wait helpers because both the editor and the binding picker need it, and a shared
/// answer in the module they both already import beats an import cycle between them.
export function waitWatches(step) {
    return !!step && step.action === 'wait' && !!step.wait
        && step.wait.mode === 'untilCondition' && !!step.target;
}

export function describeBinding(binding) {
    if (!binding) return '';
    if (binding.label) return binding.label;
    if (binding.kind === 'datasetColumn') return 'row.' + (binding.columnName || '?');
    if (binding.kind === 'datasetRow') return 'the whole row';
    if (binding.kind === 'envVar') return 'env: ' + (binding.envVarName || '?');
    if (binding.kind === 'taskInput') return 'input: ' + (binding.parameterName || '?');
    if (binding.kind === 'stepOutput') return binding.outputField || 'output';
    return binding.kind;
}

// What an action is CALLED, as opposed to what it is keyed as. `if` and `forEach` are words for
// people who already write code; the picker and the editor show these instead. The keys are
// untouched — on disk, in Gherkin, and in every test they are still `if` and `forEach`.
export const ACTION_LABEL = {
    navigate: 'Go to a page',
    click: 'Click something',
    typeText: 'Type text',
    setValue: 'Set a field',
    pressEnter: 'Press Enter',
    check: 'Tick a box',
    uncheck: 'Untick a box',
    selectRadio: 'Choose an option',
    selectOption: 'Pick from a dropdown',
    uploadFile: 'Attach a file',
    waitForElement: 'Wait for something',
    assertElement: 'Check something',
    extractText: 'Read something',
    group: 'Group of steps',
    wait: 'Wait',
    if: 'Only if…',
    else: 'Otherwise',
    forEach: 'For each row of…',
    runTask: 'Run another task',
    writeDataset: 'Save a row',
    extractAll: 'Collect a list',
    setZoom: 'Zoom the page',
    aggregate: 'Work out a total',
    checkElement: 'Check for something',
};

export const STATUS_GLYPH = { running: '⟳', passed: '✓', healed: '✓♻', failed: '✗', skipped: '▷', paused: '⏸' };
// ---- screen-reader announcements ------------------------------------------------------------

// One polite live region for run progress and structural changes. Announcements are coalesced
// on a short timer so a burst of events becomes a single utterance rather than interrupting
// the screen reader once per event.
var srPending = [], srTimer = null;

export function announce(message) {
    srPending.push(message);
    if (srTimer) return;
    srTimer = setTimeout(function () {
        srTimer = null;
        var el = $('sr-status');
        if (el) el.textContent = srPending.join(' ');
        srPending = [];
    }, 250);
}
