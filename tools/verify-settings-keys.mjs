// Narrow, one-off check for the multi-key (BYO rotation pool) Settings UI change: each provider
// row's key field is now a <textarea> (one key per line) instead of a single-line password
// input. Not part of the full verify-ui.mjs checklist — a standalone script so it doesn't need
// the fixture task/collection machinery that checklist sets up for unrelated checks.
//
// Usage: node tools/verify-settings-keys.mjs

import { chromium } from 'playwright';
import { spawn, execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import http from 'node:http';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..');
const PANEL_PORT = 9333;
const TARGET_PORT = 9334;

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

async function requirePortFree(port, label) {
  if (await servesCdp(port)) {
    throw new Error(`${label} (:${port}) is already serving CDP — close the app or kill the orphaned msedgewebview2.exe.`);
  }
}

async function waitForHttp200(port, label) {
  return waitFor(() => servesCdp(port), { timeoutMs: 20000, label });
}

async function firstPage(browser) {
  return waitFor(() => browser.contexts()[0]?.pages()?.[0] ?? null, { timeoutMs: 10000, label: 'a page to appear' });
}

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

async function main() {
  console.log('Building Automata.App...');
  execFileSync('dotnet', ['build', path.join(repoRoot, 'Automata.App'), '-c', 'Debug'], { cwd: repoRoot, stdio: 'inherit' });

  await requirePortFree(PANEL_PORT, 'panel CDP endpoint');
  await requirePortFree(TARGET_PORT, 'target CDP endpoint');

  const exePath = path.join(repoRoot, 'Automata.App', 'bin', 'Debug', 'net10.0-windows', 'Automata.App.exe');
  const scratch = path.join(tmpdir(), `automata-verify-settings-${Date.now()}`);
  const panelProfile = path.join(scratch, 'panel-profile');
  const targetProfile = path.join(scratch, 'target-profile');
  const collectionsRoot = path.join(scratch, 'collections');
  const datasetsRoot = path.join(scratch, 'datasets');
  for (const dir of [panelProfile, targetProfile, collectionsRoot, datasetsRoot]) mkdirSync(dir, { recursive: true });

  // A non-empty collections store, so the first-run tutorial (which fires only when there are
  // zero real collections) doesn't pop its own modal on top of the one this check is driving.
  const seedDir = path.join(collectionsRoot, 'Seed');
  mkdirSync(seedDir, { recursive: true });
  const now = new Date().toISOString();
  writeFileSync(path.join(seedDir, 'collection.json'), JSON.stringify({
    schemaVersion: 1, id: randomUUID().replace(/-/g, ''), name: 'Seed', description: '',
    createdUtc: now, modifiedUtc: now, taskOrder: [],
  }, null, 2));

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
      AUTOMATA_SCHEDULE_PATH: path.join(scratch, 'schedule', 'schedule.json'),
      AUTOMATA_PARKED_ROOT: path.join(scratch, 'parked'),
      AUTOMATA_DEMOS_ROOT: path.join(scratch, 'demos'),
      AUTOMATA_SETTINGS_PATH: path.join(scratch, 'settings.json'),
      // This scratch run's own scoped Vault directory — never the developer's real
      // %APPDATA%\MindAttic\LLM\providers.json.
      MINDATTIC_LLM_CREDENTIALS: path.join(scratch, 'vault-llm'),
    },
    stdio: 'ignore',
  });

  let panelBrowser;
  try {
    await waitForHttp200(PANEL_PORT, 'panel CDP endpoint');
    panelBrowser = await chromium.connectOverCDP(`http://127.0.0.1:${PANEL_PORT}`);
    const panelPage = await firstPage(panelBrowser);
    await panelPage.waitForLoadState('domcontentloaded');
    panelPage.on('pageerror', (err) => console.log(`[PANEL ERROR] ${err.message}`));
    panelPage.on('console', (m) => { if (m.type() === 'error') console.log(`[PANEL CONSOLE] ${m.text()}`); });

    await group('Settings opens and each key field is a <textarea>', async () => {
      await panelPage.locator('#btn-settings').click();
      await panelPage.locator('#key-claude').waitFor({ state: 'visible', timeout: 5000 });

      for (const p of ['claude', 'openai', 'gemini', 'kimi']) {
        const tag = await panelPage.locator('#key-' + p).evaluate((el) => el.tagName.toLowerCase());
        if (tag !== 'textarea') throw new Error(`#key-${p} is a <${tag}>, expected <textarea>`);
      }
    });

    await group('Claude field starts with the "not configured" hint', async () => {
      await waitFor(async () => {
        const placeholder = await panelPage.locator('#key-claude').getAttribute('placeholder');
        return !!placeholder;
      }, { timeoutMs: 5000, label: 'onSettings to populate the placeholder' });
      const placeholder = await panelPage.locator('#key-claude').getAttribute('placeholder');
      console.log(`  placeholder: ${JSON.stringify(placeholder)}`);
      if (!/not configured/i.test(placeholder ?? '') && !/shared default/i.test(placeholder ?? ''))
        throw new Error(`unexpected initial placeholder: ${placeholder}`);

      // A fresh, fully-unconfigured profile (exactly this scratch run) also triggers the
      // pre-existing, one-time "Bring your own API key" nudge (keysetup.js's maybeAskForKey),
      // fired from the same onSettings round-trip just awaited above — unrelated to this change,
      // but it stacks on top of Settings and intercepts later clicks if left open.
      const nudgeOpen = !(await panelPage.locator('#modal').evaluate((el) => el.classList.contains('hidden')));
      if (nudgeOpen) {
        // Click its own Cancel button rather than pressing Escape: Escape bubbles to
        // settings.js's document-level listener too and would close the Settings dialog
        // underneath along with the nudge on top of it.
        console.log('  dismissing the pre-existing "Bring your own API key" nudge modal');
        await panelPage.locator('#modal-cancel').click();
        await waitFor(() => panelPage.locator('#modal').evaluate((el) => el.classList.contains('hidden')),
          { timeoutMs: 5000, label: 'the nudge modal to close' });
      }
    });

    await group('Typing two keys and saving reports a 2-key pool hint', async () => {
      await panelPage.locator('#key-claude').fill('sk-test-1\nsk-test-2');
      await panelPage.locator('#set-key-save').click();
      await waitFor(async () => {
        const placeholder = await panelPage.locator('#key-claude').getAttribute('placeholder');
        return /2 keys set/i.test(placeholder ?? '');
      }, { timeoutMs: 5000, label: 'the 2-key pool hint to appear' });
      const placeholder = await panelPage.locator('#key-claude').getAttribute('placeholder');
      console.log(`  placeholder after save: ${JSON.stringify(placeholder)}`);
      // The textarea itself must have been cleared back to empty — raw keys never round-trip.
      const value = await panelPage.locator('#key-claude').inputValue();
      if (value !== '') throw new Error(`textarea should clear after save, still has: ${JSON.stringify(value)}`);
    });

    await group('Clearing the key reverts the hint', async () => {
      await panelPage.locator('[data-clear="claude"]').click();
      await waitFor(async () => {
        const placeholder = await panelPage.locator('#key-claude').getAttribute('placeholder');
        return /not configured|shared default/i.test(placeholder ?? '');
      }, { timeoutMs: 5000, label: 'the cleared-key hint to appear' });
      const placeholder = await panelPage.locator('#key-claude').getAttribute('placeholder');
      console.log(`  placeholder after clear: ${JSON.stringify(placeholder)}`);
    });

    await group('Textarea layout: no overflow, clear button stays visible', async () => {
      const box = await panelPage.locator('#key-claude').boundingBox();
      const rowBox = await panelPage.locator('.llm-row', { has: panelPage.locator('#key-claude') }).boundingBox();
      const clearBox = await panelPage.locator('[data-clear="claude"]').boundingBox();
      console.log(`  textarea box: ${JSON.stringify(box)}`);
      console.log(`  row box: ${JSON.stringify(rowBox)}`);
      console.log(`  clear button box: ${JSON.stringify(clearBox)}`);
      if (!box || !rowBox || !clearBox) throw new Error('one of the elements has no visible box');
      if (box.x + box.width > rowBox.x + rowBox.width + 1)
        throw new Error('textarea overflows its row');
      if (clearBox.x < box.x + box.width - 1)
        throw new Error('clear button overlaps the textarea');
    });
  } finally {
    try { panelBrowser && await panelBrowser.close(); } catch { /* best effort */ }
    try { proc.kill(); } catch { /* best effort */ }
  }

  const failed = results.filter((r) => !r.ok);
  console.log(`\n${results.length - failed.length}/${results.length} checks passed.`);
  if (failed.length) process.exit(1);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
