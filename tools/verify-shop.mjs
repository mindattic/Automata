// Acceptance check for the whole input/output loop: harvest a list off a page, then walk it one
// row at a time collecting a value from each.
//
// The run is never compared against another run. Two runs of the same task can skip the same rows
// or read the same wrong element and agree perfectly while both being wrong — so the oracle is
// INDEPENDENT: the generated product pages are read straight off disk for their prices, and the
// run has to match that total. Running it twice then proves only what a second run should prove,
// which is that it does not double-count.
//
//   node tools/verify-shop.mjs [--keep]
//
// Everything happens in a scratch directory under the system temp folder — the developer's real
// Documents\Automata store is never touched, which is why every AUTOMATA_* root is set below.

import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync, readFileSync, readdirSync, existsSync, mkdirSync, statSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { dbBeside, tasksIn, datasetRows } from './automata-db.mjs';

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
  AUTOMATA_DEMOS_ROOT: join(scratch, 'demos'),
  AUTOMATA_SCHEDULE_PATH: join(scratch, 'schedule.json'),
  AUTOMATA_SETTINGS_PATH: join(scratch, 'settings.json'),
  AUTOMATA_BROWSER_PROFILE_ROOT: join(scratch, 'browsers'),
};
const env = { ...process.env, ...roots };
// Everything the app and runner store is in one database, beside the scratch settings path.
const dbPath = dbBeside(roots.AUTOMATA_SETTINGS_PATH);

function runner(...args) {
  const result = spawnSync(exe, args, { env, encoding: 'utf8', timeout: 10 * 60 * 1000 });
  return { code: result.status, out: `${result.stdout ?? ''}${result.stderr ?? ''}` };
}

// A WebView2 profile can still be held by a browser process that has not finished exiting,
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
    console.log(`\nNote: could not remove ${scratch} yet (a browser still holds it).`);
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
  sweepOldScratch();
  mkdirSync(scratch, { recursive: true });

  // ---- seed --------------------------------------------------------------------------------
  const seeded = runner('demos', 'seed');
  check('demos seed writes the pages and the examples', seeded.code === 0, seeded.out.trim());

  // ---- the oracle: what the generated pages actually say -----------------------------------
  // Read straight off disk, with no run involved. This is the number the run has to agree with,
  // and it is why a mistake inside the engine cannot pass this check by being consistent.
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

  // ---- find the demo task ------------------------------------------------------------------
  const tasksByKey = {};
  for (const task of tasksIn(dbPath, 'Demos')) {
    if (task.demo?.key) tasksByKey[task.demo.key] = task;
  }
  check(
    'the shop example was seeded',
    Boolean(tasksByKey['shop-prices']),
    Object.keys(tasksByKey).join(', '),
  );
  if (failures) throw new Error('cannot continue without the example');

  // ---- run it ------------------------------------------------------------------------------
  const first = runner('run', '--task', tasksByKey['shop-prices'].id);
  check('the run passes', first.code === 0, tail(first.out));

  // ---- the harvest itself ------------------------------------------------------------------
  const products = csv('shop-products.csv');
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

  // ---- the run against the oracle ----------------------------------------------------------
  const rows = csv('shop-prices.csv');
  check(
    'collected a price for every product, with no row twice',
    rows.length === itemPages.length && new Set(rows.map((r) => r.sku)).size === itemPages.length,
    `${rows.length} row(s), ${new Set(rows.map((r) => r.sku)).size} distinct sku(s)`,
  );
  check(
    `the collected prices total what the pages say (${money(expectedCents)})`,
    sumCents(rows) === expectedCents,
    `got ${money(sumCents(rows))}`,
  );

  // ---- and again, which is a different question ---------------------------------------------
  // The loop appends a row per product to one file. Without the write step claiming the first
  // write of each run, a second run would report twice the products and twice the money — and it
  // would look entirely plausible.
  const second = runner('run', '--task', tasksByKey['shop-prices'].id);
  check('a second run passes too', second.code === 0, tail(second.out));

  const after = csv('shop-prices.csv');
  check(
    'running it again replaces the results rather than doubling them',
    after.length === itemPages.length && sumCents(after) === expectedCents,
    `${after.length} row(s) totalling ${money(sumCents(after))}`,
  );
} catch (error) {
  check('the check ran to completion', false, error.message);
} finally {
  console.log(`\nRESULT: ${failures === 0 ? 'all checks passed' : `${failures} check(s) failed`}`);
  cleanup();
  process.exit(failures === 0 ? 0 : 1);
}

// ---- helpers ---------------------------------------------------------------------------------

/// A dataset's rows, as the CSV file used to give them: an object of string columns per row.
function csv(dataset) {
  return datasetRows(dbPath, dataset) ?? [];
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
