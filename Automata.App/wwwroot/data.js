// The Data tab: the CSV/JSON files a task fans out over or writes results into.
//
// There is no import step and no upload — a dataset is just a file in a folder the user can open
// in Explorer, exactly like Collections. Dropping a spreadsheet export in is the whole workflow.

import { $, esc, post, state } from './core.js';

export function renderDatasets() {
    var view = $('view-data');
    if (!view) return;

    var sets = state.datasets || [];
    var head =
        '<div class="section-head"><h2 class="section-label">Datasets</h2>' +
        '<button class="mini" id="btn-open-datasets" data-tooltip="Open the Datasets folder in File Explorer">📁 Files</button>' +
        '</div>';

    if (!sets.length) {
        view.innerHTML = head +
            '<p class="empty-state">No datasets yet. Drop a <code>.csv</code> or <code>.json</code> ' +
            'file into ' + esc(state.datasetRoot || 'the Datasets folder') +
            ' and it becomes available to every task — a <em>for each</em> step can read its rows, ' +
            'and a <em>write dataset</em> step can append to it.</p>';
    } else {
        view.innerHTML = head +
            '<div id="dataset-list" role="list" aria-label="Datasets">' +
            sets.map(function (d) {
                return '<div class="dataset-row" role="listitem">' +
                    '<span class="icon" aria-hidden="true">🗒️</span>' +
                    '<span class="name">' + esc(d.name) + '</span>' +
                    '<span class="dataset-meta">' + d.rows + ' row' + (d.rows === 1 ? '' : 's') +
                    ' · ' + (d.columns || []).length + ' column' + ((d.columns || []).length === 1 ? '' : 's') +
                    '</span></div>' +
                    '<div class="dataset-columns">' + esc((d.columns || []).join(', ')) + '</div>';
            }).join('') +
            '</div>';
    }

    var openBtn = $('btn-open-datasets');
    if (openBtn) openBtn.addEventListener('click', function () { post('openDatasets'); });
}
