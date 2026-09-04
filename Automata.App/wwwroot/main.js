// Entry point: global toolbar wiring, the advanced free-text path, and the bootstrap handshake
// with the host.
//
// Loaded as an ES module (<script type="module">). WebView2 serves wwwroot over the
// https://automata.local/ virtual host, so module resolution works exactly as it would on any
// static file server - no bundler, no build step.

import { $, post, state } from './core.js';
import { render } from './render.js';

// Imported for their side effects: each of these attaches listeners or installs window.ssPanel
// when it evaluates.
import './modal.js';
import './tree.js';
import './editor.js';
import './tabs.js';
import './settings.js';
import './demos.js';
import './data.js';
import './runs.js';
import './lanes.js';
import './schedule.js';
import { wireAuthoring } from './flow.js';
import './bridge.js';

// ---- toolbar wiring ------------------------------------------------------------------------

$('go').addEventListener('click', function () {
    var url = $('url').value.trim();
    if (url) post('navigate', { url: /^[a-z]+:\/\//i.test(url) ? url : 'https://' + url });
});
$('btn-record').addEventListener('click', function () { post('record'); });
$('btn-stop').addEventListener('click', function () {
    // With a task selected, the recording appends to it; otherwise a new task is created.
    post('stopRecord', {
        collectionId: state.sel.collectionId || '',
        taskId: state.sel.taskId || null,
    });
});
$('btn-continue').addEventListener('click', function () { post('continueRun'); });
$('btn-cancel-run').addEventListener('click', function () { post('cancelRun'); });
$('btn-import').addEventListener('click', function () { post('import'); });
$('btn-export').addEventListener('click', function () {
    if (state.sel.taskId) post('export', { taskId: state.sel.taskId });
    else if (state.sel.collectionId) post('export', { collectionId: state.sel.collectionId });
});
$('btn-new-collection').addEventListener('click', function () { post('createCollection', { name: 'New collection' }); });
$('btn-folder').addEventListener('click', function () { post('openCollections'); });
// Advanced free-text LLM path (unchanged host protocol).
$('run').addEventListener('click', function () {
    var task = $('task').value.trim();
    if (task) post('run', { task: task });
});
$('cancel').addEventListener('click', function () { post('cancel'); });
wireAuthoring();

post('ready');
post('getState');
post('getSettings');
post('getDatasets');
post('getRuns');
post('getSchedule');
render();
