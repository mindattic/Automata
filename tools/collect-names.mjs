// Collects the ids and classes real sites actually use, and sorts them by what stability.js thinks
// of them. A tuning instrument, not a check — it never fails, it reports.
//
//   node tools/collect-names.mjs [--sites a,b,c] [--keep]
//
// The filter in stability.js decides which names are worth recording as an element's identity, and
// getting it wrong costs in both directions. Recording a generated name poisons the strongest
// strategies in the cascade and rewrites the task file on every run (phase 29 found exactly that on
// Google). Rejecting an AUTHORED name is the more expensive mistake and the quieter one: the
// element was perfectly identifiable and the resolver now has to fall back to something weaker.
//
// So this prints two lists per site, and the useful reading is the same both ways round:
//
//   KEPT     — names the filter would record. Scan for anything that plainly came out of a build.
//   REJECTED — names the filter would throw away. Scan for anything a person obviously wrote.
//
// Whatever the scan turns up goes into the corpus in verify-js.mjs, which is where a pattern is
// PROVEN. A rule added here and nowhere else is a rule the next person deletes by accident.
//
// The sites are ordinary public pages, visited once, read-only, for their markup. They are chosen to
// spread across the things that generate names: hand-written HTML, CSS modules, styled-components,
// Tailwind, and the two search engines the acceptance profiles already use.

import { chromium } from 'playwright';
import { spawn } from 'node:child_process';
import { mkdirSync, rmSync, readFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..');
const scriptsDir = path.join(repoRoot, 'Automata.Core', 'Automation', 'Scripts');
const keep = process.argv.includes('--keep');

// Its own ports: verify-ui holds 9333-9336 and verify-js 9337-9338.
const TARGET_PORT = 9341;
const PANEL_PORT = 9342;

const DEFAULT_SITES = [
  'https://www.google.com/search?q=wolf',
  'https://www.bing.com/search?q=wolf',
  'https://en.wikipedia.org/wiki/Wolf',
  'https://news.ycombinator.com/',
  'https://github.com/anthropics',
  'https://developer.mozilla.org/en-US/docs/Web/API/Element',
  'https://react.dev/learn',
  'https://stackoverflow.com/questions',
];

const sitesArg = process.argv.indexOf('--sites');
const SITES = sitesArg >= 0 ? process.argv[sitesArg + 1].split(',') : DEFAULT_SITES;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function waitForHttp200(port, label, timeoutMs = 60000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try { const res = await fetch(`http://127.0.0.1:${port}/json/version`); if (res.ok) return; } catch { /* not up */ }
    await sleep(250);
  }
  throw new Error(`${label} never answered on :${port}`);
}

const scratch = path.join(tmpdir(), `automata-names-${randomUUID().slice(0, 8)}`);
mkdirSync(scratch, { recursive: true });

const exePath = path.join(repoRoot, 'Automata.App', 'bin', 'Debug', 'net10.0-windows', 'Automata.App.exe');
const proc = spawn(exePath, [], {
  cwd: path.dirname(exePath),
  env: {
    ...process.env,
    AUTOMATA_PANEL_CDP_PORT: String(PANEL_PORT),
    AUTOMATA_TARGET_CDP_PORT: String(TARGET_PORT),
    AUTOMATA_PANEL_PROFILE_DIR: path.join(scratch, 'panel-profile'),
    AUTOMATA_TARGET_PROFILE_DIR: path.join(scratch, 'target-profile'),
    AUTOMATA_COLLECTIONS_ROOT: path.join(scratch, 'collections'),
    AUTOMATA_DATASETS_ROOT: path.join(scratch, 'datasets'),
    AUTOMATA_RUNS_ROOT: path.join(scratch, 'runs'),
    AUTOMATA_SCHEDULE_PATH: path.join(scratch, 'schedule.json'),
    AUTOMATA_PARKED_ROOT: path.join(scratch, 'parked'),
    AUTOMATA_DEMOS_ROOT: path.join(scratch, 'demos'),
    AUTOMATA_SETTINGS_PATH: path.join(scratch, 'settings.json'),
  },
  stdio: 'ignore',
});

let browser;
try {
  await waitForHttp200(TARGET_PORT, 'target CDP endpoint');
  await waitForHttp200(PANEL_PORT, 'panel CDP endpoint');
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${TARGET_PORT}`);
  const page = browser.contexts()[0].pages()[0];
  const stability = readFileSync(path.join(scriptsDir, 'stability.js'), 'utf8');

  for (const site of SITES) {
    let names;
    try {
      await page.goto(site, { waitUntil: 'domcontentloaded', timeout: 30000 });
      await sleep(2000);
      // The filter is evaluated IN the page, against the same code the engine injects, so this
      // reports what the engine would decide rather than what a re-implementation would.
      names = await page.evaluate((src) => {
        // eslint-disable-next-line no-new-func
        new Function('window', src)(window);
        const s = window.__automataStability;
        const ids = new Set();
        const classes = new Set();
        document.querySelectorAll('*').forEach((el) => {
          const id = el.getAttribute && el.getAttribute('id');
          if (id) ids.add(id);
          for (let i = 0; i < el.classList.length; i++) classes.add(el.classList[i]);
        });
        const sort = (set) => {
          const kept = [];
          const rejected = [];
          [...set].forEach((n) => (s.looksGenerated(n) ? rejected : kept).push(n));
          return { kept: kept.sort(), rejected: rejected.sort() };
        };
        return { ids: sort(ids), classes: sort(classes) };
      }, stability);
    } catch (err) {
      console.log(`\n=== ${site}\n  (skipped: ${err.message.split('\n')[0]})`);
      continue;
    }

    // Trimmed, because a page can carry thousands of classes and the interesting ones are not
    // buried by volume — they stand out by shape, and a wall of Tailwind utilities hides them.
    const show = (list, cap) => (list.length > cap ? `${list.slice(0, cap).join(' ')} … +${list.length - cap} more` : list.join(' '));
    console.log(`\n=== ${site}`);
    console.log(`  ids KEPT      (${names.ids.kept.length}): ${show(names.ids.kept, 40)}`);
    console.log(`  ids REJECTED  (${names.ids.rejected.length}): ${show(names.ids.rejected, 40)}`);
    console.log(`  cls KEPT      (${names.classes.kept.length}): ${show(names.classes.kept, 60)}`);
    console.log(`  cls REJECTED  (${names.classes.rejected.length}): ${show(names.classes.rejected, 40)}`);
  }
} finally {
  try { await browser?.close(); } catch { /* already gone */ }
  proc.kill();
  await sleep(1500);
  if (!keep) {
    for (let attempt = 0; attempt < 5; attempt++) {
      try { rmSync(scratch, { recursive: true, force: true }); break; } catch { await sleep(1000); }
    }
  } else {
    console.log(`\nScratch kept: ${scratch}`);
  }
}
