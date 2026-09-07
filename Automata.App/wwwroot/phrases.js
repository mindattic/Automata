// What a step row SAYS.
//
// Every row's text is derived from the record, every time it is drawn. Nothing here reads
// `step.label`, and that is the whole point: a label was written once, at creation, and never again
// — so changing a step's action left the old words in place and a row could read `Click 'Alpha'`
// while the step was an `if`. A derived sentence cannot go stale, because there is nothing to keep
// in sync.
//
// It lives in the panel rather than the host because the tree renders from the local `state`, so a
// row updates the instant an action changes. Deriving it on the host would put it behind the
// full-store echo and leave the row briefly describing the step it used to be.
//
// It is deliberately NOT `GherkinWriter.Phrase`, which has a different job. That one must produce a
// line the compiler can read back, so it names an element selector-first; this one is read by a
// person, so it names an element by whatever is most recognisable. The same step is
// `I type "wolf tshirts" into "textarea[name=\"q\"]"` there and `Type 'wolf tshirts' into Search`
// here, and both are right for their reader.

import { OPS, UNARY, describeBinding } from './core.js';

/// What to call the element a step acts on.
///
/// Human-first, the opposite of the Gherkin writer's order: the words a person would use to point
/// at the thing, falling back through progressively more technical identities and only reaching a
/// selector when there is nothing better. A recorded fingerprint usually carries several of these.
export function targetName(fingerprint) {
    if (!fingerprint) return null;
    return fingerprint.visibleText
        || fingerprint.ariaLabel
        || fingerprint.nearbyLabelText
        || fingerprint.placeholder
        || fingerprint.nameAttr
        || (fingerprint.id ? '#' + fingerprint.id : null)
        || fingerprint.cssSelector
        || fingerprint.xPath
        || null;
}

function target(step) {
    return targetName(step.target) || 'nothing yet';
}

function quote(text) {
    return text == null || text === '' ? "''" : "'" + text + "'";
}

/// A step's value, which may be a fixed string or bound to something else.
function value(step, field) {
    var bound = step.bindings && step.bindings[field || 'Value'];
    if (bound) return describeBinding(bound);
    return quote(field === 'Url' ? step.url : step.value);
}

/// A host and path is enough to recognise a page; the query string rarely is.
function shortUrl(url) {
    if (!url) return 'nowhere yet';
    var match = /^[a-z]+:\/\/([^/?#]+)([^?#]*)/i.exec(url);
    if (!match) return url;
    return match[1] + (match[2] && match[2] !== '/' ? match[2] : '');
}

/// An operand of a condition. An EMPTY literal comes back as nothing at all rather than as a pair
/// of quotes, so a half-built guard reads "Only if something has a value at all" instead of
/// "Only if '' has a value at all" — which looks like a bug rather than an unfinished step.
function operand(ref) {
    if (!ref) return '';
    if (!ref.kind || ref.kind === 'literal') {
        return ref.literal == null || ref.literal === '' ? '' : quote(ref.literal);
    }
    return describeBinding(ref);
}

/// A condition, in the same words the condition editor offers.
function conditionText(condition) {
    if (!condition) return 'something';
    var entry = OPS.filter(function (o) { return o.value === condition.op; })[0];
    var phrase = entry ? entry.label : condition.op;
    var left = operand(condition.left) || 'something';
    if (UNARY.indexOf(condition.op) >= 0) return left + ' ' + phrase;
    var right = operand(condition.right);
    return left + ' ' + phrase + (right ? ' ' + right : ' nothing yet');
}

function waitText(spec) {
    if (!spec) return 'Wait';
    if (spec.mode === 'untilTimeOfDay') {
        return 'Wait until ' + (spec.timeOfDay || 'a time of day');
    }
    if (spec.mode === 'untilCondition') {
        return 'Wait until ' + conditionText(spec.condition);
    }
    if (spec.mode === 'untilSignal') return 'Wait for a signal';
    return 'Wait ' + (spec.durationMs == null ? 0 : spec.durationMs) + 'ms';
}

/// The name of the task a runTask step calls, when it can be found; its id is no use to anybody.
function calledTaskName(step, collections) {
    var found = null;
    (collections || []).forEach(function (c) {
        (c.tasks || []).forEach(function (t) { if (t.id === step.runTaskId) found = t; });
    });
    return found ? found.name : (step.runTaskId ? 'a task that is no longer here' : 'nothing yet');
}

/// One sentence for one step. Total: every action produces something, because a row with no text is
/// worse than a clumsy one.
export function phraseFor(step, collections) {
    if (!step) return '';
    switch (step.action) {
        case 'navigate': return 'Go to ' + (step.bindings && step.bindings.Url
            ? describeBinding(step.bindings.Url) : shortUrl(step.url));
        case 'click': return 'Click ' + target(step);
        case 'typeText': return 'Type ' + value(step) + ' into ' + target(step);
        case 'setValue': return 'Set ' + target(step) + ' to ' + value(step);
        case 'pressEnter': return step.target ? 'Press Enter in ' + target(step) : 'Press Enter';
        case 'check': return 'Tick ' + target(step);
        case 'uncheck': return 'Untick ' + target(step);
        case 'selectRadio': return 'Choose ' + target(step);
        case 'selectOption': return 'Pick ' + value(step) + ' in ' + target(step);
        case 'uploadFile': return 'Attach ' + value(step) + ' to ' + target(step);
        case 'waitForElement': return 'Wait for ' + target(step);
        case 'assertElement': return step.value || (step.bindings && step.bindings.Value)
            ? 'Check ' + target(step) + ' contains ' + value(step)
            : 'Check ' + target(step) + ' is there';
        case 'extractText': return 'Read ' + target(step) +
            (step.outputs && step.outputs.length && step.outputs[0].name
                ? ' as ' + step.outputs[0].name : '');
        case 'checkElement': return 'Check whether ' + target(step) + ' is present' +
            (step.outputs && step.outputs.length && step.outputs[0].name
                ? ' as ' + step.outputs[0].name : '');
        case 'group': return 'A group of steps';
        case 'wait': return waitText(step.wait);
        case 'if': return 'Only if ' + conditionText(step.condition);
        case 'else': return 'Otherwise';
        case 'forEach': {
            var fe = step.forEach || {};
            if (fe.inlineValues && fe.inlineValues.length) {
                return 'For every one of ' + fe.inlineValues.length + ' pasted value' +
                    (fe.inlineValues.length === 1 ? '' : 's');
            }
            return 'For every row of ' + ((fe.source && fe.source.datasetName) || 'a list');
        }
        case 'runTask': return 'Run ' + quote(calledTaskName(step, collections));
        case 'writeDataset': return 'Save a row to ' +
            ((step.writeDataset && step.writeDataset.datasetName) || 'a file');
        case 'extractAll': return 'Collect a list into ' +
            ((step.harvest && step.harvest.datasetName) || 'a file');
        case 'setZoom': return 'Zoom to ' + (step.zoomPercent == null ? 100 : step.zoomPercent) + '%';
        case 'aggregate': {
            var spec = step.aggregate || {};
            var op = { sum: 'the total', count: 'how many', min: 'the smallest',
                max: 'the largest', average: 'the average' }[spec.op] || 'the total';
            return 'Work out ' + op + ' of ' + (spec.columnName || 'a column') +
                ' in ' + (spec.datasetName || 'a file');
        }
        // An action the panel does not know yet — a task written by a newer build. Its key is the
        // most honest thing available, and is better than an empty row.
        default: return step.action || 'a step';
    }
}
