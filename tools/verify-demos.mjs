// Acceptance check for the generated examples: every one of them is run, for real, in a browser.
//
// DemoCoverageTests already fails the build when a StepAction, WaitMode or ConditionOp has no
// example. That is a check on the SHAPE of the batch and it can only ever prove that an example
// was written — not that it works. This runs them.
//
// The shop pair is deliberately NOT here: tools/verify-shop.mjs runs those two and checks their
// answers against the prices read off the generated pages, which is a stronger claim than "the run
// exited zero". Running them twice would only cost time.
//
//   node tools/verify-demos.mjs [--keep]
//
// Everything happens in a scratch directory under the system temp folder — the developer's real
// Documents\Automata store is never touched, which is why every AUTOMATA_* root is set below.

import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync, readFileSync, readdirSync, existsSync, mkdirSync, statSync } from 'node:fs';
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

const scratch = mkdtempSync(join(tmpdir(), 'automata-demos-'));
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

/// Examples this check runs to completion, in the order a person would meet them.
const RUNNABLE = ['buttons', 'form', 'slow', 'order', 'chain'];

/// Covered more strictly elsewhere, or not finishable on purpose.
const ELSEWHERE = {
  'shop-prices-sequential': 'verify-shop.mjs, against the prices on the pages',
  'shop-prices-parallel': 'verify-shop.mjs, against the prices on the pages',
};
const PARKS = 'park';

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
  for (const name of readdirSync(tmpdir()).filter((n) => n.startsWith('automata-demos-'))) {
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

  // ---- seed ----------------------------------------------------------------------------------
  const seeded = runner('demos', 'seed');
  check('demos seed writes the pages and the examples', seeded.code === 0, seeded.out.trim());

  const tasksByKey = {};
  const demosDir = join(roots.AUTOMATA_COLLECTIONS_ROOT, 'Demos');
  for (const file of readdirSync(demosDir).filter((f) => f.endsWith('.json') && f !== 'collection.json')) {
    const task = JSON.parse(readFileSync(join(demosDir, file), 'utf8'));
    if (task.demo?.key) tasksByKey[task.demo.key] = task;
  }

  // Nothing may be quietly left out: every key the generator produced is either run below, run by
  // another check, or the one that parks. A new example added with no entry here fails this.
  const accounted = new Set([...RUNNABLE, ...Object.keys(ELSEWHERE), PARKS]);
  const unaccounted = Object.keys(tasksByKey).filter((k) => !accounted.has(k));
  check(
    'every seeded example is accounted for by this check',
    unaccounted.length === 0,
    `nothing runs ${JSON.stringify(unaccounted)} — add it to RUNNABLE or say where it is covered`,
  );
  const missing = [...accounted].filter((k) => !tasksByKey[k]);
  check('every example this check expects was seeded', missing.length === 0, JSON.stringify(missing));
  if (failures) throw new Error('cannot continue without the examples');

  // ---- run them ------------------------------------------------------------------------------
  for (const key of RUNNABLE) {
    const result = runner('run', '--task', tasksByKey[key].id);
    check(`${tasksByKey[key].name}: the run passes`, result.code === 0, tail(result.out));
  }

  for (const [key, where] of Object.entries(ELSEWHERE)) {
    console.log(`[SKIP] ${tasksByKey[key].name} — covered by ${where}`);
  }

  // ---- the one that is supposed not to finish -------------------------------------------------
  // Parking is the whole point of this example, so "it ran and stopped" is the pass, and a run
  // that ploughed on regardless would be the failure.
  const parked = runner('run', '--task', tasksByKey[PARKS].id);
  check(
    `${tasksByKey[PARKS].name}: parks instead of holding a browser`,
    parked.code === 0 && /^Parked /m.test(parked.out),
    tail(parked.out),
  );
  check(
    'the parked run is waiting to be picked up',
    /Parked, waiting to resume/.test(runner('status').out),
    'status does not list it',
  );

  // ---- what the conditions example wrote -------------------------------------------------------
  // Nine rows means all nine branches were taken. A condition that quietly did not hold would
  // otherwise be indistinguishable from a task that passed.
  const checks = csv(join(roots.AUTOMATA_DATASETS_ROOT, 'order-checks.csv'));
  check(
    'every one of the nine order checks held',
    checks.length === 9 && new Set(checks.map((r) => r.check)).size === 9,
    `${checks.length} row(s): ${checks.map((r) => r.check).join(', ')}`,
  );

  // A collecting task has to be repeatable, or every example quietly needs a fresh workspace and
  // the first thing a new user does twice looks broken. This is what resetOnFirstWrite buys.
  const again = runner('run', '--task', tasksByKey.order.id);
  const twice = csv(join(roots.AUTOMATA_DATASETS_ROOT, 'order-checks.csv'));
  check(
    'running it a second time replaces its rows rather than doubling them',
    again.code === 0 && twice.length === 9,
    `${twice.length} row(s) after two runs`,
  );

  // The oracle for what those rows should say is the generated page, not the run: each recorded
  // value has to be a fact that is actually printed on order.html.
  const orderHtml = readFileSync(join(roots.AUTOMATA_DEMOS_ROOT, 'order.html'), 'utf8');
  const onThePage = new Set(
    [...orderHtml.matchAll(/<dd id="[^"]+">([^<]*)<\/dd>/g)].map((m) => m[1].trim()),
  );
  onThePage.add(''); // the note field, left blank on purpose
  const strays = checks.map((r) => r.value).filter((v) => !onThePage.has(v));
  check(
    'every recorded value is one the page actually shows',
    strays.length === 0,
    `not on the page: ${JSON.stringify(strays)}`,
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

/// The last few lines of a run's output — enough to see which step failed without pasting a log.
function tail(text, lines = 6) {
  return text.trim().split(/\r?\n/).slice(-lines).join(' | ');
}
