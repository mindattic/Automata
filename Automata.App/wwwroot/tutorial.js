// First-run tutorial. This is the product's floor: a new user must be able to reach a working
// Google search and a Click Images step without meeting a single advanced concept, and
// tools/verify-ui.mjs asserts that after every phase.

import { newId, state, post, findTask, saveTask } from './core.js';
import { openInfoModal } from './modal.js';
import { render } from './render.js';

// When the app opens onto an empty store, walk the user through the model by building a
// real example in front of them: Collection → Task → Steps, one OK-gated popup per concept.
// Evaluated only on the FIRST state push of the session, so deleting everything later
// doesn't restart the tour mid-work.
var tutorialStage = 0;
var tutorialChecked = false;

function tutorialSteps() {
    return [
        { id: newId(), action: 'navigate', label: 'Go to Google', url: 'https://www.google.com', children: [] },
        {
            id: newId(), action: 'typeText', label: "Type 'wolf tshirts' into Search", value: 'wolf tshirts',
            target: {
                tag: 'textarea', nameAttr: 'q', ariaRole: 'combobox', ariaLabel: 'Search',
                cssSelector: 'textarea[name="q"]', classList: [],
            }, children: [],
        },
        {
            // Pressing Enter beats clicking the search button — Google's suggestion overlay
            // makes the button unreliable, which is true of most search boxes.
            id: newId(), action: 'pressEnter', label: 'Press Enter to search',
            target: {
                tag: 'textarea', nameAttr: 'q', ariaRole: 'combobox', ariaLabel: 'Search',
                cssSelector: 'textarea[name="q"]', classList: [],
            }, children: [],
        },
        {
            id: newId(), action: 'waitForElement', label: 'Wait for results',
            target: { tag: 'div', id: 'search', cssSelector: '#search', classList: [] }, children: [],
        },
        {
            // The results page's "Images" tab — found by its visible link text.
            id: newId(), action: 'click', label: "Click 'Images'",
            target: { tag: 'a', visibleText: 'Images', classList: [] }, children: [],
        },
    ];
}

export function maybeStartTutorial() {
    if (tutorialChecked) return;
    tutorialChecked = true;
    // The generated examples do not count as work this person has done. They are seeded on
    // first load so there is always something that runs to read, and if they suppressed the
    // tutorial then a brand-new user would never be walked through Collection -> Task -> Steps.
    var built = state.collections.filter(function (c) { return c.id !== state.demoCollectionId; });
    if (built.length > 0) return;
    tutorialStage = 1;
    openInfoModal('Welcome to Automata',
        "A Collection is a group of Tasks. Everything you automate lives inside one. " +
        "Press OK to create your first Collection: 'Google Searches'.",
        function () { post('createCollection', { name: 'Google Searches' }); });
}

// Each stage waits for the host to echo the object it just created back through onState,
// then shows the next popup — creation is visibly paused on each OK.
export function advanceTutorial() {
    if (!tutorialStage) return;

    if (tutorialStage === 1) {
        var col = state.collections.find(function (c) { return c.name === 'Google Searches'; });
        if (!col) return;
        tutorialStage = 2;
        state.sel = { collectionId: col.id, taskId: null, stepId: null };
        render();
        openInfoModal('Tasks',
            "A Task is a member of a Collection. A Task is a group of Steps that run in " +
            "order — each Step is one browser action (navigate, type, click, extract…). " +
            "Press OK to create the Task 'Wolf Tshirts'.",
            function () { post('createTask', { collectionId: col.id, name: 'Wolf Tshirts' }); });
        return;
    }

    if (tutorialStage === 2) {
        var col2 = state.collections.find(function (c) { return c.name === 'Google Searches'; });
        var task = col2 && (col2.tasks || []).find(function (t) { return t.name === 'Wolf Tshirts'; });
        if (!task) return;
        tutorialStage = 3;
        task.steps = tutorialSteps();
        state.sel = { collectionId: col2.id, taskId: task.id, stepId: null };
        state.expanded[task.id] = true;
        saveTask(task);
        return;
    }

    if (tutorialStage === 3) {
        var done = state.sel.taskId && findTask(state.sel.taskId);
        if (!done || !(done.steps || []).length) return;
        tutorialStage = 0;
        render();
        openInfoModal('Run it',
            "Click the ▶ button on a Task row to run its Steps. Click any Step to edit it, " +
            "hover between steps to insert a new one, or press ● Record to capture your own.",
            null);
    }
}
