// Acceptance check for the whole input/output loop: harvest a list off a page, iterate it, and
// collect a value from each row — once one at a time, once several at once.
//
// The check is THREE-WAY on purpose. Comparing the two runs to each other is not enough: both
// could skip the same rows, or read the same wrong element, and agree perfectly while being wrong.
// So the generated product pages are read directly for their prices and that total is the oracle;
// each run must match it, and matching each other then follows.
//
//   node tools/verify-shop.mjs [--keep]
//
// Everything happens in a scratch directory under the system temp folder — the developer's real
// Documents\Automata store is never touched, which is why every AUTOMATA_* root is set below.

import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync, readFileSync, readdirSync, existsSync, mkdirSync, writeFileSync, statSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const repo = join(dirname(fileURLToPath(import.meta.url)), '..');
const exe = join(repo, 'Automata.Runner', 'bin', 'Debug', 'net10.0-windows', 'automata-runner.exe');
const keep = process.argv.includes('--keep');

let failures = 0;
function check(name, ok, detail) {
  console.log(`[${ok ? 'PASS' : 'FAIL'}] ${name}${ok || detail === undefined ? '' : ` — ${detail}`}`);
  if (!ok) failures++;
}

if (!existsSync(exe)) {
  console.error(`Runner not built: ${exe}\nRun: dotnet build -c Debug --nologo`);
  process.exit(2);
}

const scratch = mkdtempSync(join(tmpdir(), 'automata-shop-'));
const roots = {
  AUTOMATA_COLLECTIONS_ROOT: join(scratch, 'collections'),
  AUTOMATA_DATASETS_ROOT: join(scratch, 'datasets'),
  AUTOMATA_RUNS_ROOT: join(scratch, 'runs'),
  AUTOMATA_PARKED_ROOT: join(scratch, 'parked'),
  AUTOMATA_LIVE_ROOT: join(scratch, 'live'),
  AUTOMATA_DEMOS_ROOT: join(scratch, 'demos'),
  AUTOMATA_SCHEDULE_PATH: join(scratch, 'schedule.json'),
  AUTOMATA_SETTINGS_PATH: join(scratch, 'settings.json'),
  AUTOMATA_LANE_PROFILE_ROOT: join(scratch, 'lanes'),
};
const env = { ...process.env, ...roots };

function runner(...args) {
  const result = spawnSync(exe, args, { env, encoding: 'utf8', timeout: 10 * 60 * 1000 });
  return { code: result.status, out: `${result.stdout ?? ''}${result.stderr ?? ''}` };
}

// A lane's WebView2 profile can still be held by a browser process that has not finished exiting,
// so a locked scratch directory is a nuisance rather than a failure — it must not turn a passing
// check into a crash.
function cleanup() {
  if (keep) {
    console.log(`\nScratch kept: ${scratch}`);
    return;
  }
  try {
    rmSync(scratch, { recursive: true, force: true, maxRetries: 20, retryDelay: 500 });
  } catch {
    console.log(`\nNote: could not remove ${scratch} yet (a lane's browser still holds it).`);
    console.log('The next run of this check will sweep it up.');
  }
}

/// Removes scratch directories an earlier run could not, because a WebView2 process was still
/// letting go of its profile. Left alone these are hundreds of megabytes each.
function sweepOldScratch() {
  const anHourAgo = Date.now() - 60 * 60 * 1000;
  for (const name of readdirSync(tmpdir()).filter((n) => n.startsWith('automata-shop-'))) {
    const path = join(tmpdir(), name);
    if (path === scratch) continue;
    // Only what is plainly abandoned. Two of these checks running at once would otherwise delete
    // each other's workspace mid-run, and the victim fails with a missing generated page — which
    // reads exactly like the product being broken.
    try {
      if (statSync(path).mtimeMs > anHourAgo) continue;
      rmSync(path, { recursive: true, force: true });
    } catch { /* still held, or already gone; next time */ }
  }
}

try {
  // MaxConcurrency is tighten-only by design: the global value is the machine's ceiling and a task
  // may lower it but never raise it. So to see real lanes, the ceiling has to be granted here —
  // this check owns its scratch workspace, and granting it is exactly what a machine's owner does
  // in Settings. Without this the parallel example runs one row at a time and would agree with the
  // sequential one for entirely the wrong reason.
  sweepOldScratch();
  mkdirSync(scratch, { recursive: true });
  writeFileSync(
    roots.AUTOMATA_SETTINGS_PATH,
    JSON.stringify({ engineDefaults: { maxConcurrency: 4 } }, null, 2),
    'utf8',
  );

  // ---- seed --------------------------------------------------------------------------------
  const seeded = runner('demos', 'seed');
  check('demos seed writes the pages and the examples', seeded.code === 0, seeded.out.trim());

  // ---- the oracle: what the generated pages actually say -----------------------------------
  // Read straight off disk, with no run involved. This is the number both runs have to agree
  // with, and it is why a shared mistake in both runs cannot pass this check.
  const shopDir = join(roots.AUTOMATA_DEMOS_ROOT, 'shop');
  const itemPages = readdirSync(shopDir).filter((f) => f.startsWith('item-') && f.endsWith('.html'));
  let expectedCents = 0;
  for (const page of itemPages) {
    const html = readFileSync(join(shopDir, page), 'utf8');
    const match = html.match(/data-cents="(\d+)"/);
    if (match) expectedCents += Number(match[1]);
  }
  check(
    `the generated shop holds a price on every product page (${itemPages.length} pages)`,
    itemPages.length > 0 && expectedCents > 0,
    `pages=${itemPages.length} total=${money(expectedCents)}`,
  );

  // ---- find the two demo tasks -------------------------------------------------------------
  const tasksByKey = {};
  const demosDir = join(roots.AUTOMATA_COLLECTIONS_ROOT, 'Demos');
  for (const file of readdirSync(demosDir).filter((f) => f.endsWith('.json') && f !== 'collection.json')) {
    const task = JSON.parse(readFileSync(join(demosDir, file), 'utf8'));
    if (task.demo?.key) tasksByKey[task.demo.key] = task;
  }
  check(
    'both shop examples were seeded',
    Boolean(tasksByKey['shop-prices-sequential'] && tasksByKey['shop-prices-parallel']),
    Object.keys(tasksByKey).join(', '),
  );
  if (failures) throw new Error('cannot continue without the examples');

  // ---- run them ----------------------------------------------------------------------------
  const sequential = runner('run', '--task', tasksByKey['shop-prices-sequential'].id);
  check('one at a time: the run passes', sequential.code === 0, tail(sequential.out));

  const parallel = runner('run', '--task', tasksByKey['shop-prices-parallel'].id);
  check('several at once: the run passes', parallel.code === 0, tail(parallel.out));

  // A ForEach may only ASK for concurrency; the resolved ceiling grants it. If the ceiling won,
  // this run was secretly sequential and would agree with the other for the wrong reason — so the
  // engine's own throttle notice failing to appear is what proves the lanes really ran together.
  check(
    'several at once was not silently throttled back to one lane',
    !/resolves to 1 here/.test(parallel.out),
    'the engine reported that Max concurrency held it to a single lane',
  );

  // ---- the harvest itself ------------------------------------------------------------------
  const products = csv(join(roots.AUTOMATA_DATASETS_ROOT, 'shop-products.csv'));
  check(
    `the harvest wrote one row per product (${itemPages.length})`,
    products.length === itemPages.length,
    `${products.length} row(s)`,
  );
  check(
    'every harvested row carries a sku, a title and a url',
    products.length > 0 && products.every((r) => r.sku && r.title && r.url),
    'a column came back blank',
  );
  check(
    'the harvested urls are absolute, so a later Navigate can use them',
    products.every((r) => r.url.startsWith('file:///')),
    products[0]?.url,
  );

  // ---- the three-way comparison ------------------------------------------------------------
  const seqRows = csv(join(roots.AUTOMATA_DATASETS_ROOT, 'shop-prices-sequential.csv'));
  const parRows = csv(join(roots.AUTOMATA_DATASETS_ROOT, 'shop-prices-parallel.csv'));

  for (const [label, rows] of [['one at a time', seqRows], ['several at once', parRows]]) {
    check(
      `${label}: collected a price for every product, with no row twice`,
      rows.length === itemPages.length && new Set(rows.map((r) => r.sku)).size === itemPages.length,
      `${rows.length} row(s), ${new Set(rows.map((r) => r.sku)).size} distinct sku(s)`,
    );
  }

  const seqTotal = sumCents(seqRows);
  const parTotal = sumCents(parRows);

  check(
    `one at a time totals what the pages say (${money(expectedCents)})`,
    seqTotal === expectedCents,
    `got ${money(seqTotal)}`,
  );
  check(
    `several at once totals what the pages say (${money(expectedCents)})`,
    parTotal === expectedCents,
    `got ${money(parTotal)}`,
  );
  check(
    'raising the concurrency did not change the answer',
    seqTotal === parTotal,
    `${money(seqTotal)} vs ${money(parTotal)}`,
  );
} catch (error) {
  check('the check ran to completion', false, error.message);
} finally {
  console.log(`\nRESULT: ${failures === 0 ? 'all checks passed' : `${failures} check(s) failed`}`);
  cleanup();
  process.exit(failures === 0 ? 0 : 1);
}

// ---- helpers ---------------------------------------------------------------------------------

/// A reader for the datasets this project writes, which quote a field only when it needs it.
function csv(path) {
  if (!existsSync(path)) return [];
  const lines = readFileSync(path, 'utf8').split(/\r?\n/).filter((l) => l.length > 0);
  if (lines.length < 2) return [];
  const header = splitRow(lines[0]);
  return lines.slice(1).map((line) => {
    const cells = splitRow(line);
    return Object.fromEntries(header.map((name, i) => [name, cells[i] ?? '']));
  });
}

function splitRow(line) {
  const cells = [];
  let cell = '';
  let quoted = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (quoted) {
      if (ch === '"' && line[i + 1] === '"') { cell += '"'; i++; }
      else if (ch === '"') quoted = false;
      else cell += ch;
    } else if (ch === '"') quoted = true;
    else if (ch === ',') { cells.push(cell); cell = ''; }
    else cell += ch;
  }
  cells.push(cell);
  return cells;
}

/// Prices come back as the page displayed them ("$12.99"), so the currency and separators are
/// stripped and the arithmetic is done in whole cents — floats do not add money up reliably.
function sumCents(rows) {
  return rows.reduce((total, row) => {
    const digits = (row.price ?? '').replace(/[^0-9.]/g, '');
    if (!digits) return total;
    const [whole, fraction = ''] = digits.split('.');
    return total + Number(whole) * 100 + Number(fraction.padEnd(2, '0').slice(0, 2));
  }, 0);
}

function money(cents) {
  return `$${Math.floor(cents / 100)}.${String(cents % 100).padStart(2, '0')}`;
}

function tail(text) {
  const lines = text.trim().split(/\r?\n/);
  return lines.slice(-2).join(' | ');
}
