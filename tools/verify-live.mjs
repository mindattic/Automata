// The acceptance scenarios, against the real sites they name.
//
//   node tools/verify-live.mjs --live [--term wolf] [--keep]
//
// NOT part of the green bar, and it refuses to do anything without --live. Every other suite here
// runs against pages this repo generates, so a failure means Automata broke. This one talks to
// Google, to Bing and to a mailbox, so a failure can as easily mean a site was redesigned this
// morning, or that a consent wall appeared, or that the network is down. Mixing those two kinds of
// signal into one number would make the number worth less, not more — so this is a thing you run
// and READ, by hand, and the output is written to be read rather than to be counted.
//
// The mail scenario needs an account, and skips itself by name when there is not one:
//   AUTOMATA_MAIL_URL, AUTOMATA_MAIL_USER, AUTOMATA_MAIL_PASS
//
// Self-healing is left ON deliberately. Sites moving under a recording is the thing these exist to
// meet, and a repair being made and kept is a result worth reporting, not noise.

import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync, readFileSync, readdirSync, existsSync, mkdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const repo = join(dirname(fileURLToPath(import.meta.url)), '..');
const exe = join(repo, 'Automata.Runner', 'bin', 'Debug', 'net10.0-windows', 'automata-runner.exe');
const keep = process.argv.includes('--keep');
const live = process.argv.includes('--live');
const termArg = process.argv.indexOf('--term');
const term = termArg > 0 ? process.argv[termArg + 1] : 'wolf';

if (!live) {
  console.log(
    'verify-live talks to real websites, so it does nothing unless you say so:\n' +
    '  node tools/verify-live.mjs --live\n' +
    'It is deliberately not part of the suite the build runs — a Google redesign is not a\n' +
    'regression in this repo, and a check that cannot tell the two apart is worse than no check.',
  );
  process.exit(0);
}

if (!existsSync(exe)) {
  console.error(`Runner not built: ${exe}\nRun: dotnet build -c Debug --nologo`);
  process.exit(2);
}

let failures = 0;
let skipped = 0;
function check(name, ok, detail) {
  console.log(`[${ok ? 'PASS' : 'FAIL'}] ${name}${ok || detail === undefined ? '' : ` — ${detail}`}`);
  if (!ok) failures++;
}
function skip(name, why) {
  console.log(`[SKIP] ${name} — ${why}`);
  skipped++;
}

const scratch = mkdtempSync(join(tmpdir(), 'automata-live-'));
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

function runner(...args) {
  const result = spawnSync(exe, args, { env, encoding: 'utf8', timeout: 10 * 60 * 1000 });
  return { code: result.status, out: `${result.stdout ?? ''}${result.stderr ?? ''}` };
}

/// A few seconds between scenarios. Partly manners — these are somebody else's servers and this
/// suite can fire four searches in under a minute — and partly accuracy: a run that arrives while a
/// search engine is still deciding whether to rate-limit you comes back with an interstitial, which
/// looks exactly like the profile being wrong. This is not in the green bar precisely because that
/// kind of failure exists, but there is no reason to go looking for it.
function pause(seconds = 4) {
  spawnSync(process.execPath, ['-e', `setTimeout(() => {}, ${seconds * 1000})`], { timeout: 60000 });
}

/// A harvest writes a CSV; this reads back what it actually collected.
function rowsOf(dataset) {
  const file = join(roots.AUTOMATA_DATASETS_ROOT, dataset);
  if (!existsSync(file)) return null;
  const lines = readFileSync(file, 'utf8').trim().split(/\r?\n/);
  if (lines.length < 2) return [];
  return lines.slice(1).map((line) => line.replace(/^"|"$/g, '').replace(/""/g, '"'));
}

/// The profile as it currently sits on disk, by id — the names carry an em dash and the files are
/// named after them, so scanning beats constructing a path.
function profileOnDisk(id) {
  const dir = join(roots.AUTOMATA_COLLECTIONS_ROOT, 'Acceptance');
  if (!existsSync(dir)) return null;
  for (const file of readdirSync(dir).filter((f) => f.endsWith('.json') && f !== 'collection.json')) {
    const task = JSON.parse(readFileSync(join(dir, file), 'utf8'));
    if (task.id === id) return task;
  }
  return null;
}

/// What a self-heal actually WROTE, step by step.
///
/// Reporting that a heal happened is not enough, and finding that out is most of what pointing
/// these at live sites is for: the first run of the Google profile healed to `#ti6dpd`, a generated
/// id that will be a different string on the next page load. A repair can make the record worse,
/// and only the value it wrote says which happened.
function healedInto(before, after) {
  const changes = [];
  const targets = (task) => Object.fromEntries(
    (task?.steps ?? []).map((s) => [s.id, JSON.stringify(s.target ?? null)]));
  const was = targets(before);
  const now = targets(after);
  for (const [id, value] of Object.entries(now)) {
    if (was[id] !== undefined && was[id] !== value) {
      changes.push(`${id}: ${was[id]} → ${value}`);
    }
  }
  return changes;
}

function tailOf(text, lines = 4) {
  return text.trim().split(/\r?\n/).slice(-lines).join(' | ');
}

/// Says what kind of "it did not work" this was, because on a live site there are several and they
/// call for different things. Guessing precisely would be dishonest — this only reports HOW FAR it
/// got, which is genuinely known and is what tells you where to look.
function diagnose(out, rows) {
  if (/self-healed/.test(out)) return 'the page had moved and the run repaired itself — read the log';
  if (rows === null) {
    return 'it never reached the collecting step: the page did not look the way the profile ' +
      'expects. A consent interstitial, a region variant or a redesign all look like this — ' +
      'open the profile in the app to see the page it actually got';
  }
  if (rows.length === 0) {
    return 'the container was found but nothing inside it matched: the row selector needs ' +
      're-recording against the page as it is now';
  }
  return 'the run reported a failure after collecting';
}

function cleanup() {
  if (keep) {
    console.log(`\nScratch kept: ${scratch}`);
    return;
  }
  try { rmSync(scratch, { recursive: true, force: true }); }
  catch { console.log('\nNote: the scratch dir is still held by a browser; the next run sweeps it.'); }
}

try {
  mkdirSync(scratch, { recursive: true });

  const seeded = runner('profiles', 'seed');
  check('profiles seed installs the acceptance scenarios', seeded.code === 0, seeded.out.trim());
  console.log(seeded.out.trim());

  // ---- the two searches ------------------------------------------------------------------------
  for (const [id, name, dataset] of [
    ['profile-google', 'Google search — result titles', 'google-titles.csv'],
    ['profile-bing', 'Bing search — result titles', 'bing-titles.csv'],
  ]) {
    pause();
    const before = profileOnDisk(id);
    const result = runner('run', '--task', name, '--input', `term=${term}`);
    const rows = rowsOf(dataset);
    check(`${name}: the run passes`, result.code === 0, diagnose(result.out, rows));

    // Five is a floor, not a count: how many results a search returns is the site's business, and
    // asserting an exact number would make this fail every time somebody's homepage changed.
    check(
      `${name}: collected result titles for "${term}"`,
      rows !== null && rows.length >= 5,
      rows === null ? 'no dataset was written' : `${rows.length} row(s)`,
    );
    if (rows?.length) {
      console.log(`       ${rows.length} titles, first three:`);
      for (const row of rows.slice(0, 3)) console.log(`         · ${row.slice(0, 90)}`);
      // The term appearing in at least one title is the difference between "a page was scraped"
      // and "the search this task asked for was the one that ran".
      check(
        `${name}: the results are for the term that was asked for`,
        rows.some((r) => r.toLowerCase().includes(term.toLowerCase())),
        `none of ${rows.length} titles mention "${term}"`,
      );
    }
    const healed = healedInto(before, profileOnDisk(id));
    for (const change of healed) console.log(`       healed and saved — ${change}`);

    // A repair has to CONVERGE. Before the reject patterns were tightened this profile healed on
    // every single run, because what it wrote back was a generated id that would not be there next
    // time — so the file was rewritten forever and the strongest strategies never once matched.
    // Running it twice is the only way to tell a repair from a treadmill.
    if (healed.length) {
      const second = profileOnDisk(id);
      pause();
      const again = runner('run', '--task', name, '--input', `term=${term}`);
      check(
        `${name}: the repair holds — a second run has nothing left to heal`,
        again.code === 0 && healedInto(second, profileOnDisk(id)).length === 0,
        healedInto(second, profileOnDisk(id)).join('; ') || tailOf(again.out),
      );
    }
  }

  // ---- the one that needs an account -------------------------------------------------------------
  const mailName = 'Webmail — the first 20 subject lines';
  const missing = ['AUTOMATA_MAIL_URL', 'AUTOMATA_MAIL_USER', 'AUTOMATA_MAIL_PASS']
    .filter((v) => !process.env[v]);
  if (missing.length) {
    skip(mailName, `no account to sign in as — set ${missing.join(', ')}`);
  } else {
    const result = runner('run', '--task', mailName);
    const rows = rowsOf('inbox-subjects.csv');
    check(`${mailName}: the run passes`, result.code === 0, diagnose(result.out, rows));
    check(
      `${mailName}: collected subject lines, capped at 20`,
      rows !== null && rows.length > 0 && rows.length <= 20,
      rows === null ? 'no dataset was written' : `${rows.length} row(s)`,
    );
    // Subjects are the one thing here that is somebody's private mail, so the count is reported
    // and the content is not.
    if (rows?.length) console.log(`       ${rows.length} subject line(s) collected (not printed)`);
  }

  console.log(
    `\n${failures === 0 ? 'RESULT: all checks passed' : `RESULT: ${failures} check(s) failed`}` +
    `${skipped ? ` (${skipped} skipped)` : ''}`,
  );
} finally {
  cleanup();
}

process.exitCode = failures === 0 ? 0 : 1;
