// The Data tab: the datasets a task fans out over or writes results into.
//
// Datasets live in the database. A CSV or JSON file comes in with Import (a spreadsheet export is
// the usual source) and any dataset goes back out as a file with its ⇩ button.

import { $, esc, post, state } from './core.js';

export function renderDatasets() {
    var view = $('view-data');
    if (!view) return;

    var sets = state.datasets || [];
    var head =
        '<div class="section-head"><h2 class="section-label">Datasets</h2>' +
        '<button class="mini" id="btn-import-dataset" data-tooltip="Import CSV or JSON files as datasets (one with the same name is replaced)">⇪ Import</button>' +
        '</div>';

    if (!sets.length) {
        view.innerHTML = head +
            '<p class="empty-state">No datasets yet. Import a <code>.csv</code> or <code>.json</code> ' +
            'file and it becomes available to every task — a <em>for each</em> step can read its rows, ' +
            'and a <em>write dataset</em> step can append to it (creating it if need be).</p>';
    } else {
        view.innerHTML = head +
            '<div id="dataset-list" role="list" aria-label="Datasets">' +
            sets.map(function (d) {
                return '<div class="dataset-row" role="listitem">' +
                    '<span class="icon" aria-hidden="true">🗒️</span>' +
                    '<span class="name">' + esc(d.name) + '</span>' +
                    '<span class="dataset-meta">' + d.rows + ' row' + (d.rows === 1 ? '' : 's') +
                    ' · ' + (d.columns || []).length + ' column' + ((d.columns || []).length === 1 ? '' : 's') +
                    '</span>' +
                    '<button class="mini" data-export-dataset="' + esc(d.name) + '"' +
                    ' aria-label="Export ' + esc(d.name) + ' to a file" data-tooltip="Export to a file">⇩</button>' +
                    '</div>' +
                    '<div class="dataset-columns">' + esc((d.columns || []).join(', ')) + '</div>';
            }).join('') +
            '</div>';
    }

    var importBtn = $('btn-import-dataset');
    if (importBtn) importBtn.addEventListener('click', function () { post('importDataset'); });
    view.querySelectorAll('[data-export-dataset]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            post('exportDataset', { name: btn.getAttribute('data-export-dataset') });
        });
    });
}
