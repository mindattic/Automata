// Drives the Automata WPF app's two WebView2 panes over the Chrome DevTools Protocol (CDP), so
// UI behavior (hover states, click flows, the rendered step tree) can be verified the same way a
// web app would be — instead of only smoke-testing that the app launches. See
// .claude/skills/verify-automata-ui/SKILL.md for the short version; this file is the driver.
//
// Usage: node tools/verify-ui.mjs [--clean]
//   --clean   remove the scratch directory on exit instead of leaving it for post-mortem.

import { chromium } from 'playwright';
import { spawn, execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync, rmSync, readFileSync, readdirSync, existsSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import path from 'node:path';
import http from 'node:http';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
// axe-core ships only in tools/ (a devDependency) and is injected into the already-CDP-attached
// panel page — it never reaches the app's wwwroot, so the shipped app stays offline-capable.
const axeCorePath = createRequire(import.meta.url).resolve('axe-core');
const repoRoot = path.resolve(__dirname, '..');
const clean = process.argv.includes('--clean');

const PANEL_PORT = 9333;
const TARGET_PORT = 9334;
// The floor check needs a second app launch against an empty store, on its own ports so it can
// never race the first launch's listeners.
const FLOOR_PANEL_PORT = 9335;
const FLOOR_TARGET_PORT = 9336;

const newId = () => randomUUID().replace(/-/g, '');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function waitFor(predicate, { timeoutMs = 10000, intervalMs = 150, label = 'condition' } = {}) {
  const deadline = Date.now() + timeoutMs;
  let lastErr;
  while (Date.now() < deadline) {
    try {
      const value = await predicate();
      if (value) return value;
    } catch (err) {
      lastErr = err;
    }
    await sleep(intervalMs);
  }
  throw new Error(`Timed out waiting for ${label}${lastErr ? `: ${lastErr.message}` : ''}`);
}

function servesCdp(port) {
  return new Promise((resolve) => {
    const req = http.get({ host: '127.0.0.1', port, path: '/json/version', timeout: 1000 }, (res) => {
      res.resume();
      resolve(res.statusCode === 200);
    });
    req.on('error', () => resolve(false));
    req.on('timeout', () => { req.destroy(); resolve(false); });
  });
}

// Refuses to start while something is already serving CDP on a port this run needs.
//
// Worth failing loudly over: a WebView2 orphaned by an earlier interrupted run keeps listening,
// waitForHttp200 is satisfied by it immediately, and the whole run then drives the STALE panel —
// which reports a mystery like "the fixture task never appeared" instead of "you have a leftover".
async function requirePortFree(port, label) {
  if (await servesCdp(port)) {
    throw new Error(
      `${label} (:${port}) is already serving CDP. A previous run's WebView2 is still alive — ` +
      'close the app, or kill the orphaned msedgewebview2.exe, and retry. Attaching to it would ' +
      'drive the wrong panel and report nonsense.');
  }
}

async function waitForHttp200(port, label) {
  return waitFor(
    () =>
      new Promise((resolve) => {
        const req = http.get({ host: '127.0.0.1', port, path: '/json/version', timeout: 1000 }, (res) => {
          res.resume();
          resolve(res.statusCode === 200);
        });
        req.on('error', () => resolve(false));
        req.on('timeout', () => { req.destroy(); resolve(false); });
      }),
    { timeoutMs: 20000, label },
  );
}

async function firstPage(browser) {
  return waitFor(
    () => browser.contexts()[0]?.pages()?.[0] ?? null,
    { timeoutMs: 10000, label: 'a page to appear' },
  );
}

async function hasClass(locator, cls) {
  const attr = await locator.getAttribute('class');
  return !!attr && attr.split(/\s+/).includes(cls);
}

async function waitForClass(locator, cls, timeoutMs = 10000) {
  return waitFor(() => hasClass(locator, cls), { timeoutMs, label: `class "${cls}"` });
}

async function waitForEnabled(locator, timeoutMs = 10000) {
  return waitFor(async () => !(await locator.isDisabled()), { timeoutMs, label: 'element to become enabled' });
}

// .node-btns only becomes visible on :hover — Playwright can't click a display:none button
// directly (it has no box to move the pointer onto), so hover the row first to reveal it.
//
// Retried as a PAIR on purpose. The tree re-renders from scratch on every host push, so a row can
// be replaced between revealing its buttons and clicking one — and CSS :hover does not reliably
// re-apply to the replacement without the pointer moving again. Playwright's own retry re-resolves
// the button but cannot re-hover, so a run still emitting step events in the background used to
// make this time out at random.
//
// Every row operation now lives behind that row's wrench, so this is two clicks: the wrench, then
// the item. The menu is rendered at the document root rather than inside the row, which is why the
// second click is not scoped to the row locator.
async function clickRowOp(rowLocator, op) {
  let lastErr;
  for (let attempt = 0; attempt < 4; attempt++) {
    try {
      await rowLocator.hover();
      await rowLocator.locator('[data-op="menu"]').click({ timeout: 5000 });
      await rowLocator.page().locator(`.row-menu [data-op="${op}"]`).click({ timeout: 5000 });
      return;
    } catch (err) {
      lastErr = err;
      // Off and back, so the next hover is a real pointer movement rather than a no-op at
      // coordinates the pointer is already at.
      if (nudgePage) await nudgePage.mouse.move(0, 0);
      await sleep(250);
    }
  }
  throw lastErr;
}

let nudgePage = null;
function useNudgePage(page) { nudgePage = page; }

function assertEqual(actual, expected, message) {
  if (actual !== expected) throw new Error(`${message}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
}

function assertTrue(value, message) {
  if (!value) throw new Error(message);
}

// ---- fixture: a scratch collection with a passing task (a deliberate gap between two of its
// steps, for the record-at-gap checklist) and a second task engineered to fail its only step (for
// the run-collection "continues past a failure" checklist) ----------------------------------------

function writeFixture(collectionsRoot, fixtureUrl) {
  const collectionId = newId();
  const taskId = newId();
  const stepAId = newId();
  const stepBId = newId();
  const failTaskId = newId();
  const failStepId = newId();
  const now = new Date().toISOString();

  const collectionDir = path.join(collectionsRoot, 'Verify');
  mkdirSync(collectionDir, { recursive: true });

  writeFileSync(
    path.join(collectionDir, 'collection.json'),
    JSON.stringify({
      schemaVersion: 1, id: collectionId, name: 'Verify', description: '',
      createdUtc: now, modifiedUtc: now, taskOrder: [taskId, failTaskId],
    }, null, 2),
  );

  const target = (id, text) => ({ tag: 'button', id, cssSelector: `#${id}`, classList: [], visibleText: text });

  writeFileSync(
    path.join(collectionDir, 'Insert Fixture.json'),
    JSON.stringify({
      schemaVersion: 1, id: taskId, collectionId, name: 'Insert Fixture', description: '',
      startUrl: fixtureUrl,
      steps: [
        { id: stepAId, action: 'click', label: "Click 'Alpha'", target: target('alpha', 'Alpha'), children: [] },
        { id: stepBId, action: 'click', label: "Click 'Beta'", target: target('beta', 'Beta'), children: [] },
      ],
      createdUtc: now, modifiedUtc: now,
    }, null, 2),
  );

  writeFileSync(
    path.join(collectionDir, 'Fail Task.json'),
    JSON.stringify({
      schemaVersion: 1, id: failTaskId, collectionId, name: 'Fail Task', description: '',
      startUrl: fixtureUrl,
      steps: [
        { id: failStepId, action: 'click', label: "Click 'Missing'", target: target('does-not-exist', 'Missing'), children: [] },
      ],
      createdUtc: now, modifiedUtc: now,
    }, null, 2),
  );

  return { collectionId, taskId, failTaskId };
}

// A second collection, so the control-flow task never joins the Verify collection's run and
// cannot disturb the pass/fail counts the checks above assert on. Named to sort after "Verify".
function writeFlowFixture(collectionsRoot, datasetsRoot, fixtureUrl) {
  const collectionId = newId();
  const taskId = newId();
  const now = new Date().toISOString();

  mkdirSync(datasetsRoot, { recursive: true });
  writeFileSync(path.join(datasetsRoot, 'skus.csv'), 'sku\naaa\nbbb\n');

  const dir = path.join(collectionsRoot, 'Verify Flow');
  mkdirSync(dir, { recursive: true });
  writeFileSync(path.join(dir, 'collection.json'), JSON.stringify({
    schemaVersion: 2, id: collectionId, name: 'Verify Flow', description: '',
    createdUtc: now, modifiedUtc: now, taskOrder: [taskId],
  }, null, 2));

  writeFileSync(path.join(dir, 'Loop.json'), JSON.stringify({
    schemaVersion: 2, id: taskId, collectionId, name: 'Loop', description: '',
    startUrl: fixtureUrl,
    steps: [
      {
        id: newId(), action: 'forEach', label: 'Each sku',
        forEach: { source: { kind: 'datasetRow', datasetName: 'skus.csv' }, rowVariableName: 'row' },
        children: [
          {
            id: newId(), action: 'click', label: "Click 'Alpha'",
            target: { tag: 'button', id: 'alpha', cssSelector: '#alpha', classList: [], visibleText: 'Alpha' },
            children: [],
          },
        ],
      },
    ],
    createdUtc: now, modifiedUtc: now,
  }, null, 2));

  return { flowCollectionId: collectionId, flowTaskId: taskId };
}

// ---- the floor check ----------------------------------------------------------------------
//
// The governing invariant for this project: a new user must still be able to record a Google
// search, click Images, and press Run without ever meeting a trigger, a binding, a lane, a
// dataset or a settings scope. The first-run tutorial IS that floor, so every phase has to
// leave it working and leave the new machinery out of sight.
//
// It needs its own app launch because the tutorial only fires against an empty store.
async function floorCheck(exePath, group) {
  const scratch = path.join(tmpdir(), `automata-floor-${Date.now()}`);
  const panelProfile = path.join(scratch, 'panel-profile');
  const targetProfile = path.join(scratch, 'target-profile');
  const collectionsRoot = path.join(scratch, 'collections');
  for (const dir of [panelProfile, targetProfile, collectionsRoot]) mkdirSync(dir, { recursive: true });

  console.log(`Relaunching against an empty store for the floor check (panel CDP :${FLOOR_PANEL_PORT})...`);
  const proc = spawn(exePath, [], {
    cwd: path.dirname(exePath),
    env: {
      ...process.env,
      AUTOMATA_PANEL_CDP_PORT: String(FLOOR_PANEL_PORT),
      AUTOMATA_TARGET_CDP_PORT: String(FLOOR_TARGET_PORT),
      AUTOMATA_PANEL_PROFILE_DIR: panelProfile,
      AUTOMATA_TARGET_PROFILE_DIR: targetProfile,
      AUTOMATA_COLLECTIONS_ROOT: collectionsRoot,
      AUTOMATA_DATASETS_ROOT: path.join(scratch, 'datasets'),
      AUTOMATA_RUNS_ROOT: path.join(scratch, 'runs'),
      AUTOMATA_SCHEDULE_PATH: path.join(scratch, 'schedule', 'schedule.json'),
      AUTOMATA_PARKED_ROOT: path.join(scratch, 'parked'),
      AUTOMATA_LIVE_ROOT: path.join(scratch, 'live'),
      AUTOMATA_DEMOS_ROOT: path.join(scratch, 'demos'),
      AUTOMATA_SETTINGS_PATH: path.join(scratch, 'settings.json'),
    },
    stdio: 'ignore',
  });

  let browser;
  try {
    await waitForHttp200(FLOOR_PANEL_PORT, 'floor-check panel CDP endpoint');
    browser = await chromium.connectOverCDP(`http://127.0.0.1:${FLOOR_PANEL_PORT}`);
    const page = await firstPage(browser);
    await page.waitForLoadState('domcontentloaded');

    const modalTitled = (title, label) =>
      waitFor(async () => (await page.locator('#modal-title').textContent()) === title,
        { timeoutMs: 20000, label });

    await group('floor: the first-run tutorial still walks Collection -> Task -> Steps', async () => {
      await modalTitled('Welcome to Automata', 'the welcome popup');
      await page.locator('#modal-ok').click();
      await modalTitled('Tasks', 'the Tasks popup');
      await page.locator('#modal-ok').click();
      await modalTitled('Run it', 'the closing "Run it" popup');
      await page.locator('#modal-ok').click();
      await waitFor(() => hasClass(page.locator('#modal'), 'hidden'),
        { timeoutMs: 5000, label: 'the tutorial to finish' });
    });

    await group('floor: it builds Google Searches / Wolf Tshirts, ending at Click Images', async () => {
      // First load also seeds the generated "Demos" collection, deliberately: a new user should
      // have something that already works to run and read. So the invariant is not "one
      // collection" any more — it is that the tutorial's own collection is there, that it is the
      // only thing besides the generated examples, and that it holds exactly the tutorial.
      const collections = (await page.locator('#tree .node.collection .name').allTextContents())
        .map((t) => t.trim());
      assertEqual(JSON.stringify(collections.slice().sort()), JSON.stringify(['Demos', 'Google Searches']),
        `expected the tutorial collection plus the generated examples, got ${JSON.stringify(collections)}`);

      // Scoped by id through the tree's data-collection / data-task attributes, so the examples'
      // tasks and steps cannot mask a regression in what the tutorial itself builds. The tree is a
      // flat list of rows, so scoping has to be by attribute rather than by containment.
      const tutorialId = await page
        .locator('#tree .node.collection', { has: page.locator('.name', { hasText: /^Google Searches$/ }) })
        .getAttribute('data-collection');
      const tasks = (await page.locator(`#tree .node.task[data-collection="${tutorialId}"] .name`)
        .allTextContents()).map((t) => t.trim());
      assertEqual(JSON.stringify(tasks), JSON.stringify(['Wolf Tshirts']),
        `expected only the tutorial task, got ${JSON.stringify(tasks)}`);
      const tutorialTaskId = await page
        .locator(`#tree .node.task[data-collection="${tutorialId}"]`).getAttribute('data-task');
      const labels = await page.locator(`#tree .node.step[data-task="${tutorialTaskId}"] .name`)
        .allTextContents();
      assertEqual(labels.length, 5, `expected the 5 tutorial steps, got ${JSON.stringify(labels)}`);
      assertTrue(/Images/i.test(labels[4]), `the tutorial must end at Click Images, got "${labels[4]}"`);
    });

    await group('floor: the action picker still offers only the original 14 actions', async () => {
      // Every step action added after the original fourteen (Wait / If / ForEach / RunTask /
      // WriteDataset / ExtractAll / whatever comes next) must arrive in a separate collapsed
      // "Flow" group, never at the top level of this list. The number in the group is expected to
      // GROW, so what is pinned here is the top level, which must not.
      await clickRowOp(page.locator('#tree .node.step').first(), 'ins-after');
      await page.locator('#modal-list .action-pick').first().waitFor({ state: 'visible', timeout: 5000 });
      // Direct children only: flow-control actions live in a collapsed <details> below, and the
      // invariant is about what a new user is shown up front.
      const picks = await page.locator('#modal-list > .action-pick b').allTextContents();
      assertEqual(picks.length, 15,
        `expected Record + 14 actions at the top level, got ${picks.length}: ${JSON.stringify(picks)}`);
      const flowGroup = page.locator('#modal-list details.pick-group');
      assertEqual(await flowGroup.count(), 1, 'flow actions should be in their own group');
      assertEqual(await flowGroup.evaluate((el) => el.open), false, 'the flow group must start collapsed');
      const flowCount = await flowGroup.locator('.action-pick').count();
      assertTrue(flowCount >= 5,
        `every flow-control action belongs in the collapsed group; found only ${flowCount}`);
      // The specific trap this catches: a new action added to the basic list by mistake. Checked by
      // action key and named in the failure, because a count says nothing about WHICH one leaked.
      const topLevelKeys = await page.locator('#modal-list > .action-pick')
        .evaluateAll((els) => els.map((el) => el.getAttribute('data-value')));
      for (const advanced of ['wait', 'if', 'forEach', 'runTask', 'writeDataset', 'extractAll']) {
        assertTrue(!topLevelKeys.includes(advanced),
          `the advanced action "${advanced}" leaked into the top-level picker`);
      }
      await page.keyboard.press('Escape');
      await waitFor(() => hasClass(page.locator('#modal'), 'hidden'),
        { timeoutMs: 5000, label: 'the picker to close' });
    });

    await group('floor: a first-run user lands on Build with nothing advanced on screen', async () => {
      // Tabs themselves are chrome, not an advanced concept, so they are allowed here - but Build
      // must be what a new user sees, and every advanced CONCEPT must stay out of sight.
      assertEqual(await page.locator('#tab-build').getAttribute('aria-selected'), 'true',
        'a first-run user must land on Build');
      for (const view of ['view-schedule', 'view-data', 'view-runs']) {
        assertTrue(await page.locator(`#${view}`).isHidden(), `#${view} should be hidden on first run`);
      }
      // Two different bars. Some things must not exist at all yet; the rest exist but must not be
      // on screen until the user reaches for them. Every per-row operation now lives inside a menu
      // that is built when its wrench is clicked, so for those the invariant is presence: nothing
      // named an engine setting or a Gherkin feature is anywhere in a first-run DOM.
      const absent = await page.locator(
        '.chip.bound, .chip.sched, #lane-strip, .settings-field, .row-menu',
      ).count();
      assertEqual(absent, 0, 'advanced affordances exist in the first-run DOM');
      const visible = await page.locator(
        '#ed-settings:visible, .binding-toggle:visible, #btn-draft:visible, ' +
        '[data-op="menu"]:visible',
      ).count();
      assertEqual(visible, 0, 'advanced affordances are on screen without hovering anything');
    });

    await group('floor: the store holds only the collection, with no new fields', async () => {
      const dir = path.join(collectionsRoot, 'Google Searches');
      const files = readdirSync(dir).sort();
      assertEqual(JSON.stringify(files), JSON.stringify(['Wolf Tshirts.json', 'collection.json']),
        `expected only the collection and its one task on disk, got ${JSON.stringify(files)}`);
      const task = JSON.parse(readFileSync(path.join(dir, 'Wolf Tshirts.json'), 'utf8'));
      assertTrue(!('settings' in task),
        'a task must not carry a settings object until someone overrides something');
      // Freshly written files carry the current schema version; what the floor actually cares
      // about is that no new FIELD appeared, which the checks either side of this assert.
      assertEqual(task.schemaVersion, 2, 'a newly written task should carry the current schema version');
      const collection = JSON.parse(readFileSync(path.join(dir, 'collection.json'), 'utf8'));
      for (const field of ['settings', 'taskDependencies', 'triggers']) {
        assertTrue(!(field in collection), `collection.json must not gain "${field}" until it is used`);
      }
    });
  } finally {
    try { await browser?.close(); } catch { /* CDP-attached browsers may already be gone */ }
    const exited = new Promise((resolve) => proc.once('exit', resolve));
    proc.kill();
    if (clean) {
      await Promise.race([exited, sleep(5000)]);
      for (let attempt = 0; attempt < 5; attempt++) {
        try { rmSync(scratch, { recursive: true, force: true }); break; } catch { await sleep(1000); }
      }
    }
  }
}

// ---- main ---------------------------------------------------------------------------------

async function main() {
  console.log('Building Automata.App...');
  execFileSync('dotnet', ['build', path.join(repoRoot, 'Automata.App'), '-c', 'Debug'], {
    cwd: repoRoot, stdio: 'inherit',
  });

  for (const [port, label] of [
    [PANEL_PORT, 'panel CDP endpoint'],
    [TARGET_PORT, 'target CDP endpoint'],
    [FLOOR_PANEL_PORT, 'floor-check panel CDP endpoint'],
    [FLOOR_TARGET_PORT, 'floor-check target CDP endpoint'],
  ]) await requirePortFree(port, label);

  const exePath = path.join(repoRoot, 'Automata.App', 'bin', 'Debug', 'net10.0-windows', 'Automata.App.exe');
  const scratch = path.join(tmpdir(), `automata-verify-${Date.now()}`);
  const panelProfile = path.join(scratch, 'panel-profile');
  const targetProfile = path.join(scratch, 'target-profile');
  const collectionsRoot = path.join(scratch, 'collections');
  const datasetsRoot = path.join(scratch, 'datasets');
  // The schedule lives in one file, and it must be a scratch one: without this the app under
  // test would read and write the developer's real Documents\Automata\Schedule\schedule.json.
  const schedulePath = path.join(scratch, 'schedule', 'schedule.json');
  // Same reason as the schedule: a run that parks writes into this folder, and the app under test
  // must not be able to reach into the developer's real one.
  const parkedRoot = path.join(scratch, 'parked');
  // Where each Automata process publishes the browser lanes it has busy right now.
  const liveRoot = path.join(scratch, 'live');
  // The generated example pages. Isolated for the same reason as everything else here — the app
  // WRITES these on first load, so without the hook a test run would rewrite the developer's own
  // Documents\Automata\Demos folder every time it ran.
  const demosRoot = path.join(scratch, 'demos');
  for (const dir of [panelProfile, targetProfile, collectionsRoot, datasetsRoot]) mkdirSync(dir, { recursive: true });

  const fixtureHtmlPath = path.join(__dirname, 'verify-ui-fixture.html');
  const fixtureUrl = 'file:///' + fixtureHtmlPath.replace(/\\/g, '/');
  const { collectionId, taskId, failTaskId } = writeFixture(collectionsRoot, fixtureUrl);
  const { flowCollectionId, flowTaskId } = writeFlowFixture(collectionsRoot, datasetsRoot, fixtureUrl);

  console.log(`Scratch dir: ${scratch}`);
  console.log(`Launching Automata.App (panel CDP :${PANEL_PORT}, target CDP :${TARGET_PORT})...`);

  const proc = spawn(exePath, [], {
    cwd: path.dirname(exePath),
    env: {
      ...process.env,
      AUTOMATA_PANEL_CDP_PORT: String(PANEL_PORT),
      AUTOMATA_TARGET_CDP_PORT: String(TARGET_PORT),
      AUTOMATA_PANEL_PROFILE_DIR: panelProfile,
      AUTOMATA_TARGET_PROFILE_DIR: targetProfile,
      AUTOMATA_COLLECTIONS_ROOT: collectionsRoot,
      AUTOMATA_DATASETS_ROOT: datasetsRoot,
      AUTOMATA_RUNS_ROOT: path.join(scratch, 'runs'),
      AUTOMATA_SCHEDULE_PATH: schedulePath,
      AUTOMATA_PARKED_ROOT: parkedRoot,
      AUTOMATA_LIVE_ROOT: liveRoot,
      AUTOMATA_DEMOS_ROOT: demosRoot,
      AUTOMATA_SETTINGS_PATH: path.join(scratch, 'settings.json'),
    },
    stdio: 'ignore',
  });

  const results = [];
  async function group(name, fn) {
    try {
      await fn();
      results.push({ name, ok: true });
      console.log(`[PASS] ${name}`);
    } catch (err) {
      results.push({ name, ok: false, err });
      console.log(`[FAIL] ${name}: ${err.message}`);
    }
  }

  let panelBrowser, targetBrowser;
  try {
    await waitForHttp200(PANEL_PORT, 'panel CDP endpoint');
    await waitForHttp200(TARGET_PORT, 'target CDP endpoint');

    panelBrowser = await chromium.connectOverCDP(`http://127.0.0.1:${PANEL_PORT}`);
    targetBrowser = await chromium.connectOverCDP(`http://127.0.0.1:${TARGET_PORT}`);
    const panelPage = await firstPage(panelBrowser);
    const targetPage = await firstPage(targetBrowser);
    await panelPage.waitForLoadState('domcontentloaded');
    useNudgePage(panelPage);

    // The fixture task exists but starts collapsed/unselected — select it to expand its step
    // tree (mirrors a user clicking the task row).
    await waitFor(() => panelPage.locator(`.node.task[data-task="${taskId}"]`).count().then((n) => n > 0),
      { timeoutMs: 10000, label: 'the fixture task to appear in the tree' });
    await panelPage.locator(`.node.task[data-task="${taskId}"] .name`).click();
    await waitFor(() => panelPage.locator('.insert-zone[data-index="1"]').count().then((n) => n > 0),
      { timeoutMs: 10000, label: 'the fixture task\'s step tree to render' });

    const gap = panelPage.locator('.insert-zone[data-index="1"]');

    // ---- accessibility baseline (WCAG 2.2 AA) -------------------------------------------------
    // These run first, against the untouched initial render, so a regression here can't be
    // masked by state the later interaction checks create.

    await group('axe-core: no serious or critical violations on the panel', async () => {
      await panelPage.addScriptTag({ path: axeCorePath });
      const violations = await panelPage.evaluate(async () => {
        const res = await window.axe.run(document, {
          resultTypes: ['violations'],
          runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'] },
        });
        return res.violations
          .filter((v) => v.impact === 'serious' || v.impact === 'critical')
          .map((v) => ({ id: v.id, impact: v.impact, help: v.help, nodes: v.nodes.slice(0, 3).map((n) => n.target.join(' ')) }));
      });
      assertTrue(violations.length === 0,
        `axe reported ${violations.length} serious/critical violation(s): ${JSON.stringify(violations, null, 2)}`);
    });

    await group('1.3.1: the tree exposes real ARIA tree semantics', async () => {
      assertEqual(await panelPage.locator('#tree[role="tree"]').count(), 1, '#tree should carry role="tree"');
      assertTrue(await panelPage.locator('#tree [role="treeitem"]').count() > 0, 'expected treeitem rows');
      const coll = panelPage.locator(`.node.collection[data-collection="${collectionId}"]`);
      assertEqual(await coll.getAttribute('aria-level'), '1', 'collection row should be aria-level 1');
      assertEqual(await coll.getAttribute('aria-expanded'), 'true', 'an open collection should be aria-expanded=true');
      const task = panelPage.locator(`.node.task[data-task="${taskId}"]`);
      assertEqual(await task.getAttribute('aria-level'), '2', 'task row should be aria-level 2');
      assertEqual(await task.getAttribute('aria-selected'), 'true', 'the selected task should be aria-selected=true');
      assertEqual(await panelPage.locator('#tree .node.step').first().getAttribute('aria-level'), '3',
        'a top-level step should be aria-level 3');
    });

    await group('2.1.1: exactly one tree row is tabbable (roving tabindex)', async () => {
      assertEqual(await panelPage.locator('#tree [role="treeitem"][tabindex="0"]').count(), 1,
        'expected exactly one row with tabindex=0');
    });

    await group('4.1.2: every icon-only row button has an accessible name', async () => {
      const unnamed = await panelPage.evaluate(() =>
        Array.prototype.slice.call(document.querySelectorAll('#tree .node-btns button'))
          .filter((b) => !(b.getAttribute('aria-label') || '').trim())
          .map((b) => b.getAttribute('data-op') || b.textContent));
      assertTrue(unnamed.length === 0, `row buttons with no accessible name: ${JSON.stringify(unnamed)}`);
    });

    await group('2.5.8: row buttons meet the 24x24 pointer-target minimum', async () => {
      // .node-btns is display:none until the row is hovered or focused, so measure while revealed
      // — a display:none button has no box and would measure 0x0.
      const undersized = await panelPage.evaluate(() => {
        const row = document.querySelector('#tree .node.step');
        row.focus();
        return Array.prototype.slice.call(row.querySelectorAll('.node-btns button'))
          .map((b) => {
            const r = b.getBoundingClientRect();
            return { op: b.getAttribute('data-op'), w: Math.round(r.width), h: Math.round(r.height) };
          })
          .filter((m) => m.w < 24 || m.h < 24);
      });
      assertTrue(undersized.length === 0, `buttons under 24x24: ${JSON.stringify(undersized)}`);
    });

    await group('2.5.8: revealing row buttons does not change the row height', async () => {
      // The 24px buttons only fit without a layout shift because .node reserves min-height.
      //
      // The baseline has to be a row with NOTHING revealed. An earlier version focused the row and
      // then read "before", by which point :focus-within had already shown the buttons - so it
      // compared a grown row against itself and passed while every row in the app was growing 2px
      // on hover. Blur and move the pointer off the tree first.
      await panelPage.mouse.move(0, 0);
      const shift = await panelPage.evaluate(() => {
        if (document.activeElement && document.activeElement.blur) document.activeElement.blur();
        const row = document.querySelector('#tree .node.step');
        const before = Math.round(row.getBoundingClientRect().height);
        row.focus();
        return { before, after: Math.round(row.getBoundingClientRect().height) };
      });
      assertEqual(shift.after, shift.before, `row height changed when its buttons appeared (${JSON.stringify(shift)})`);
    });

    await group('2.4.7: a :focus-visible rule exists in the stylesheet', async () => {
      const found = await panelPage.evaluate(() => {
        const hits = [];
        for (const sheet of Array.prototype.slice.call(document.styleSheets)) {
          let rules;
          try { rules = sheet.cssRules; } catch (e) { continue; }
          for (const r of Array.prototype.slice.call(rules)) {
            if (r.selectorText && r.selectorText.indexOf(':focus-visible') >= 0) hits.push(r.selectorText);
          }
        }
        return hits;
      });
      assertTrue(found.length > 0, 'no :focus-visible rule found — there is no visible focus indicator');
    });

    await group('1.3.1 / 4.1.2: the view tabs follow the ARIA tabs pattern', async () => {
      assertEqual(await panelPage.locator('[role="tablist"]').count(), 1, 'expected one tablist');
      const tabs = panelPage.locator('[role="tab"]');
      assertEqual(await tabs.count(), 4, 'expected Build / Schedule / Data / Runs');
      assertEqual(await panelPage.locator('[role="tab"][aria-selected="true"]').count(), 1,
        'exactly one tab may be selected');
      assertEqual(await panelPage.locator('[role="tab"][tabindex="0"]').count(), 1,
        'roving tabindex: exactly one tab is tabbable');
      assertEqual(await panelPage.locator('#tab-build').getAttribute('aria-selected'), 'true',
        'Build must be the default tab');
      // Every tab must point at a real panel, and inactive panels must be truly hidden rather
      // than merely invisible.
      const wiring = await panelPage.evaluate(() =>
        Array.prototype.slice.call(document.querySelectorAll('[role="tab"]')).map((t) => {
          const panel = document.getElementById(t.getAttribute('aria-controls'));
          return {
            id: t.id,
            hasPanel: !!panel,
            role: panel ? panel.getAttribute('role') : null,
            hidden: panel ? panel.hidden : null,
            selected: t.getAttribute('aria-selected'),
          };
        }));
      for (const w of wiring) {
        assertTrue(w.hasPanel && w.role === 'tabpanel', `${w.id} does not control a tabpanel`);
        assertEqual(w.hidden, w.selected !== 'true', `${w.id}: panel hidden state should mirror selection`);
      }
    });

    await group('1.4.1: the selected tab is marked by more than colour', async () => {
      const marks = await panelPage.evaluate(() => {
        const on = document.querySelector('[role="tab"][aria-selected="true"]');
        const off = document.querySelector('[role="tab"][aria-selected="false"]');
        const cs = (el) => getComputedStyle(el);
        return {
          weightOn: cs(on).fontWeight, weightOff: cs(off).fontWeight,
          borderOn: cs(on).borderBottomColor, borderOff: cs(off).borderBottomColor,
        };
      });
      assertTrue(marks.weightOn !== marks.weightOff || marks.borderOn !== marks.borderOff,
        `selection is conveyed by colour alone: ${JSON.stringify(marks)}`);
    });

    await group('4.1.3: the log and the status region are live', async () => {
      assertEqual(await panelPage.locator('#log[aria-live="polite"]').count(), 1, '#log needs aria-live="polite"');
      assertEqual(await panelPage.locator('#sr-status[role="status"]').count(), 1, '#sr-status needs role="status"');
    });

    await group('1.4.1: step status is conveyed by glyph, not colour alone', async () => {
      const glyphs = await panelPage.locator('#tree .node.step .status').allTextContents();
      assertTrue(glyphs.length > 0 && glyphs.every((g) => g.trim().length > 0),
        `expected a text glyph in every status cell, got ${JSON.stringify(glyphs)}`);
      const named = await panelPage.evaluate(() =>
        Array.prototype.slice.call(document.querySelectorAll('#tree .node.step .status'))
          .every((el) => (el.getAttribute('aria-label') || '').trim().length > 0));
      assertTrue(named, 'each status glyph needs an aria-label so its meaning is not colour-only');
    });

    await group('row icons: collection 🗂️, task 📋, idle step ▫', async () => {
      const collIcon = await panelPage.locator(`.node.collection[data-collection="${collectionId}"] .icon`).textContent();
      assertTrue((collIcon ?? '').includes('🗂'), `expected the collection row icon to be 🗂️, got "${collIcon}"`);
      const taskIcon = await panelPage.locator(`.node.task[data-task="${taskId}"] .icon`).textContent();
      assertTrue((taskIcon ?? '').includes('📋'), `expected the task row icon to be 📋, got "${taskIcon}"`);
      const stepStatuses = await panelPage.locator('#tree .node.step .status').allTextContents();
      assertTrue(stepStatuses.length > 0 && stepStatuses.every((s) => s === '▫'),
        `expected idle steps to show ▫, got ${JSON.stringify(stepStatuses)}`);
    });

    await group('every row runs from its own wrench; the toolbar Run button is gone', async () => {
      assertEqual(await panelPage.locator('#btn-run').count(), 0, '#btn-run should no longer exist in the DOM');

      // One wrench per row, not a strip: the whole point of the change is that a row spends its
      // width on its name.
      for (const [what, selector] of [
        ['collection', `.node.collection[data-collection="${collectionId}"]`],
        ['task', `.node.task[data-task="${taskId}"]`],
      ]) {
        const row = panelPage.locator(selector);
        assertEqual(await row.locator('.node-btns button').count(), 1,
          `expected exactly one row button on the ${what} row`);

        await row.hover();
        await row.locator('[data-op="menu"]').click();
        await panelPage.locator('.row-menu').waitFor({ state: 'visible', timeout: 5000 });
        assertEqual(await panelPage.locator(`.row-menu [data-op="run-${what}"]`).count(), 1,
          `expected the ${what} menu to offer its run`);

        // Escape closes it and hands focus back to the wrench it came from — a menu you can only
        // leave with the mouse is a trap.
        await panelPage.keyboard.press('Escape');
        await waitFor(() => panelPage.locator('.row-menu').count().then((n) => n === 0),
          { timeoutMs: 5000, label: `the ${what} menu to close on Escape` });
        assertEqual(
          await panelPage.evaluate(() => document.activeElement?.getAttribute('data-op')),
          'menu', 'focus should return to the wrench the menu came from');
      }
    });

    await group('row menu: arrow keys move through it, and only one is ever open', async () => {
      const collectionRow = panelPage.locator(`.node.collection[data-collection="${collectionId}"]`);
      const taskRow = panelPage.locator(`.node.task[data-task="${taskId}"]`);

      await collectionRow.hover();
      await collectionRow.locator('[data-op="menu"]').click();
      await panelPage.locator('.row-menu').waitFor({ state: 'visible', timeout: 5000 });

      // Opening focuses the first item, so the menu is usable without touching the mouse again.
      // A tooltip left on screen under the menu would be captioning the menu with the name of the
      // button that opened it — the same words, twice, one of them behind the other.
      await waitFor(() => panelPage.locator('#ma-tooltip:visible').count().then((n) => n === 0),
        { timeoutMs: 3000, label: "the wrench's tooltip to give way to the menu it opened" });

      const first = await panelPage.evaluate(() => document.activeElement?.getAttribute('data-op'));
      assertEqual(first, 'run-collection', 'opening a menu should focus its first item');

      await panelPage.keyboard.press('ArrowDown');
      assertEqual(await panelPage.evaluate(() => document.activeElement?.getAttribute('data-op')),
        'add-task', 'ArrowDown should move to the next item');

      // Separators are not stops. A keyboard user pressing Down twice must land on two commands,
      // not on a horizontal rule.
      await panelPage.keyboard.press('ArrowDown');
      assertEqual(await panelPage.evaluate(() => document.activeElement?.getAttribute('data-op')),
        'ren-collection', 'a separator must not take focus');

      // Up from the first item wraps to the last, which is the ARIA menu pattern.
      await panelPage.keyboard.press('Home');
      await panelPage.keyboard.press('ArrowUp');
      assertEqual(await panelPage.evaluate(() => document.activeElement?.getAttribute('data-op')),
        'del-collection', 'ArrowUp from the first item should wrap to the last');

      // Opening another row's menu replaces this one rather than stacking beside it.
      await taskRow.hover();
      await taskRow.locator('[data-op="menu"]').click();
      await panelPage.locator('.row-menu [data-op="run-task"]').waitFor({ state: 'visible', timeout: 5000 });
      assertEqual(await panelPage.locator('.row-menu').count(), 1,
        'two menus must never be open at once');

      await panelPage.keyboard.press('Escape');
      await waitFor(() => panelPage.locator('.row-menu').count().then((n) => n === 0),
        { timeoutMs: 5000, label: 'the menu to close' });

      // Shift+F10 on a focused row opens the same menu — there is no mouse-only path to any of
      // these operations, and no second list assembled for the keyboard that could fall behind it.
      await taskRow.click();
      await panelPage.locator(`.node.task[data-task="${taskId}"]`).focus();
      await panelPage.keyboard.press('Shift+F10');
      await panelPage.locator('.row-menu [data-op="run-task"]').waitFor({ state: 'visible', timeout: 5000 });
      await panelPage.keyboard.press('Escape');
      await waitFor(() => panelPage.locator('.row-menu').count().then((n) => n === 0),
        { timeoutMs: 5000, label: 'the keyboard-opened menu to close' });
    });

    await group('hover gap: hr-line appears, and nothing around it moves', async () => {
      // Every row's geometry, not just the gap's: revealing the label must not shift the strip OR
      // nudge the steps either side of it, which is what "the gap causes a padding shift" looks
      // like from the outside.
      const geometry = () => panelPage.evaluate(() =>
        Array.prototype.slice.call(document.querySelectorAll('#tree .node, #tree .insert-zone'))
          .map((el) => {
            const r = el.getBoundingClientRect();
            return [Math.round(r.top), Math.round(r.left), Math.round(r.width), Math.round(r.height)].join(',');
          }).join(' | '));

      // Scrolled into view FIRST: hovering would otherwise scroll it there itself, and every row
      // would shift by the same amount. A uniform shift is the tree scrolling, not the gap pushing
      // its neighbours around — which is the only thing this check is about.
      await gap.scrollIntoViewIfNeeded();
      await panelPage.mouse.move(0, 0);
      await panelPage.evaluate(() => {
        if (document.activeElement && document.activeElement.blur) document.activeElement.blur();
      });
      await sleep(100);
      const before = await geometry();
      await gap.hover();
      await sleep(100);
      assertEqual(await geometry(), before,
        'revealing the insert-zone label moved something in the tree');

      // The hover indicator is a plain hr-style line (::after), not a box/border — pseudo-element
      // styles aren't exposed via getComputedStyle on the element itself, so read them via a
      // page-side getComputedStyle(el, '::after') call instead.
      const lineBg = await gap.evaluate((el) => getComputedStyle(el, '::after').backgroundColor);
      assertTrue(lineBg !== 'rgba(0, 0, 0, 0)' && lineBg !== 'transparent',
        `expected the ::after line to have a visible background on hover, got "${lineBg}"`);

      // The label sits ON the line, on its own opaque ground, so the line stops either side of the
      // text instead of striking through it. Colour alone would not prove that — the patch has to
      // be opaque and above the line.
      const label = await gap.locator('span').evaluate((el) => {
        const style = getComputedStyle(el);
        return { background: style.backgroundColor, color: style.color, z: style.zIndex };
      });
      assertTrue(!/rgba\(0, 0, 0, 0\)|transparent/.test(label.background),
        `the label needs an opaque ground to mask the line, got "${label.background}"`);
      assertEqual(label.background, 'rgb(23, 23, 23)',
        'the label ground has to match the tree background, or it reads as a chip');
      assertEqual(label.color, 'rgb(224, 138, 46)', 'the label text stays the insert colour');
      assertTrue(Number(label.z) > 0, `the label must sit above the line, got z-index "${label.z}"`);
    });

    await group('click gap -> picker opens with Record option', async () => {
      await gap.click();
      await panelPage.locator('.action-pick.record-pick').waitFor({ state: 'visible', timeout: 5000 });
    });

    await group('click Record -> gap-active + Stop enabled', async () => {
      await panelPage.locator('.action-pick.record-pick').click();
      await waitForClass(gap, 'gap-active', 15000);
      await waitForEnabled(panelPage.locator('#btn-stop'), 5000);
    });

    await group('physical click in fixture page is captured', async () => {
      await targetPage.locator('#gamma').click();
      await targetPage.locator('.clicked[data-id="gamma"]').waitFor({ state: 'visible', timeout: 5000 });
    });

    await group('Stop -> new step spliced at the gap', async () => {
      await panelPage.locator('#btn-stop').click();
      // The panel can re-render more than once in quick succession while the saveTask round-trip
      // is in flight (an intermediate onState can briefly show the pre-splice tree) — poll for
      // the settled state (3 rows, the new one at index 1) rather than a single snapshot.
      let lastSeen = null;
      const label = await waitFor(
        async () => {
          const labels = await panelPage.locator('#tree .node.step .name').allTextContents();
          lastSeen = labels;
          return labels.length === 3 && /gamma/i.test(labels[1]) ? labels[1] : null;
        },
        { timeoutMs: 15000, label: `the new step to settle at index 1 (last seen: ${JSON.stringify(lastSeen)})` },
      ).catch((err) => { throw new Error(`${err.message}; last seen labels: ${JSON.stringify(lastSeen)}`); });
      assertTrue(/gamma/i.test(label), `expected the new step's label to mention "gamma", got "${label}"`);
    });

    await group('run stays paused after insertion', async () => {
      await waitForEnabled(panelPage.locator('#btn-continue'), 5000);
      assertTrue(!(await panelPage.locator('#btn-cancel-run').isDisabled()), 'Cancel button should still be enabled');
    });

    await group('Continue resumes and finishes playing the rest of the task', async () => {
      await panelPage.locator('#btn-continue').click();
      // The original next step (Beta) should actually execute — not just stay parked — and the
      // run should reach a normal, successful conclusion afterward.
      await waitFor(
        async () => {
          const labels = await panelPage.locator('#tree .node.step .name').allTextContents();
          const betaIdx = labels.findIndex((l) => /beta/i.test(l));
          if (betaIdx < 0) return false;
          const cls = await panelPage.locator('#tree .node.step').nth(betaIdx).getAttribute('class');
          return /\bst-passed\b/.test(cls ?? '');
        },
        { timeoutMs: 10000, label: 'the Beta step to actually run and pass after Continue' },
      );
      await waitFor(() => panelPage.locator('#btn-cancel-run').isDisabled(),
        { timeoutMs: 10000, label: 'the run to finish (Cancel button disabled again)' });
    });

    await group('run-collection: continues past a failing task, reports a summary', async () => {
      await clickRowOp(panelPage.locator(`.node.collection[data-collection="${collectionId}"]`), 'run-collection');
      await waitFor(
        async () => {
          if (!(await panelPage.locator(`.node.task[data-task="${failTaskId}"]`).count())) return false;
          const cls = await panelPage.locator('#tree .node.step').last().getAttribute('class');
          return /\bst-failed\b/.test(cls ?? '');
        },
        { timeoutMs: 15000, label: "Fail Task's step to reach a failed status" },
      );
      await waitFor(
        () => panelPage.locator('#log').locator('div', { hasText: /\d+\/\d+ task\(s\) passed/ }).count().then((n) => n > 0),
        { timeoutMs: 10000, label: 'a "N/M task(s) passed" summary line in the log' },
      );
      await waitFor(() => panelPage.locator('#btn-cancel-run').isDisabled(),
        { timeoutMs: 10000, label: 'the collection run to finish' });
    });

    await group('onTaskStarted auto-expands/selects the active task', async () => {
      // Fail Task was never manually clicked/expanded anywhere above — only onTaskStarted's
      // auto-select could have put it in this state, proving the collection run announced it.
      await waitForClass(panelPage.locator(`.node.task[data-task="${failTaskId}"]`), 'selected', 5000);
      const stepCount = await panelPage.locator('#tree .node.step').count();
      assertEqual(stepCount, 4, "expected Fail Task's step to be auto-expanded alongside Insert Fixture's 3 steps");
    });

    await group('Cancel stops a collection run instead of continuing to the next task', async () => {
      // RunCollectionAsync always logs a "N/M task(s) passed" summary once the loop exits —
      // whether it broke early or ran to completion — and since Fail Task fails in an uncancelled
      // run too, the pass count alone can't distinguish "stopped early" from "ran normally". The
      // real signal: onTaskStarted only fires for a task the loop actually reaches, so select Task
      // 1 as a known baseline first, then confirm Task 2 (Fail Task) never becomes selected —
      // proving the loop never reached it.
      await panelPage.locator(`.node.task[data-task="${taskId}"] .name`).click();
      await waitForClass(panelPage.locator(`.node.task[data-task="${taskId}"]`), 'selected', 5000);

      // Fire both clicks in one browser-side tick (native element.click(), not Playwright's own
      // actionability-waiting click()) so there's no Node/CDP round-trip between them — the
      // fixture's local tasks run fast enough that even one extra await could let the whole
      // collection finish before Cancel lands, which would test nothing. #btn-cancel-run starts
      // disabled (no run yet) and a disabled element's click() doesn't fire, so post the
      // underlying message directly instead, exactly as its click handler would.
      // The run is started from the collection's menu now, so the menu is opened FIRST and only
      // the item click shares a tick with the cancel — opening it in the same tick would leave
      // nothing to click.
      const collectionRow = panelPage.locator(`.node.collection[data-collection="${collectionId}"]`);
      await collectionRow.hover();
      await collectionRow.locator('[data-op="menu"]').click();
      await panelPage.locator('.row-menu [data-op="run-collection"]').waitFor({ state: 'visible', timeout: 5000 });
      await panelPage.evaluate(() => {
        document.querySelector('.row-menu [data-op="run-collection"]').click();
        window.chrome.webview.postMessage({ action: 'cancelRun' });
      });
      await waitFor(() => panelPage.locator('#btn-cancel-run').isDisabled(),
        { timeoutMs: 10000, label: 'the cancelled collection run to actually stop' });
      await sleep(300);
      assertTrue(!(await hasClass(panelPage.locator(`.node.task[data-task="${failTaskId}"]`), 'selected')),
        'Fail Task became selected (onTaskStarted fired for it) — Cancel did not stop the collection before task 2 began');
    });

    await group('Settings modal opens, is interactable, and closes without saving', async () => {
      await panelPage.locator('#btn-settings').click();
      await panelPage.locator('#llm-claude').waitFor({ state: 'visible', timeout: 5000 });
      assertTrue(await panelPage.locator('#key-openai').isVisible(), 'expected the OpenAI key input to be visible');
      assertTrue(await panelPage.locator('#set-radius').isVisible(), 'expected the border-radius slider to be visible');
      await panelPage.locator('#settings-modal-close').click();
      await waitFor(() => hasClass(panelPage.locator('#settings-modal'), 'hidden'),
        { timeoutMs: 5000, label: 'settings modal to close' });
      await panelPage.locator('#btn-settings').click();
      await waitFor(async () => !(await hasClass(panelPage.locator('#settings-modal'), 'hidden')),
        { timeoutMs: 5000, label: 'settings modal to reopen' });
      await panelPage.keyboard.press('Escape');
      await waitFor(() => hasClass(panelPage.locator('#settings-modal'), 'hidden'),
        { timeoutMs: 5000, label: 'Escape to close the settings modal' });
    });

    await group('scoped settings: inherited renders read-only, override then reset round-trips', async () => {
      const taskFile = path.join(collectionsRoot, 'Verify', 'Insert Fixture.json');
      assertTrue(!readFileSync(taskFile, 'utf8').includes('"settings"'),
        'the fixture task should start with no settings node');

      await clickRowOp(panelPage.locator(`.node.task[data-task="${taskId}"]`), 'task-settings');
      await panelPage.locator('#modal-form .settings-field').first().waitFor({ state: 'visible', timeout: 5000 });

      // Every field starts inherited: read-only text plus an explicit Override button, never a
      // pre-filled editable control (the trap this dialog exists to avoid).
      const initial = await panelPage.evaluate(() => {
        const rows = Array.prototype.slice.call(document.querySelectorAll('#modal-form .settings-field'));
        return {
          rows: rows.length,
          allInherited: rows.every((r) => r.querySelector('.settings-value.inherited')),
          editableControls: rows.filter((r) => r.querySelector('input, select')).length,
          overrideButtons: rows.filter((r) => r.querySelector('[data-op="override"]')).length,
        };
      });
      assertTrue(initial.rows > 0, 'expected setting rows');
      assertTrue(initial.allInherited, 'every row should start in the inherited state');
      assertEqual(initial.editableControls, 0, 'an inherited row must not render an editable control');
      assertEqual(initial.overrideButtons, initial.rows, 'every inherited row needs an Override button');

      // Overriding takes ownership of the value without changing it.
      await panelPage.locator('.settings-field[data-key="selfHeal"] [data-op="override"]').click();
      await panelPage.locator('.settings-field[data-key="selfHeal"] .settings-value.overridden')
        .waitFor({ state: 'visible', timeout: 5000 });
      assertEqual(await panelPage.locator('.settings-field[data-key="selfHeal"] [data-op="reset"]').count(), 1,
        'an overridden row needs a named Reset action, not just an empty field');
      await waitFor(() => readFileSync(taskFile, 'utf8').includes('"selfHeal"'),
        { timeoutMs: 5000, label: 'the override to reach disk' });

      // Resetting removes it entirely - an override that overrides nothing must not linger.
      await panelPage.locator('.settings-field[data-key="selfHeal"] [data-op="reset"]').click();
      await panelPage.locator('.settings-field[data-key="selfHeal"] .settings-value.inherited')
        .waitFor({ state: 'visible', timeout: 5000 });
      await waitFor(() => !readFileSync(taskFile, 'utf8').includes('"settings"'),
        { timeoutMs: 5000, label: 'the settings node to be pruned from disk' });

      await panelPage.keyboard.press('Escape');
      await waitFor(() => hasClass(panelPage.locator('#modal'), 'hidden'),
        { timeoutMs: 5000, label: 'the settings dialog to close' });
    });

    // ---- keyboard operation (2.1.1, 2.1.2, 2.4.3, 2.4.7, 2.5.7) -------------------------------
    // Last, because the reorder check mutates the fixture (and puts it back).

    await group('2.1.1: arrow keys and Home move focus through the tree', async () => {
      const first = panelPage.locator('#tree [role="treeitem"]').first();
      await first.focus();
      const before = await panelPage.evaluate(() => document.activeElement.getAttribute('data-key'));
      await panelPage.keyboard.press('ArrowDown');
      const after = await panelPage.evaluate(() => document.activeElement.getAttribute('data-key'));
      assertTrue(after && after !== before, `ArrowDown did not move focus (still on ${after})`);
      await panelPage.keyboard.press('Home');
      assertEqual(await panelPage.evaluate(() => document.activeElement.getAttribute('data-key')), before,
        'Home should return focus to the first row');
    });

    await group('2.4.7: the keyboard-focused row actually shows a ring', async () => {
      await panelPage.locator('#tree [role="treeitem"]').first().focus();
      await panelPage.keyboard.press('ArrowDown');
      const ring = await panelPage.evaluate(() => {
        const cs = getComputedStyle(document.activeElement);
        return { style: cs.outlineStyle, width: cs.outlineWidth, color: cs.outlineColor };
      });
      // The ring is authored at 2px, but Chromium snaps outline width to whole device pixels,
      // so on a 125%-scaled display getComputedStyle reports 1.6px (2 device px). AA (2.4.7)
      // only requires a visible indicator - the >=2px minimum is AAA (2.4.13) - so assert
      // presence and a non-hairline width rather than an exact CSS pixel count.
      assertTrue(ring.style !== 'none' && parseFloat(ring.width) >= 1,
        `expected a visible focus ring on the keyboard-focused row, got ${JSON.stringify(ring)}`);
    });

    await group('2.5.7: Alt+ArrowDown reorders a step with no dragging', async () => {
      await panelPage.locator(`.node.task[data-task="${taskId}"] .name`).click();
      const before = await panelPage.locator('#tree .node.step .name').allTextContents();
      assertTrue(before.length >= 2, `need two steps to reorder, saw ${JSON.stringify(before)}`);
      await panelPage.locator('#tree .node.step').first().focus();
      await panelPage.keyboard.press('Alt+ArrowDown');
      await waitFor(async () => {
        const now = await panelPage.locator('#tree .node.step .name').allTextContents();
        return now[0] === before[1] && now[1] === before[0];
      }, { timeoutMs: 10000, label: 'the first two steps to swap' });
      // Leave the fixture as it was found.
      await panelPage.locator('#tree .node.step').nth(1).focus();
      await panelPage.keyboard.press('Alt+ArrowUp');
      await waitFor(async () => {
        const now = await panelPage.locator('#tree .node.step .name').allTextContents();
        return now[0] === before[0] && now[1] === before[1];
      }, { timeoutMs: 10000, label: 'the reorder to be undone' });
    });

    await group('2.5.7: a task can be moved between collections without dragging', async () => {
      await clickRowOp(panelPage.locator(`.node.task[data-task="${taskId}"]`), 'move-task');
      await panelPage.locator('#modal .modal-box').waitFor({ state: 'visible', timeout: 5000 });
      assertEqual(await panelPage.locator('#modal-title').textContent(), 'Move task',
        'expected the Move task dialog');
      await panelPage.keyboard.press('Escape');
      await waitFor(() => hasClass(panelPage.locator('#modal'), 'hidden'),
        { timeoutMs: 5000, label: 'the move dialog to close' });
    });

    await group('2.1.2 / 2.4.3: a dialog traps Tab and restores focus on close', async () => {
      await panelPage.locator('#btn-help').click();
      await panelPage.locator('#modal .modal-box').waitFor({ state: 'visible', timeout: 5000 });
      for (let i = 0; i < 6; i++) {
        await panelPage.keyboard.press('Tab');
        const inside = await panelPage.evaluate(() => !!document.activeElement.closest('#modal'));
        assertTrue(inside, `focus escaped the dialog after ${i + 1} Tab press(es)`);
      }
      await panelPage.keyboard.press('Escape');
      await waitFor(() => hasClass(panelPage.locator('#modal'), 'hidden'),
        { timeoutMs: 5000, label: 'the help dialog to close' });
      assertEqual(await panelPage.evaluate(() => document.activeElement.id), 'btn-help',
        'focus should return to the button that opened the dialog');
    });

    await group('2.1.1: tabs use manual activation - arrows move focus, Enter selects', async () => {
      await panelPage.locator('#tab-build').focus();
      await panelPage.keyboard.press('ArrowRight');
      assertEqual(await panelPage.evaluate(() => document.activeElement.id), 'tab-schedule',
        'ArrowRight should move focus to the next tab');
      assertEqual(await panelPage.locator('#tab-build').getAttribute('aria-selected'), 'true',
        'arrowing must NOT change selection (manual activation)');
      await panelPage.keyboard.press('Enter');
      await waitFor(async () => (await panelPage.locator('#tab-schedule').getAttribute('aria-selected')) === 'true',
        { timeoutMs: 5000, label: 'Enter to select the focused tab' });
      assertTrue(await panelPage.locator('#view-build').isHidden(), 'the Build panel should be hidden now');
      assertTrue(await panelPage.locator('#view-schedule').isVisible(), 'the Schedule panel should be visible');
      // Back to Build so later checks and the summary see the normal view.
      await panelPage.locator('#tab-build').click();
      await waitFor(async () => (await panelPage.locator('#tab-build').getAttribute('aria-selected')) === 'true',
        { timeoutMs: 5000, label: 'Build to be reselected' });
    });

    await group('bindings: capture an output, bind a later step to it, then unbind', async () => {
      const taskFile = path.join(collectionsRoot, 'Verify', 'Insert Fixture.json');
      const editorReady = (sel) => panelPage.locator(sel).waitFor({ state: 'attached', timeout: 5000 });

      // 1. Turn the first step into an extractText that publishes a named value.
      await panelPage.locator('#tree .node.step').first().click();
      await editorReady('#ed-action');
      await panelPage.locator('#ed-action').selectOption('extractText');
      await editorReady('#ed-output');
      await panelPage.locator('#ed-output').fill('total');
      await panelPage.locator('#ed-output').dispatchEvent('change');
      await waitFor(() => readFileSync(taskFile, 'utf8').includes('"total"'),
        { timeoutMs: 5000, label: 'the declared output to reach disk' });

      // 2. Give the second step a value field to bind.
      await panelPage.locator('#tree .node.step').nth(1).click();
      await editorReady('#ed-action');
      await panelPage.locator('#ed-action').selectOption('typeText');
      await editorReady('#ed-value');

      // 3. The toggle is hidden until its field is hovered or focused - that IS the affordance.
      assertTrue(await panelPage.locator('.binding-toggle').isHidden(),
        'the binding toggle should be hidden until the field is reached for');
      await panelPage.locator('#ed-value').focus();
      await panelPage.locator('.binding-toggle').click();

      // 4. The picker offers the earlier step's output - enumerated, never typed.
      await panelPage.locator('#modal-list .action-pick').first().waitFor({ state: 'visible', timeout: 5000 });
      const offered = await panelPage.locator('#modal-list .action-pick b').allTextContents();
      assertTrue(offered.some((o) => o.includes('total')),
        `expected the earlier step's output to be offered, got ${JSON.stringify(offered)}`);
      await panelPage.locator('#modal-list .action-pick').first().click();

      await panelPage.locator('.chip.bound').waitFor({ state: 'visible', timeout: 5000 });
      await waitFor(() => readFileSync(taskFile, 'utf8').includes('"stepOutput"'),
        { timeoutMs: 5000, label: 'the binding to reach disk' });
      assertEqual(await panelPage.locator('#ed-value').count(), 0,
        'a bound field renders a chip, not an editable literal');

      // 5. Unbinding restores the plain field and leaves no empty bindings object behind.
      await panelPage.locator('.chip.bound').click();
      await panelPage.locator('#modal-list .action-pick').first().waitFor({ state: 'visible', timeout: 5000 });
      await panelPage.locator('#modal-list .action-pick[data-value="clear"]').click();
      await waitFor(() => !readFileSync(taskFile, 'utf8').includes('"bindings"'),
        { timeoutMs: 5000, label: 'the binding to be removed from disk' });
      await editorReady('#ed-value');
    });

    await group('flow: a forEach step runs its substeps once per dataset row', async () => {
      await panelPage.locator(`.node.task[data-task="${flowTaskId}"] .name`).click();
      await clickRowOp(panelPage.locator(`.node.task[data-task="${flowTaskId}"]`), 'run-task');

      // The per-row log line is the proof the loop actually iterated rather than running once.
      await waitFor(
        () => panelPage.locator('#log').locator('div', { hasText: /row 2 of 2/ }).count().then((n) => n > 0),
        { timeoutMs: 20000, label: 'the second row of the dataset to be reached' },
      );
      await waitFor(() => panelPage.locator('#btn-cancel-run').isDisabled(),
        { timeoutMs: 15000, label: 'the loop run to finish' });

      const statuses = await panelPage.locator(`.node.task[data-task="${flowTaskId}"] ~ .node.step`)
        .evaluateAll((rows) => rows.map((r) => r.className));
      assertTrue(statuses.some((c) => /\bst-passed\b/.test(c)),
        `expected the loop's steps to pass, got ${JSON.stringify(statuses)}`);
    });

    await group('flow: a collecting write can start its dataset fresh each run', async () => {
      // The footgun this option closes: a loop that appends keeps the last run's rows, so running
      // a task twice doubles its results and says nothing.
      const taskFile = path.join(collectionsRoot, 'Verify', 'Insert Fixture.json');
      await panelPage.locator('#tree .node.step').first().click();
      await panelPage.locator('#ed-action').waitFor({ state: 'visible', timeout: 10000 });
      await panelPage.locator('#ed-action').selectOption('writeDataset');
      await panelPage.locator('#ed-write-append').waitFor({ state: 'visible', timeout: 10000 });

      assertTrue(await panelPage.locator('#ed-write-reset').isVisible(),
        'the reset option belongs beside append, where the decision is being made');

      await panelPage.locator('#ed-write-reset').check();
      await waitFor(() => readFileSync(taskFile, 'utf8').includes('"resetOnFirstWrite": true'),
        { timeoutMs: 5000, label: 'the reset flag to reach disk' });

      // It only means anything alongside append, so it goes away with it rather than sitting there
      // ticked and inert.
      await panelPage.locator('#ed-write-append').uncheck();
      await waitFor(() => panelPage.locator('#ed-write-reset-row').isHidden(),
        { timeoutMs: 5000, label: 'the reset option to withdraw with append' });
      await waitFor(() => !readFileSync(taskFile, 'utf8').includes('"resetOnFirstWrite": true'),
        { timeoutMs: 5000, label: 'the reset flag to be cleared on disk' });
    });

    // ---- harvest (extractAll) ------------------------------------------------------------
    // The point of these is that NOTHING here is typed. A harvest is built by clicking one
    // example in the page, and what that click resolved to is shown back as a count — so these
    // checks drive the real click in the real target pane and read the real count.

    await group('harvest: the examples are generated on first load, off in their own folder', async () => {
      // Written by the app at startup, into the scratch folder the hook points at — the check
      // that the developer's own Documents\Automata\Demos is never the thing being rewritten.
      await waitFor(() => Promise.resolve(existsSync(path.join(demosRoot, 'shop', 'search.html'))),
        { timeoutMs: 15000, label: 'the generated shop pages' });
      assertTrue(existsSync(path.join(demosRoot, 'buttons.html')), 'the buttons example page');
      const demoDir = path.join(collectionsRoot, 'Demos');
      assertTrue(existsSync(demoDir), 'the generated Demos collection');
      const seeded = readdirSync(demoDir).filter((f) => f !== 'collection.json').sort();
      assertTrue(seeded.length >= 3, `expected the example tasks, got ${JSON.stringify(seeded)}`);
    });

    await group('harvest: picking one item in the page becomes the whole list', async () => {
      // A fresh step on the flow fixture task, switched to extractAll through the editor's own
      // action dropdown — "+ add step" creates a plain click step rather than opening the picker.
      await panelPage.locator(`.node.task[data-task="${flowTaskId}"] .name`).click();
      await clickRowOp(panelPage.locator(`.node.task[data-task="${flowTaskId}"]`), 'add-step');
      await panelPage.locator('#ed-action').waitFor({ state: 'visible', timeout: 10000 });
      await panelPage.locator('#ed-action').selectOption('extractAll');

      await panelPage.locator('#ed-harvest-pick-row').waitFor({ state: 'visible', timeout: 10000 });

      // Nothing picked yet, so there is nothing to pick columns FROM — offering the button would
      // invite a click that cannot be answered.
      assertTrue(await panelPage.locator('#ed-harvest-add-field').isDisabled(),
        'the column picker must stay disabled until a row has been picked');

      // Put the generated results page in the target pane, then pick a tile.
      const searchUrl = 'file:///' + path.join(demosRoot, 'shop', 'search.html').replace(/\\/g, '/');
      await targetPage.goto(searchUrl);
      await targetPage.locator('ul.results > li.product').first().waitFor({ state: 'visible', timeout: 10000 });

      await panelPage.locator('#ed-harvest-pick-row').click();
      await targetPage.locator('ul.results > li.product .title').first().click();

      await waitFor(async () => (await panelPage.locator('.harvest-rows').innerText()).includes('12'),
        { timeoutMs: 10000, label: 'the harvest to report how many items it matched' });
      const rowsText = await panelPage.locator('.harvest-rows').innerText();
      assertTrue(/li\.product/.test(rowsText),
        `expected the generalised row selector to be shown, got: ${rowsText}`);
      assertTrue(!/SKU-001/.test(rowsText),
        'the selector must be generalised away from the one tile that was clicked, not pinned to it');
    });

    await group('harvest: a picked link becomes a column, without following the link', async () => {
      const before = targetPage.url();
      await panelPage.locator('#ed-harvest-add-field').click();
      await targetPage.locator('ul.results > li.product .title').first().click();

      await waitFor(() => panelPage.locator('.column-row[data-harvest-field]').count().then((n) => n > 0),
        { timeoutMs: 10000, label: 'the picked column to appear' });

      // The pick consumes the click. If it did not, picking a column inside a product tile would
      // navigate to that product and take the page out from under the harvest being built.
      assertEqual(targetPage.url(), before, 'picking a link must not follow it');

      const row = panelPage.locator('.column-row[data-harvest-field]').first();
      const source = await row.locator('.harvest-source').inputValue();
      assertEqual(source, 'href', 'a picked <a> should default to reading where its link goes');
      assertTrue((await row.locator('.column-name').inputValue()).length > 0,
        'a picked column needs a usable name straight away, or the next save drops it');
    });

    await group('harvest: re-picking the rows clears columns rather than keeping dead ones', async () => {
      // Column selectors are relative to the row set, so they cannot survive a different one.
      // Keeping them would leave selectors that quietly resolve to nothing on every row.
      await panelPage.locator('#ed-harvest-pick-row').click();
      await targetPage.locator('ul.results > li.product .brand').nth(1).click();

      await waitFor(() => panelPage.locator('.column-row[data-harvest-field]').count().then((n) => n === 0),
        { timeoutMs: 10000, label: 'the columns to be cleared with the row set' });
      const log = await panelPage.locator('#log').innerText();
      assertTrue(/columns were cleared/i.test(log),
        'clearing the columns has to be said out loud, not done silently');
    });

    await group('harvest: the Examples dialog reports each example, and warns before replacing any', async () => {
      // Safe to open Settings now that AUTOMATA_SETTINGS_PATH isolates it; before that hook
      // existed this dialog would have been reading the developer's real provider and API keys.
      await panelPage.locator('#btn-settings').click();
      await panelPage.locator('#set-regen-demos').click();
      await waitFor(async () => (await panelPage.locator('#demos-body').innerText()).includes('Shop prices'),
        { timeoutMs: 10000, label: 'the examples to be surveyed' });

      const body = await panelPage.locator('#demos-body').innerText();
      assertTrue(/up to date/.test(body), `expected the untouched examples to read as current: ${body}`);
      // Regenerating is wholesale — there is nothing to choose per example, and the dialog must
      // not imply otherwise by offering controls.
      assertEqual(await panelPage.locator('.demo-choices, .demo-row input').count(), 0,
        'the dialog offers a warning, not a negotiation');
      // Freshly generated, so nothing is at stake and there is nothing to warn about yet.
      assertEqual(await panelPage.locator('.demo-warning').count(), 0,
        'a warning with nothing to lose behind it teaches people to ignore warnings');
      assertTrue(/Nothing here has been changed/.test(body), 'the dialog should say nothing is at stake');
      assertTrue(body.includes(demosRoot) || /demos/i.test(body), 'the dialog should name where pages are written');

      // Opening this closes Settings rather than stacking on top of it — two live modals would
      // fight over the focus trap, and the button that opened this one lives inside the other.
      assertTrue(await hasClass(panelPage.locator('#settings-modal'), 'hidden'),
        'Settings should have closed when the Examples dialog opened');

      await panelPage.locator('#demos-modal-close').click();
      await waitFor(() => hasClass(panelPage.locator('#demos-modal'), 'hidden'),
        { timeoutMs: 5000, label: 'the examples dialog to close' });
    });

    await group('examples: an edited one is named in the warning before it is replaced', async () => {
      // Edit an example the way a user would — through the store, since the point is what the
      // dialog SAYS about it afterwards, not how it came to be edited.
      const demoFile = path.join(collectionsRoot, 'Demos', 'Click a button.json');
      const demo = JSON.parse(readFileSync(demoFile, 'utf8'));
      demo.steps[0].label = 'mine now';
      writeFileSync(demoFile, JSON.stringify(demo, null, 2), 'utf8');

      await panelPage.locator('#btn-settings').click();
      await panelPage.locator('#set-regen-demos').click();
      await waitFor(() => panelPage.locator('.demo-warning').count().then((n) => n > 0),
        { timeoutMs: 10000, label: 'the warning about the edited example' });

      const warning = await panelPage.locator('.demo-warning').innerText();
      assertTrue(/Click a button/.test(warning),
        `the warning has to NAME what is about to go, got: ${warning}`);
      assertTrue(/move or duplicate/i.test(warning),
        'and say what to do instead of offering a choice it does not have');
      assertEqual(await panelPage.locator('.demo-row-edited').count(), 1,
        'exactly the edited example is marked in the list');

      // And it does what it says.
      await panelPage.locator('#demos-regen').click();
      await waitFor(() => Promise.resolve(
        JSON.parse(readFileSync(demoFile, 'utf8')).steps[0].label !== 'mine now'),
        { timeoutMs: 10000, label: 'the example to be restored' });
    });

    await group('data tab: lists the dataset with its row and column counts', async () => {
      await panelPage.locator('#tab-data').click();
      await panelPage.locator('#dataset-list .dataset-row').first().waitFor({ state: 'visible', timeout: 10000 });
      const text = await panelPage.locator('#dataset-list').innerText();
      assertTrue(text.includes('skus.csv'), `expected skus.csv to be listed, got: ${text}`);
      assertTrue(/2 rows/.test(text), `expected the row count, got: ${text}`);
      await panelPage.locator('#tab-build').click();
      await waitFor(async () => (await panelPage.locator('#tab-build').getAttribute('aria-selected')) === 'true',
        { timeoutMs: 5000, label: 'Build to be reselected' });
    });

    await group('feature view: renders a task as Gherkin and flags a recorded one read-only', async () => {
      await clickRowOp(panelPage.locator(`.node.task[data-task="${taskId}"]`), 'task-feature');
      await panelPage.locator('#feature-text').waitFor({ state: 'visible', timeout: 5000 });

      const text = await panelPage.locator('#feature-text').inputValue();
      assertTrue(text.includes('Feature:'), `expected a feature file, got: ${text.slice(0, 120)}`);
      assertTrue(text.includes('Scenario:'), 'a task should render as a Scenario');
      // The fixture's targets carry a recorded identity (id + selector), which a written target
      // cannot express - so the view must say it is read-only rather than pretend otherwise.
      assertEqual(await panelPage.locator('#feature-text').getAttribute('readonly'), '',
        'the feature view should be read-only');
      assertTrue(await panelPage.locator('.diagnostics.warn').count() > 0,
        'a lossy feature view must explain why it is read-only');

      await panelPage.keyboard.press('Escape');
      await waitFor(() => hasClass(panelPage.locator('#modal'), 'hidden'),
        { timeoutMs: 5000, label: 'the feature view to close' });
    });

    await group('authoring: an empty description asks for one instead of calling out', async () => {
      await panelPage.locator('#advanced > summary').click();
      await panelPage.locator('#btn-draft').waitFor({ state: 'visible', timeout: 5000 });
      await panelPage.locator('#btn-draft').click();
      await panelPage.locator('#modal .modal-box').waitFor({ state: 'visible', timeout: 5000 });
      assertEqual(await panelPage.locator('#modal-title').textContent(), 'Describe it first',
        'an empty description should be caught in the panel, not sent to a model');
      await panelPage.keyboard.press('Escape');
      await waitFor(() => hasClass(panelPage.locator('#modal'), 'hidden'),
        { timeoutMs: 5000, label: 'the prompt to close' });
    });

    // The whole pipeline except the model itself: compile -> review -> insert. The model's half is
    // unit-tested against a scripted LLM; this proves the rest works in the real app.
    await group('authoring: hand-written Gherkin compiles, previews, and inserts', async () => {
      const feature = [
        'Feature: Drafted by verify',
        '',
        '  Scenario: Alpha then Beta',
        '    Given I open "https://example.invalid/"',
        '    And I click "Alpha"',
        '    And I extract text from "#total" as price',
        '    When price is not empty',
        '    And I click "Beta"',
      ].join('\n');

      await panelPage.evaluate((text) => {
        window.chrome.webview.postMessage({ action: 'compileFlow', featureText: text });
      }, feature);

      await panelPage.locator('#draft-feature').waitFor({ state: 'visible', timeout: 10000 });
      assertEqual(await panelPage.locator('.diagnostics[role="alert"]').count(), 0,
        'this feature should compile cleanly');

      // The preview shows the compiled tree, including the nesting the guard produced.
      const outline = await panelPage.locator('.draft-task pre').innerText();
      assertTrue(/When price/.test(outline), `expected the guard in the outline, got: ${outline}`);
      assertTrue(/\n\s{2,}Click/.test(outline),
        `the guard's substeps should be indented under it, got: ${outline}`);

      await panelPage.locator('#draft-insert').click();
      await waitFor(
        () => panelPage.locator('.node.collection .name', { hasText: 'Drafted by verify' }).count().then((n) => n > 0),
        { timeoutMs: 10000, label: 'the drafted collection to appear in the tree' },
      );

      const onDisk = readFileSync(
        path.join(collectionsRoot, 'Drafted by verify', 'Alpha then Beta.json'), 'utf8');
      assertTrue(onDisk.includes('"if"'), 'the guard should have compiled to an if step');
      assertTrue(onDisk.includes('"stepOutput"'), 'the condition should bind to the captured value');
    });

    await group('authoring: a bad phrase comes back with its line number', async () => {
      await panelPage.evaluate(() => {
        window.chrome.webview.postMessage({
          action: 'compileFlow',
          featureText: 'Feature: Bad\n  Scenario: S\n    Given I open "https://x.example"\n    And I frobnicate the widget\n',
        });
      });

      await panelPage.locator('.diagnostics[role="alert"]').waitFor({ state: 'visible', timeout: 10000 });
      const problems = await panelPage.locator('.diagnostics[role="alert"]').innerText();
      assertTrue(/line 4/.test(problems), `expected the offending line number, got: ${problems}`);
      assertTrue(/frobnicate/.test(problems), 'the diagnostic should quote what it could not match');
      assertEqual(await panelPage.locator('#draft-insert').count(), 0,
        'a draft that does not compile must not offer Insert');

      await panelPage.keyboard.press('Escape');
      await waitFor(() => hasClass(panelPage.locator('#modal'), 'hidden'),
        { timeoutMs: 5000, label: 'the draft preview to close' });
    });

    // ---- schedule tab ------------------------------------------------------------------------
    //
    // The point of these: a schedule is assembled from pickers and COMPILES to cron, the
    // expression is shown rather than hidden, and every "when does this fire" answer on screen
    // comes from the same evaluator the runner's tick obeys. Anything that cannot fire is refused
    // with a reason, because a schedule that quietly does nothing is this feature's worst
    // possible failure.

    const scheduleOnDisk = () =>
      (existsSync(schedulePath) ? JSON.parse(readFileSync(schedulePath, 'utf8')) : []);

    async function openScheduleTab() {
      await panelPage.locator('#tab-schedule').click();
      await waitFor(async () => (await panelPage.locator('#tab-schedule').getAttribute('aria-selected')) === 'true',
        { timeoutMs: 5000, label: 'the Schedule tab to be selected' });
      await panelPage.locator('#btn-new-schedule').waitFor({ state: 'visible', timeout: 10000 });
    }

    await group('schedule: a picker choice compiles to cron and reaches disk', async () => {
      await openScheduleTab();
      await panelPage.locator('#btn-new-schedule').click();
      await panelPage.locator('#modal-form [data-input="target"]').waitFor({ state: 'visible', timeout: 5000 });

      await panelPage.locator('[data-input="target"]').selectOption(`collection:${collectionId}`);
      // The name follows the target, so the common case needs no naming step.
      assertEqual(await panelPage.locator('[data-input="name"]').inputValue(), 'Verify',
        'the name should default to the chosen target');

      // A single trigger must not be dressed up as a list — no numbering, no remove button.
      assertEqual(await panelPage.locator('.trigger-block').count(), 1,
        'a new schedule starts with exactly one trigger');
      assertEqual(await panelPage.locator('[data-op="remove-trigger"]').count(), 0,
        'the only trigger is not removable — an entry with none runs solely by hand');

      await panelPage.locator('[data-input="when"]').selectOption('weekdays');
      await panelPage.locator('[data-input="time"]').fill('09:30');
      await panelPage.locator('[data-input="time"]').dispatchEvent('change');

      // Shown BEFORE saving: the expression is what gets stored and what the CLI prints, so it is
      // never hidden behind the picker that produced it.
      assertEqual((await panelPage.locator('#modal-form .scope-note code').textContent())?.trim(),
        '30 9 * * 1-5', 'the compiled cron expression should be shown before saving');
      // Nobody had to type a cron field to get here.
      assertEqual(await panelPage.locator('[data-input="cron"]').count(), 0,
        'a picker shape must not expose a raw cron box');

      await panelPage.locator('#modal-ok').click();
      const saved = await waitFor(() => { const all = scheduleOnDisk(); return all.length === 1 ? all[0] : null; },
        { timeoutMs: 10000, label: 'the schedule entry to reach disk' });

      assertEqual(saved.triggers[0].kind, 'cron', 'a picker shape should store a cron trigger');
      assertEqual(saved.triggers[0].cronExpression, '30 9 * * 1-5', 'the stored expression should be the compiled one');
      assertEqual(saved.target, 'collection', 'the entry should target the collection');
      assertEqual(saved.targetId, collectionId, 'the entry should name the chosen collection');
      assertTrue(!!saved.nextDueUtc, 'the entry should be given a written-down next-due time');

      // The row explains when it next fires, in words - and that answer came from the host.
      await panelPage.locator('.sched-row').first().waitFor({ state: 'visible', timeout: 5000 });
      const text = await panelPage.locator('#schedule-list').innerText();
      assertTrue(/first run in|next in|due/i.test(text), `expected a next-due explanation, got: ${text}`);
      assertTrue(/every weekday at 09:30/.test(text), `expected the shape spelled out, got: ${text}`);
    });

    await group('schedule: an expression that could never fire is refused with a reason', async () => {
      await openScheduleTab();
      await panelPage.locator('#btn-new-schedule').click();
      await panelPage.locator('#modal-form [data-input="target"]').waitFor({ state: 'visible', timeout: 5000 });
      await panelPage.locator('[data-input="target"]').selectOption(`collection:${flowCollectionId}`);
      await panelPage.locator('[data-input="when"]').selectOption('cron');
      await panelPage.locator('[data-input="cron"]').fill('0 9 31 2 *');   // 31 February
      await panelPage.locator('[data-input="cron"]').dispatchEvent('change');
      await panelPage.locator('#modal-ok').click();

      // Refused, and the editor comes back with the reason AND everything already typed.
      await panelPage.locator('#modal-form .diagnostics[role="alert"]').waitFor({ state: 'visible', timeout: 10000 });
      const reason = await panelPage.locator('#modal-form .diagnostics[role="alert"]').innerText();
      assertTrue(/never/i.test(reason), `expected an explanation of why it cannot fire, got: ${reason}`);
      assertEqual(await panelPage.locator('[data-input="cron"]').inputValue(), '0 9 31 2 *',
        'a refused save must not lose what was typed');
      assertEqual(scheduleOnDisk().length, 1, 'a refused entry must not be written to disk');

      await panelPage.locator('#modal-cancel').click();
      await waitFor(() => hasClass(panelPage.locator('#modal'), 'hidden'),
        { timeoutMs: 5000, label: 'the schedule editor to close' });
    });

    await group('schedule: one entry chained after another previews the order', async () => {
      await openScheduleTab();
      await panelPage.locator('#btn-new-schedule').click();
      await panelPage.locator('#modal-form [data-input="target"]').waitFor({ state: 'visible', timeout: 5000 });
      await panelPage.locator('[data-input="target"]').selectOption(`collection:${flowCollectionId}`);
      await panelPage.locator('[data-input="when"]').selectOption('after');
      await panelPage.locator('[data-input="afterEntryId"]').waitFor({ state: 'visible', timeout: 5000 });
      await panelPage.locator('#modal-ok').click();

      await waitFor(() => scheduleOnDisk().length === 2,
        { timeoutMs: 10000, label: 'the chained entry to reach disk' });
      const chained = scheduleOnDisk().find((e) => e.targetId === flowCollectionId);
      assertEqual(chained.triggers[0].kind, 'afterEntry', 'it should store an after-entry trigger');
      assertEqual(chained.triggers[0].requiredOutcome, 'succeeded', 'success should be the default upstream outcome');

      // The preview is computed by TriggerEvaluator.Chain - the same walk the tick follows.
      await panelPage.locator('.sched-chain').first().waitFor({ state: 'visible', timeout: 5000 });
      const chain = await panelPage.locator('.sched-chain').first().innerText();
      assertTrue(/Verify Flow/.test(chain), `expected the downstream entry named in the preview, got: ${chain}`);
      const detail = await panelPage.locator('#schedule-list').innerText();
      assertTrue(/after .Verify./.test(detail), `expected the upstream named on the chained row, got: ${detail}`);
    });

    await group('schedule: an entry can be started by several triggers, whichever comes first', async () => {
      // The model has always been a list and the evaluator has always taken the soonest firing
      // across it; this is the editor finally being able to write more than one.
      await openScheduleTab();
      await panelPage.locator('#btn-new-schedule').click();
      await panelPage.locator('#modal-form [data-input="target"]').waitFor({ state: 'visible', timeout: 5000 });
      await panelPage.locator('[data-input="target"]').selectOption(`task:${taskId}`);

      // Trigger 1: every day at 07:15.
      await panelPage.locator('[data-input="when"][data-trigger="0"]').selectOption('daily');
      await panelPage.locator('[data-input="time"][data-trigger="0"]').fill('07:15');

      await panelPage.locator('[data-op="add-trigger"]').click();
      assertEqual(await panelPage.locator('.trigger-block').count(), 2,
        'adding a trigger should add a block');
      assertEqual(await panelPage.locator('[data-op="remove-trigger"]').count(), 2,
        'with two, either can be removed');

      // Trigger 2: every 30 minutes. Each block edits its OWN trigger — the first must not move.
      await panelPage.locator('[data-input="when"][data-trigger="1"]').selectOption('minutes');
      await panelPage.locator('[data-input="everyMinutes"][data-trigger="1"]').fill('30');
      assertEqual(await panelPage.locator('[data-input="time"][data-trigger="0"]').inputValue(), '07:15',
        'editing the second trigger must not touch the first');

      // Each block shows its own compiled expression, and only the cron-shaped one has one.
      assertEqual(await panelPage.locator('#sched-cron-note-0').textContent(), '15 7 * * *');
      assertEqual(await panelPage.locator('#sched-cron-note-1').count(), 0,
        'an interval is not stored as cron, so it shows no expression');

      // Every control is still uniquely named, which three time pickers in one form would not be
      // by default.
      const duplicated = await panelPage.evaluate(() => {
        const names = Array.prototype.slice.call(document.querySelectorAll('#modal-form [aria-label]'))
          .map((el) => el.getAttribute('aria-label'));
        return names.filter((n, i) => names.indexOf(n) !== i);
      });
      assertEqual(JSON.stringify(duplicated), '[]',
        `two controls share an accessible name: ${JSON.stringify(duplicated)}`);

      assertTrue(/whichever comes first/i.test(await panelPage.locator('#modal-form').innerText()),
        'the form has to say these are alternatives, not steps');

      await panelPage.locator('#modal-ok').click();
      const saved = await waitFor(() => scheduleOnDisk().find((e) => e.targetId === taskId) ?? null,
        { timeoutMs: 10000, label: 'the multi-trigger entry to reach disk' });

      assertEqual(saved.triggers.length, 2, 'both triggers should have been stored');
      assertEqual(saved.triggers[0].kind, 'cron');
      assertEqual(saved.triggers[0].cronExpression, '15 7 * * *');
      assertEqual(saved.triggers[1].kind, 'interval');
      assertEqual(saved.triggers[1].intervalSeconds, 1800);

      // The row names both, and its next-due time is the SOONER of the two — computed host-side by
      // the same evaluator the tick obeys.
      const listed = await panelPage.locator(`.sched-row[data-entry="${saved.id}"] ~ .sched-detail`)
        .first().innerText();
      assertTrue(/every day at 07:15/.test(listed), `expected the first trigger described, got: ${listed}`);
      assertTrue(/every 30 minutes/.test(listed), `expected the second trigger described, got: ${listed}`);
      assertTrue(/, or /.test(listed),
        `several triggers are alternatives, and the row should say "or", got: ${listed}`);

      // Reopening reads both back as the shapes they were built with, not as raw cron.
      await panelPage.locator(`.sched-row[data-entry="${saved.id}"] [data-op="edit"]`).click();
      await panelPage.locator('#modal-form [data-input="target"]').waitFor({ state: 'visible', timeout: 5000 });
      assertEqual(await panelPage.locator('.trigger-block').count(), 2,
        'both triggers should reopen');
      assertEqual(await panelPage.locator('[data-input="when"][data-trigger="0"]').inputValue(), 'daily',
        'a daily cron should reopen as "every day at", not as a raw expression');
      assertEqual(await panelPage.locator('[data-input="everyMinutes"][data-trigger="1"]').inputValue(), '30');

      // Removing one leaves the other, and the numbering goes away with the list.
      await panelPage.locator('[data-op="remove-trigger"][data-trigger="0"]').click();
      assertEqual(await panelPage.locator('.trigger-block').count(), 1);
      assertEqual(await panelPage.locator('[data-input="everyMinutes"][data-trigger="0"]').inputValue(), '30',
        'the trigger that survived should be the one that was not removed');

      await panelPage.locator('#modal-cancel').click();
      await waitFor(() => hasClass(panelPage.locator('#modal'), 'hidden'),
        { timeoutMs: 5000, label: 'the schedule editor to close' });
      assertEqual(scheduleOnDisk().find((e) => e.targetId === taskId).triggers.length, 2,
        'cancelling must leave what was saved alone');
    });

    await group('schedule: pausing an entry keeps its trigger and says so in words', async () => {
      await openScheduleTab();
      // Named rather than positional: other groups add entries, and a positional pick would
      // silently start testing a different schedule.
      const entryId = scheduleOnDisk().find((e) => e.targetId === collectionId).id;
      await panelPage.locator(`.sched-row[data-entry="${entryId}"] [data-op="toggle"]`).click();

      const target = () => scheduleOnDisk().find((e) => e.id === entryId);
      await waitFor(() => target().enabled === false,
        { timeoutMs: 10000, label: 'the entry to be paused on disk' });
      assertEqual(target().triggers[0].cronExpression, '30 9 * * 1-5',
        'pausing must not rewrite the trigger');

      const paused = panelPage.locator(`.sched-row[data-entry="${entryId}"]`);
      await waitFor(() => hasClass(paused, 'off'), { timeoutMs: 5000, label: 'the row to read as paused' });
      assertTrue(/disabled/.test(await paused.innerText()),
        'a paused entry must say so in words, not by colour alone');

      await panelPage.locator(`.sched-row[data-entry="${entryId}"] [data-op="toggle"]`).click();
      await waitFor(() => target().enabled === true,
        { timeoutMs: 10000, label: 'the entry to be resumed' });
    });

    await group('schedule: every row control is named, and 24x24', async () => {
      await openScheduleTab();
      const unnamed = await panelPage.evaluate(() =>
        Array.prototype.slice.call(document.querySelectorAll('#schedule-list button'))
          .filter((b) => !(b.getAttribute('aria-label') || '').trim())
          .map((b) => b.getAttribute('data-op') || b.textContent));
      assertEqual(JSON.stringify(unnamed), '[]',
        `schedule row buttons with no accessible name: ${JSON.stringify(unnamed)}`);

      const undersized = await panelPage.evaluate(() =>
        Array.prototype.slice.call(document.querySelectorAll('#schedule-list button'))
          .map((b) => {
            const r = b.getBoundingClientRect();
            return { op: b.getAttribute('data-op'), w: Math.round(r.width), h: Math.round(r.height) };
          })
          .filter((m) => m.w < 24 || m.h < 24));
      assertEqual(JSON.stringify(undersized), '[]', `schedule buttons under 24x24: ${JSON.stringify(undersized)}`);

      const glyphsNamed = await panelPage.evaluate(() =>
        Array.prototype.slice.call(document.querySelectorAll('#schedule-list .status'))
          .every((el) => (el.getAttribute('aria-label') || '').trim().length > 0));
      assertTrue(glyphsNamed, 'each schedule glyph needs an accessible name');
    });

    await group('schedule: a scheduled collection is chipped in the tree, without growing the row', async () => {
      await panelPage.locator('#tab-build').click();
      await waitFor(async () => (await panelPage.locator('#tab-build').getAttribute('aria-selected')) === 'true',
        { timeoutMs: 5000, label: 'Build to be reselected' });

      const chip = panelPage.locator(`.node.collection[data-collection="${collectionId}"] .chip.sched`);
      await chip.waitFor({ state: 'visible', timeout: 10000 });
      const label = await chip.getAttribute('aria-label');
      assertTrue(/every weekday at 09:30/.test(label || ''),
        `the chip must say what schedules it, not just that something does: got "${label}"`);

      // Every scheduled row would be taller than every unscheduled one if the chip did not stay
      // inside the 28px the row already reserves for its buttons.
      const heights = await panelPage.evaluate(() =>
        Array.prototype.slice.call(document.querySelectorAll('#tree .node.collection'))
          .map((el) => Math.round(el.getBoundingClientRect().height)));
      assertEqual(new Set(heights).size, 1,
        `a chipped collection row is a different height from an unchipped one: ${JSON.stringify(heights)}`);
    });

    await group('runs tab: a finished run is recorded and listed', async () => {
      await panelPage.locator('#tab-runs').click();
      await panelPage.locator('#run-list .run-row').first().waitFor({ state: 'visible', timeout: 10000 });

      const text = await panelPage.locator('#run-list').innerText();
      // Several runs happened above; the fixture task is the one we can name for certain.
      assertTrue(/Insert Fixture|Verify|Loop/.test(text), `expected a recorded run, got: ${text.slice(0, 200)}`);
      assertTrue(/passed|failed/.test(text), 'each run should report its outcome in words, not colour alone');

      // The list is read from disk, which is what lets it show runs this window never saw.
      const onDisk = readdirSync(path.join(scratch, 'runs'));
      assertTrue(onDisk.length > 0, 'runs should have been written to the run store');

      const named = await panelPage.evaluate(() =>
        Array.prototype.slice.call(document.querySelectorAll('#run-list .status'))
          .every((el) => (el.getAttribute('aria-label') || '').trim().length > 0));
      assertTrue(named, 'each run status glyph needs an accessible name');

      await panelPage.locator('#tab-build').click();
      await waitFor(async () => (await panelPage.locator('#tab-build').getAttribute('aria-selected')) === 'true',
        { timeoutMs: 5000, label: 'Build to be reselected' });
    });

    await group('runs tab: a parked run reads as parked, not as still running', async () => {
      // A parked run's manifest is still OPEN — success is null — so on its own it is
      // indistinguishable from one that is executing. This seeds both halves the app has to join
      // (the open manifest and the parked record) and checks the tab explains itself rather than
      // showing an overnight run as "running" with no reason given.
      const runId = 'ab12cd34ef56ab78cd90ef12ab34cd56';
      const started = new Date(Date.now() - 3 * 3600_000);
      const stamp = (d) => `${d.getFullYear()}${String(d.getMonth() + 1).padStart(2, '0')}` +
        `${String(d.getDate()).padStart(2, '0')}-${String(d.getHours()).padStart(2, '0')}` +
        `${String(d.getMinutes()).padStart(2, '0')}${String(d.getSeconds()).padStart(2, '0')}`;
      const runDir = path.join(scratch, 'runs', `${stamp(started)}-nightly-${runId.slice(0, 8)}`);
      mkdirSync(runDir, { recursive: true });
      writeFileSync(path.join(runDir, 'manifest.json'), JSON.stringify({
        schemaVersion: 1, runId, target: 'task', targetId: taskId, targetName: 'Nightly',
        trigger: 'schedule', startedUtc: started.toISOString(),
      }, null, 2));

      const resumeAt = new Date(Date.now() + 6 * 3600_000);
      mkdirSync(parkedRoot, { recursive: true });
      writeFileSync(path.join(parkedRoot, `${runId}.json`), JSON.stringify({
        schemaVersion: 1, runId, target: 'task', targetName: 'Nightly', trigger: 'schedule',
        taskId, taskName: 'Nightly batch', collectionId,
        remainingTaskIds: [], tasksPassed: 0, totalTasks: 1,
        parkedAtUtc: started.toISOString(), resumeCount: 0,
        checkpoint: {
          resumeAtUtc: resumeAt.toISOString(),
          reason: 'a wait until 09:00 (UTC)',
          resumePath: [1], resumeStepId: 'w', stepLabel: 'Wait until 09:00',
          outputs: [], variables: {}, passed: 1, healed: 0,
        },
      }, null, 2));

      await panelPage.locator('#tab-runs').click();
      await panelPage.locator('#btn-refresh-runs').click();

      const row = panelPage.locator(`.run-row[data-run="${runId}"]`);
      await row.waitFor({ state: 'visible', timeout: 10000 });
      assertTrue(await hasClass(row, 'st-parked'),
        `expected the row to read as parked, got class "${await row.getAttribute('class')}"`);
      assertTrue(/parked/i.test(await row.innerText()),
        'parked has to be said in words, not carried by the glyph and colour alone');

      const note = await panelPage.locator('.parked-note').first().innerText();
      assertTrue(/Wait until 09:00/.test(note), `the note should name the step it parked on, got: ${note}`);
      assertTrue(/no browser is held/i.test(note),
        `the note should say the browser was released — that is the whole point, got: ${note}`);
      assertTrue(/Resumes/.test(note), `the note should say when it resumes, got: ${note}`);

      await panelPage.locator('#tab-build').click();
      await waitFor(async () => (await panelPage.locator('#tab-build').getAttribute('aria-selected')) === 'true',
        { timeoutMs: 5000, label: 'Build to be reselected' });
    });

    await group('runs tab: the live lane strip shows another process’s browsers, and only real ones', async () => {
      // The lanes worth watching are never this window's - it has one browser pane and no pool.
      // They belong to automata-runner, so this seeds what that process would have published.
      //
      // The pid is THIS node process's, with its real start time: the reader deliberately checks
      // liveness against the operating system rather than trusting the file, and a fixture that
      // used a made-up pid would be discarded as a phantom before it ever rendered.
      const alivePid = process.pid;
      const aliveStarted = new Date(Date.now() - process.uptime() * 1000);
      mkdirSync(liveRoot, { recursive: true });
      writeFileSync(path.join(liveRoot, `${alivePid}.json`), JSON.stringify({
        schemaVersion: 1,
        processId: alivePid,
        processStartedUtc: aliveStarted.toISOString(),
        processName: 'automata-runner',
        targetName: 'Nightly batch',
        runId: 'ab12cd34ef56ab78cd90ef12ab34cd56',
        maxConcurrency: 3,
        updatedUtc: new Date().toISOString(),
        lanes: [
          {
            laneId: 'lane-1', profileKey: 'default', busy: true, runId: 'r1',
            taskName: 'Wolf Tshirts', currentStepLabel: 'Click Images',
            startedUtc: new Date(Date.now() - 42_000).toISOString(),
          },
          {
            laneId: 'lane-2', profileKey: 'default', busy: false, runId: null,
            taskName: null, currentStepLabel: null, startedUtc: null,
          },
        ],
      }, null, 2));

      // And a file for a process that is definitely gone. It must not render, and must not
      // survive the read - a monitor that shows work which is not happening is worse than one
      // that shows nothing.
      const deadPid = 999999;
      writeFileSync(path.join(liveRoot, `${deadPid}.json`), JSON.stringify({
        schemaVersion: 1,
        processId: deadPid,
        processStartedUtc: new Date(Date.now() - 7200_000).toISOString(),
        processName: 'automata-runner',
        targetName: 'A run that was killed',
        maxConcurrency: 1,
        updatedUtc: new Date().toISOString(),
        lanes: [{
          laneId: 'ghost-lane', profileKey: 'default', busy: true, runId: 'r0',
          taskName: 'Ghost task', currentStepLabel: 'Never finished', startedUtc: new Date().toISOString(),
        }],
      }, null, 2));

      // Selecting Runs is what starts the poll; nothing polls while its panel is off screen.
      await panelPage.locator('#tab-runs').click();
      await panelPage.locator('#lane-strip').waitFor({ state: 'visible', timeout: 15000 });

      // innerText is the RENDERED text, and .section-label is text-transform: uppercase — so the
      // heading assertions have to be case-insensitive.
      const strip = await panelPage.locator('#lane-strip').innerText();
      assertTrue(/Running now/i.test(strip), `expected the strip to say what it is, got: ${strip}`);
      assertTrue(/automata-runner/i.test(strip), `expected the owning process named, got: ${strip}`);
      assertTrue(/Wolf Tshirts/.test(strip), `expected the running task named, got: ${strip}`);
      assertTrue(/Click Images/.test(strip),
        `the step in flight is the point of a LIVE strip, got: ${strip}`);
      assertTrue(/1 of 3 lanes busy/.test(strip),
        `expected the busy count against the ceiling, got: ${strip}`);
      assertTrue(/1 warm/.test(strip),
        `a returned-but-open lane explains why the browser count exceeds the work, got: ${strip}`);
      assertEqual(await panelPage.locator('.lane-row').count(), 1,
        'only busy lanes are rows; a warm lane is a count, not work in flight');
      assertTrue(!/Ghost task/.test(strip), `a dead process must not appear as running, got: ${strip}`);

      assertTrue(!existsSync(path.join(liveRoot, `${deadPid}.json`)),
        'the dead process’s file should have been tidied away as it was read');

      const named = await panelPage.evaluate(() =>
        Array.prototype.slice.call(document.querySelectorAll('#lane-strip .status'))
          .every((el) => (el.getAttribute('aria-label') || '').trim().length > 0));
      assertTrue(named, 'each lane glyph needs an accessible name');

      // Leaving the tab stops the poll, and the strip goes with the panel.
      await panelPage.locator('#tab-build').click();
      await waitFor(async () => (await panelPage.locator('#tab-build').getAttribute('aria-selected')) === 'true',
        { timeoutMs: 5000, label: 'Build to be reselected' });
    });

    await group('3.2.6: a consistent help entry point exists and is named', async () => {
      const label = await panelPage.locator('#btn-help').getAttribute('aria-label');
      assertTrue(!!label && label.trim().length > 0, 'the help button needs an accessible name');
    });
  } finally {
    try { await panelBrowser?.close(); } catch { /* CDP-attached browsers may already be gone */ }
    try { await targetBrowser?.close(); } catch { /* same */ }
    const exited = new Promise((resolve) => proc.once('exit', resolve));
    proc.kill();
    if (clean) {
      // WebView2's profile files can stay briefly locked after the process is signaled to exit —
      // wait for the actual exit event (bounded), then retry the removal a few times as any
      // lingering handles (e.g. a GPU cache file) release.
      await Promise.race([exited, sleep(5000)]);
      let lastErr;
      for (let attempt = 0; attempt < 5; attempt++) {
        try {
          rmSync(scratch, { recursive: true, force: true });
          lastErr = null;
          break;
        } catch (err) {
          lastErr = err;
          await sleep(1000);
        }
      }
      if (lastErr) console.log(`(cleanup) could not fully remove scratch dir: ${lastErr.message}`);
    }
  }

  await floorCheck(exePath, group);

  const passed = results.filter((r) => r.ok).length;
  console.log(`RESULT: ${passed}/${results.length} passed${clean ? '' : ` — scratch dir: ${scratch}`}`);
  console.log(`Fixture task id: ${taskId}`);
  process.exitCode = passed === results.length ? 0 : 1;
}

main().catch((err) => {
  console.error('verify-ui.mjs crashed:', err);
  process.exitCode = 1;
});
