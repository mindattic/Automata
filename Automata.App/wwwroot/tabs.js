// Sidebar view tabs (Build / Schedule / Data / Runs).

import { $, post } from './core.js';

// WAI-ARIA APG tabs, manual activation: arrows move focus only, Enter/Space (native button
// activation) selects. Inactive panels get the real `hidden` attribute so their content
// leaves the accessibility tree rather than merely going invisible.
var tabsEl = document.querySelector('.view-tabs');

function tabButtons() {
    return Array.prototype.slice.call(tabsEl.querySelectorAll('[role="tab"]'));
}

function selectTab(tab) {
    tabButtons().forEach(function (t) {
        var on = t === tab;
        t.setAttribute('aria-selected', String(on));
        t.setAttribute('tabindex', on ? '0' : '-1');
        var panel = $(t.getAttribute('aria-controls'));
        if (panel) panel.hidden = !on;
    });
}

// Build is where the tree, the editor and the recording preview live, so anything that
// implies "look at the steps" pulls the user back to it rather than updating a hidden panel.
export function showBuildTab() {
    var build = $('tab-build');
    if (build && build.getAttribute('aria-selected') !== 'true') selectTab(build);
}

tabsEl.addEventListener('click', function (e) {
    var tab = e.target.closest ? e.target.closest('[role="tab"]') : null;
    if (!tab) return;
    selectTab(tab);
    // Datasets are files on disk that change outside the app, so re-read them on arrival rather
    // than trusting whatever was cached at startup.
    if (tab.id === 'tab-data') post('getDatasets');
    // Runs and datasets are files on disk that change outside this window, so re-read them on
    // arrival rather than trusting whatever was cached at startup.
    if (tab.id === 'tab-runs') post('getRuns');
    // Same for the schedule: the runner writes next-due times into it on every tick, so what
    // this window last saw is stale the moment anything fires.
    if (tab.id === 'tab-schedule') post('getSchedule');
});

tabsEl.addEventListener('keydown', function (e) {
    var tabs = tabButtons();
    var i = tabs.indexOf(document.activeElement);
    if (i < 0) return;
    var next = e.key === 'ArrowRight' ? (i + 1) % tabs.length
        : e.key === 'ArrowLeft' ? (i - 1 + tabs.length) % tabs.length
        : e.key === 'Home' ? 0
        : e.key === 'End' ? tabs.length - 1 : -1;
    if (next < 0) return;
    e.preventDefault();
    tabs[next].focus();
});
