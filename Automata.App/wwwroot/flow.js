// Natural-language authoring: describe a workflow, review the Gherkin it produced, then insert it.
//
// The review step is the point. The LLM's output is a short feature file in a documented, closed
// vocabulary — readable by the person who asked for it, and already compiled, so the preview shows
// the actual steps rather than a promise. Nothing reaches the store until Insert.

import { $, esc, post, state } from './core.js';
import { openFormModal, openInfoModal } from './modal.js';

export function requestDraft() {
    var box = $('task');
    var description = (box.value || '').trim();
    if (!description) {
        openInfoModal('Describe it first',
            'Say what the task should do, in your own words — for example: ' +
            '"search Google for wolf tshirts and click Images".', null);
        box.focus();
        return;
    }
    $('btn-draft').disabled = true;
    $('btn-draft').textContent = 'Drafting…';
    post('draftFlow', { description: description });
}

function resetDraftButton() {
    var btn = $('btn-draft');
    if (!btn) return;
    btn.disabled = false;
    btn.textContent = '✎ Draft steps';
}

export function showDraft(draft) {
    resetDraftButton();
    state.flowDraft = draft;

    var form = openFormModal(
        draft.canInsert ? 'Review the drafted steps' : 'This draft does not compile',
        draft.canInsert
            ? 'Nothing is saved until you press Insert.'
            : 'Fix the description, or edit the feature text and try again.');

    var errors = (draft.diagnostics || []).filter(function (d) { return d.severity === 'error'; });
    var warnings = (draft.diagnostics || []).filter(function (d) { return d.severity !== 'error'; });

    form.innerHTML =
        // role="alert" so a failure is announced rather than only coloured.
        (errors.length
            ? '<div class="diagnostics" role="alert"><h3 class="section-label">Problems</h3><ul>' +
              errors.map(function (d) {
                  return '<li><b>line ' + d.line + '</b> ' + esc(d.message) + '</li>';
              }).join('') + '</ul></div>'
            : '') +
        (warnings.length
            ? '<div class="diagnostics warn"><h3 class="section-label">Worth knowing</h3><ul>' +
              warnings.map(function (d) { return '<li>' + esc(d.message) + '</li>'; }).join('') +
              '</ul></div>'
            : '') +
        (draft.tasks && draft.tasks.length
            ? '<h3 class="section-label">Steps it compiles to</h3>' +
              draft.tasks.map(function (t) {
                  return '<div class="draft-task"><b>' + esc(t.name) + '</b><pre>' +
                      esc((t.steps || []).join('\n')) + '</pre></div>';
              }).join('')
            : '') +
        ((draft.datasets && draft.datasets.length)
            ? '<p class="scope-note">Will also create: ' +
              draft.datasets.map(function (d) { return esc(d.name) + ' (' + d.rows + ' rows)'; }).join(', ') +
              '</p>'
            : '') +
        '<h3 class="section-label">What it understood</h3>' +
        '<textarea id="draft-feature" rows="12" spellcheck="false"' +
        ' aria-label="The Gherkin feature this description produced">' + esc(draft.featureText) + '</textarea>' +
        '<div class="row">' +
        '<button id="draft-recompile" class="secondary">Re-check edits</button>' +
        (draft.canInsert ? '<button id="draft-insert">Insert</button>' : '') +
        '</div>' +
        '<p class="scope-note">Drafted by ' + esc(draft.provider) + ' in ' + draft.attempts +
        ' attempt' + (draft.attempts === 1 ? '' : 's') + '. Steps are editable afterwards like any other.</p>';

    var insert = $('draft-insert');
    if (insert) {
        insert.addEventListener('click', function () {
            post('insertFlow');
            $('modal-ok').click();
        });
    }
    $('draft-recompile').addEventListener('click', function () {
        // Compiles the edit directly. Sending it back through the model would quietly discard
        // what the user just wrote; a hand edit is held to the same standard, not re-rolled.
        post('compileFlow', { featureText: $('draft-feature').value });
    });
}

export function showFeatureView(view) {
    var form = openFormModal(
        'Feature view — ' + view.taskName,
        view.isLossy
            ? 'Read-only: this task cannot be fully expressed as Gherkin.'
            : 'How this task reads as Gherkin.');

    form.innerHTML =
        (view.isLossy
            ? '<div class="diagnostics warn"><h3 class="section-label">Why it is read-only</h3><ul>' +
              (view.reasons || []).map(function (r) { return '<li>' + esc(r) + '</li>'; }).join('') +
              '</ul></div>'
            : '') +
        '<textarea id="feature-text" rows="14" readonly spellcheck="false"' +
        ' aria-label="This task as a Gherkin feature">' + esc(view.featureText) + '</textarea>';
}

export function wireAuthoring() {
    var btn = $('btn-draft');
    if (btn) btn.addEventListener('click', requestDraft);
}
