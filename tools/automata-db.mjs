// Access to an Automata database for the acceptance harnesses (read-only, bar editTaskSteps).
//
// The app and the runner keep everything in one SQLite file (automata.db). A harness points the
// app at a scratch folder through AUTOMATA_SETTINGS_PATH, and the app puts its database beside
// that path — so `dbBeside(settingsPath)` is where to look. These helpers hand back tasks,
// collections and datasets in the same JSON shape the old per-task files had, so a check written
// against a file's text still reads the same.
//
// Uses node:sqlite (built into Node 22.13+), opened read-only and fresh on every call so each read
// sees what the app has committed so far.

import path from 'node:path';
import { existsSync } from 'node:fs';
import { DatabaseSync } from 'node:sqlite';

export function dbBeside(settingsPath) {
  return path.join(path.dirname(settingsPath), 'automata.db');
}

function query(dbPath, sql, ...params) {
  if (!existsSync(dbPath)) return [];
  const db = new DatabaseSync(dbPath, { readOnly: true });
  try {
    // Timestamps are 64-bit integers past Number's safe range; read every integer as a BigInt and
    // narrow the ones that fit.
    const statement = db.prepare(sql);
    statement.setReadBigInts(true);
    return statement.all(...params).map((row) => Object.fromEntries(Object.entries(row).map(([k, v]) =>
      [k, typeof v === 'bigint' && v <= BigInt(Number.MAX_SAFE_INTEGER) && v >= -BigInt(Number.MAX_SAFE_INTEGER) ? Number(v) : v])));
  } finally {
    db.close();
  }
}

// EF's DateTimeOffsetToBinaryConverter: ((clock ticks / 1000) << 11) | offset minutes (11-bit,
// signed). Both directions, so a harness can read a stored instant and seed one.
const EPOCH_TICKS = 621355968000000000n;

function isoFromBinary(value) {
  if (value === null || value === undefined) return undefined;
  const v = BigInt(value);
  const ticks = (v >> 11n) * 1000n;
  const offsetMinutes = Number(BigInt.asIntN(11, v & 0x7FFn));
  const ms = Number((ticks - EPOCH_TICKS) / 10000n) - offsetMinutes * 60000;
  return new Date(ms).toISOString();
}

export function binaryFromDate(date) {
  if (date === null || date === undefined) return null;
  const ticks = BigInt(new Date(date).getTime()) * 10000n + EPOCH_TICKS;
  return (ticks / 1000n) << 11n; // UTC: offset 0
}

// Enums are stored by name ("Task"); the JSON files wrote them camelCase ("task").
const enumJson = (name) => (name ? name.charAt(0).toLowerCase() + name.slice(1) : name);
const enumColumn = (json) => (json ? json.charAt(0).toUpperCase() + json.slice(1) : json);

function camel(name) {
  return name.charAt(0).toLowerCase() + name.slice(1);
}

/// Rebuilds a complex-type column group (Settings_*, Settings_Retry_*) into the nested object the
/// JSON files carried, or undefined when the row holds none.
function complex(row, prefix, discriminated) {
  if (discriminated && (row[`${prefix}_Discriminator`] === null || row[`${prefix}_Discriminator`] === undefined)) {
    return undefined;
  }
  const out = {};
  let any = false;
  for (const [key, value] of Object.entries(row)) {
    if (!key.startsWith(`${prefix}_`) || value === null) continue;
    const rest = key.slice(prefix.length + 1);
    if (rest === 'Discriminator' || rest.includes('_')) continue;
    out[camel(rest)] = typeof value === 'bigint' ? Number(value) : value;
    any = true;
  }
  const nested = new Set(Object.keys(row)
    .filter((k) => k.startsWith(`${prefix}_`) && k.slice(prefix.length + 1).includes('_'))
    .map((k) => k.slice(prefix.length + 1).split('_')[0]));
  for (const group of nested) {
    const inner = complex(row, `${prefix}_${group}`, false);
    if (inner) { out[camel(group)] = inner; any = true; }
  }
  return any || discriminated ? out : undefined;
}

// Engine-settings booleans are stored as 0/1; the JSON files wrote true/false.
const BOOLEAN_SETTINGS = new Set(['selfHeal', 'allowLlmRepair', 'continueOnStepError', 'continueOnTaskError', 'screenshotOnFailure']);
function fixBooleans(settings) {
  if (!settings) return settings;
  for (const key of Object.keys(settings)) if (BOOLEAN_SETTINGS.has(key)) settings[key] = Boolean(settings[key]);
  return settings;
}

function taskFromRow(row) {
  const task = {
    schemaVersion: row.SchemaVersion,
    id: row.Id,
    collectionId: row.CollectionId,
    name: row.Name,
    description: row.Description,
  };
  if (row.StartUrl !== null) task.startUrl = row.StartUrl;
  task.inputs = JSON.parse(row.Inputs || '[]');
  task.outputs = JSON.parse(row.Outputs || '[]');
  task.steps = JSON.parse(row.Steps || '[]');
  task.createdUtc = isoFromBinary(row.CreatedUtc);
  task.modifiedUtc = isoFromBinary(row.ModifiedUtc);
  const settings = fixBooleans(complex(row, 'Settings', true));
  if (settings) task.settings = settings;
  if (row.Demo_Key !== null) task.demo = { key: row.Demo_Key, factoryHash: row.Demo_FactoryHash };
  return task;
}

function collectionFromRow(row) {
  const collection = {
    schemaVersion: row.SchemaVersion,
    id: row.Id,
    name: row.Name,
    description: row.Description,
    createdUtc: isoFromBinary(row.CreatedUtc),
    modifiedUtc: isoFromBinary(row.ModifiedUtc),
    taskOrder: JSON.parse(row.TaskOrder || '[]'),
  };
  const settings = fixBooleans(complex(row, 'Settings', true));
  if (settings) collection.settings = settings;
  return collection;
}

/// Every visible collection, by name.
export function collections(dbPath) {
  return query(dbPath, 'SELECT * FROM Collections WHERE DeletedUtc IS NULL ORDER BY Name')
    .map(collectionFromRow);
}

export function collectionNamed(dbPath, name) {
  return collections(dbPath).find((c) => c.name === name) ?? null;
}

/// The visible tasks of the collection with this name, in its task order.
export function tasksIn(dbPath, collectionName) {
  const collection = collectionNamed(dbPath, collectionName);
  if (!collection) return [];
  const tasks = query(dbPath, 'SELECT * FROM Tasks WHERE DeletedUtc IS NULL AND CollectionId = ?', collection.id)
    .map(taskFromRow);
  const rank = (t) => { const i = collection.taskOrder.indexOf(t.id); return i < 0 ? Number.MAX_SAFE_INTEGER : i; };
  return tasks.sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name));
}

export function task(dbPath, collectionName, taskName) {
  return tasksIn(dbPath, collectionName).find((t) => t.name === taskName) ?? null;
}

/// A task as the text its old file would have held (indented JSON), or '' when it is not there —
/// so `taskText(...).includes('"selfHeal"')` reads exactly like the old file checks did.
export function taskText(dbPath, collectionName, taskName) {
  const found = task(dbPath, collectionName, taskName);
  return found ? JSON.stringify(found, null, 2) : '';
}

/// A dataset's rows: string columns for a CSV dataset, the stored objects for a JSON one. Null
/// when there is no such dataset.
export function datasetRows(dbPath, name) {
  const [record] = query(dbPath, 'SELECT Id, Name, Columns FROM Datasets WHERE Name = ? COLLATE NOCASE', name);
  if (!record) return null;
  const rows = query(dbPath, 'SELECT Json FROM DatasetRows WHERE DatasetId = ? ORDER BY Ordinal', record.Id)
    .map((r) => JSON.parse(r.Json));
  if (record.Name.toLowerCase().endsWith('.json')) return rows;
  const columns = JSON.parse(record.Columns || '[]');
  return rows.map((row) => Object.fromEntries(columns.map((c) => [c, row[c] ?? ''])));
}

/// Rewrites one task's step tree in place, the way a hand edit would — for the checks that need
/// an example to have been changed behind the app's back. The app re-reads on its next request.
export function editTaskSteps(dbPath, collectionName, taskName, mutate) {
  const found = task(dbPath, collectionName, taskName);
  if (!found) throw new Error(`no task '${taskName}' in '${collectionName}'`);
  mutate(found.steps);
  const db = new DatabaseSync(dbPath);
  try {
    db.exec('PRAGMA busy_timeout = 10000');
    db.prepare('UPDATE Tasks SET Steps = ? WHERE Id = ?').run(JSON.stringify(found.steps), found.id);
  } finally {
    db.close();
  }
}

/// The schedule, in order, shaped like the old schedule.json entries.
export function schedule(dbPath) {
  return query(dbPath, 'SELECT * FROM Schedule ORDER BY SortOrder').map((row) => {
    const entry = {
      id: row.Id,
      name: row.Name,
      enabled: Boolean(row.Enabled),
      target: enumJson(row.Target),
      targetId: row.TargetId,
      triggers: JSON.parse(row.Triggers || '[]'),
    };
    if (row.NextDueUtc !== null) entry.nextDueUtc = isoFromBinary(row.NextDueUtc);
    if (row.LastRunUtc !== null) entry.lastRunUtc = isoFromBinary(row.LastRunUtc);
    if (row.LastOutcome !== null) entry.lastOutcome = row.LastOutcome;
    return entry;
  });
}

/// Seeds a run record and its parked checkpoint — the two halves the Runs tab joins — taking the
/// same shapes the old manifest.json and Parked/<runId>.json files had.
export function seedParkedRun(dbPath, manifest, parked) {
  const db = new DatabaseSync(dbPath);
  try {
    db.exec('PRAGMA busy_timeout = 10000');
    db.prepare(`INSERT INTO Runs (RunId, SchemaVersion, Target, TargetId, TargetName, Trigger, StartedUtc, EndedUtc, Success, Summary)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`).run(
      manifest.runId, manifest.schemaVersion ?? 1, enumColumn(manifest.target), manifest.targetId,
      manifest.targetName, manifest.trigger ?? 'manual', binaryFromDate(manifest.startedUtc),
      binaryFromDate(manifest.endedUtc), manifest.success === undefined || manifest.success === null ? null : (manifest.success ? 1 : 0),
      manifest.summary ?? null);
    db.prepare(`INSERT INTO ParkedRuns (RunId, SchemaVersion, Target, TargetName, Trigger, TaskId, TaskName, CollectionId,
                RemainingTaskIds, TasksPassed, TotalTasks, ParkedAtUtc, ResumeCount, Checkpoint)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`).run(
      parked.runId, parked.schemaVersion ?? 1, enumColumn(parked.target ?? 'task'), parked.targetName ?? '',
      parked.trigger ?? 'manual', parked.taskId, parked.taskName ?? '', parked.collectionId ?? '',
      JSON.stringify(parked.remainingTaskIds ?? []), parked.tasksPassed ?? 0, parked.totalTasks ?? 1,
      binaryFromDate(parked.parkedAtUtc), parked.resumeCount ?? 0, JSON.stringify(parked.checkpoint));
  } finally {
    db.close();
  }
}

/// Run records, newest first, shaped like the old manifest.json files.
export function runs(dbPath) {
  return query(dbPath, 'SELECT * FROM Runs ORDER BY StartedUtc DESC').map((row) => ({
    runId: row.RunId,
    target: enumJson(row.Target),
    targetId: row.TargetId,
    targetName: row.TargetName,
    trigger: row.Trigger,
    startedUtc: isoFromBinary(row.StartedUtc),
    endedUtc: isoFromBinary(row.EndedUtc),
    success: row.Success === null ? null : Boolean(row.Success),
    summary: row.Summary,
  }));
}
