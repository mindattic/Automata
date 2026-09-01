// Drives the Automata WPF app's two WebView2 panes over the Chrome DevTools Protocol (CDP), so
// UI behavior (hover states, click flows, the rendered step tree) can be verified the same way a
// web app would be — instead of only smoke-testing that the app launches. See
// .claude/skills/verify-automata-ui/SKILL.md for the short version; this file is the driver.
//
// Usage: node tools/verify-ui.mjs [--clean]
//   --clean   remove the scratch directory on exit instead of leaving it for post-mortem.

import { chromium } from 'playwright';
import { spawn, execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import http from 'node:http';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..');
const clean = process.argv.includes('--clean');

const PANEL_PORT = 9333;
const TARGET_PORT = 9334;

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
async function clickRowOp(rowLocator, op) {
  await rowLocator.hover();
  await rowLocator.locator(`[data-op="${op}"]`).click();
}

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

// ---- main ---------------------------------------------------------------------------------

async function main() {
  console.log('Building Automata.App...');
  execFileSync('dotnet', ['build', path.join(repoRoot, 'Automata.App'), '-c', 'Debug'], {
    cwd: repoRoot, stdio: 'inherit',
  });

  const exePath = path.join(repoRoot, 'Automata.App', 'bin', 'Debug', 'net10.0-windows', 'Automata.App.exe');
  const scratch = path.join(tmpdir(), `automata-verify-${Date.now()}`);
  const panelProfile = path.join(scratch, 'panel-profile');
  const targetProfile = path.join(scratch, 'target-profile');
  const collectionsRoot = path.join(scratch, 'collections');
  for (const dir of [panelProfile, targetProfile, collectionsRoot]) mkdirSync(dir, { recursive: true });

  const fixtureHtmlPath = path.join(__dirname, 'verify-ui-fixture.html');
  const fixtureUrl = 'file:///' + fixtureHtmlPath.replace(/\\/g, '/');
  const { collectionId, taskId, failTaskId } = writeFixture(collectionsRoot, fixtureUrl);

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

    // The fixture task exists but starts collapsed/unselected — select it to expand its step
    // tree (mirrors a user clicking the task row).
    await waitFor(() => panelPage.locator(`.node.task[data-task="${taskId}"]`).count().then((n) => n > 0),
      { timeoutMs: 10000, label: 'the fixture task to appear in the tree' });
    await panelPage.locator(`.node.task[data-task="${taskId}"] .name`).click();
    await waitFor(() => panelPage.locator('.insert-zone[data-index="1"]').count().then((n) => n > 0),
      { timeoutMs: 10000, label: 'the fixture task\'s step tree to render' });

    const gap = panelPage.locator('.insert-zone[data-index="1"]');

    await group('row icons: collection 🗂️, task 📋, idle step ▫', async () => {
      const collIcon = await panelPage.locator(`.node.collection[data-collection="${collectionId}"] .icon`).textContent();
      assertTrue((collIcon ?? '').includes('🗂'), `expected the collection row icon to be 🗂️, got "${collIcon}"`);
      const taskIcon = await panelPage.locator(`.node.task[data-task="${taskId}"] .icon`).textContent();
      assertTrue((taskIcon ?? '').includes('📋'), `expected the task row icon to be 📋, got "${taskIcon}"`);
      const stepStatuses = await panelPage.locator('#tree .node.step .status').allTextContents();
      assertTrue(stepStatuses.length > 0 && stepStatuses.every((s) => s === '▫'),
        `expected idle steps to show ▫, got ${JSON.stringify(stepStatuses)}`);
    });

    await group('per-row Run buttons exist; toolbar Run button is gone', async () => {
      assertEqual(await panelPage.locator('#btn-run').count(), 0, '#btn-run should no longer exist in the DOM');
      assertEqual(await panelPage.locator(`.node.collection[data-collection="${collectionId}"] [data-op="run-collection"]`).count(), 1,
        'expected a run-collection button on the collection row');
      assertEqual(await panelPage.locator(`.node.task[data-task="${taskId}"] [data-op="run-task"]`).count(), 1,
        'expected a run-task button on the task row');
    });

    await group('hover gap: hr-line indicator appears, height unchanged', async () => {
      const before = await gap.boundingBox();
      await gap.hover();
      await sleep(100);
      const after = await gap.boundingBox();
      assertEqual(after.height, before.height, 'insert-zone height changed on hover');
      // The hover indicator is a plain hr-style line (::after), not a box/border — pseudo-element
      // styles aren't exposed via getComputedStyle on the element itself, so read them via a
      // page-side getComputedStyle(el, '::after') call instead.
      const lineBg = await gap.evaluate((el) => getComputedStyle(el, '::after').backgroundColor);
      assertTrue(lineBg !== 'rgba(0, 0, 0, 0)' && lineBg !== 'transparent',
        `expected the ::after line to have a visible background on hover, got "${lineBg}"`);
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
      await panelPage.evaluate((cid) => {
        document.querySelector(`.node.collection[data-collection="${cid}"] [data-op="run-collection"]`).click();
        window.chrome.webview.postMessage({ action: 'cancelRun' });
      }, collectionId);
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

  const passed = results.filter((r) => r.ok).length;
  console.log(`RESULT: ${passed}/${results.length} passed${clean ? '' : ` — scratch dir: ${scratch}`}`);
  console.log(`Fixture task id: ${taskId}`);
  process.exitCode = passed === results.length ? 0 : 1;
}

main().catch((err) => {
  console.error('verify-ui.mjs crashed:', err);
  process.exitCode = 1;
});
