// node tools/Test-LoginRetention.mjs <app-directory> <new-output-directory> [--exercise-crash] [--exercise-clear-all] [--exercise-last-tab]
// Use --only-clear-all, --only-last-tab or --only-cookie-controls for a focused regression run.
// Requires Node 22.13+ with node:sqlite and the Windows desktop WebView2 runtime.
import http from 'node:http';
import path from 'node:path';
import { promises as fs } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { setTimeout as delay } from 'node:timers/promises';
import { DatabaseSync } from 'node:sqlite';

const [appDirectory, target, ...options] = process.argv.slice(2);
if (options.some(option => !['--exercise-crash', '--exercise-clear-all', '--exercise-last-tab', '--only-clear-all', '--only-last-tab', '--only-cookie-controls'].includes(option))) throw new Error('Unknown login test option.');
if (options.some(option => option.startsWith('--only-')) && options.length !== 1) throw new Error('Run a focused regression by itself.');
if (!appDirectory || !target) throw new Error('Pass a built application directory and a new test output directory.');
const executable = path.resolve(appDirectory, 'StealthWorkspace.exe');
await fs.access(executable);
const output = path.resolve(target);
await fs.mkdir(output); // Never reuse previous data or allow using an installed vault.
const runId = randomUUID().replaceAll('-', '');
const logs = [];
function report(message) { logs.push(message); console.log(message); }
const tags = { '127.0.0.1': 'A', localhost: 'B' };
const testHttpCache = options.includes('--exercise-crash');
const testCache = testHttpCache || options.includes('--only-cookie-controls');
let assetGets = 0;
const observations = [];
let currentPhase;

function handler(req, res) {
  const host = req.headers.host?.split(':')[0];
  if (req.method !== 'GET' || !Object.hasOwn(tags, host) || !req.url?.startsWith('/' + runId + '/')) { res.writeHead(404).end(); return; }
  if (testCache && req.url === '/' + runId + '/asset' && tags[host] === 'A') {
    res.writeHead(200, { 'Content-Type': 'text/plain', 'Cache-Control': 'public, max-age=86400, immutable' });
    res.end('asset-' + runId + '-' + ++assetGets);
    return;
  }
  if (testCache && req.url === '/' + runId + '/asset-count' && tags[host] === 'A') {
    res.writeHead(200, { 'Content-Type': 'text/plain', 'Cache-Control': 'no-store' }).end(String(assetGets));
    return;
  }
  const receipt = req.url.match(/^\/[0-9a-f]{32}\/storage-check\/([a-z]+)\/(login|read|logout)\/(present|absent)\/(none|[0-9]+-[0-9]+-[0-9]+)$/);
  if (testCache && receipt && receipt[1] === currentPhase) {
    observations.push({ site: tags[host], phase: receipt[1], op: receipt[2], offline: receipt[3], counts: receipt[4], serverGets: assetGets });
    res.writeHead(204, { 'Cache-Control': 'no-store' }).end();
    return;
  }
  const match = req.url.match(/^\/([0-9a-f]{32})\/(login|read|logout|who)(?:(?:\/([a-z]+))?\/[0-9a-f]{32})?$/);
  if (match?.[1] !== runId) { res.writeHead(404).end(); return; }
  const site = tags[host], op = match[2], secret = runId + site;
  res.setHeader('Cache-Control', 'no-store');
  if (op === 'who') {
    const authenticated = (req.headers.cookie ?? '').split(';').some(c => c.trim() === 'fixture_session=' + secret);
    res.setHeader('Content-Type', 'application/json');
    res.end(JSON.stringify({ authenticated }));
    return;
  }
  if (op === 'login' || op === 'logout') {
    res.setHeader('Set-Cookie', 'fixture_session=' + (op === 'login' ? secret : '') +
      '; HttpOnly; SameSite=Lax; Path=/; Max-Age=' + (op === 'login' ? '3600' : '0'));
  }
  res.setHeader('Content-Type', 'text/html; charset=utf-8');
  res.end('<!doctype html><html><meta charset=utf-8><title>Membership fixture ' + site +
    '</title><main>Local membership fixture</main><script>(' + fixturePage.toString() + ')(' +
    [runId, site, op, match[3] ?? currentPhase, testCache, testHttpCache].map(v => JSON.stringify(v)).join(',') + ')</script></html>');
}

async function fixturePage(id, site, action, phase, checkCache, checkHttpCache) {
  let step = 'localStorage';
  try {
    const value = id + site;
    const write = action === 'login';
    if (write) localStorage.setItem('fixture_value', value);
    step = 'IndexedDB.databases';
    const databaseExists = (await indexedDB.databases()).some(db => db.name === 'fixture_db');
    let saved;
    if (write || databaseExists) {
      step = 'IndexedDB.open';
      const db = await new Promise((resolve, reject) => {
        const request = indexedDB.open('fixture_db', 1);
        request.onupgradeneeded = () => request.result.createObjectStore('preferences');
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
      });
      try {
        step = 'IndexedDB.transaction';
        saved = await new Promise((resolve, reject) => {
          const tx = db.transaction('preferences', write ? 'readwrite' : 'readonly');
          const store = tx.objectStore('preferences');
          if (write) store.put(value, 'value');
          const read = store.get('value');
          tx.oncomplete = () => resolve(read.result);
          tx.onabort = tx.onerror = () => reject(tx.error);
        });
      } finally { db.close(); }
    }
    if (checkCache) {
      step = 'CacheStorage';
      const name = 'offline-membership-' + id;
      const key = '/' + id + '/saved-offline-page';
      if (write) await (await caches.open(name)).put(key, new Response(value));
      const cached = await caches.match(key, { cacheName: name });
      const hasOffline = !!cached && await cached.text() === value;
      if (hasOffline !== (saved === value)) throw Error('CacheStorage and expected site data disagree.');
      let counts = 'none';
      // Seed runs this after its lock/unlock. Crash is the next fresh app
      // process, before the controller requests an abrupt exit.
      if (checkHttpCache && site === 'A' && action === 'read' && (phase === 'seed' || phase === 'crash')) {
        step = 'ordinary HTTP GET reuse';
        const count = async () => Number(await (await fetch('/' + id + '/asset-count', { cache: 'no-store' })).text());
        const url = '/' + id + '/asset';
        const before = await count();
        const first = await (await fetch(url)).text();
        const afterFirst = await count();
        const second = await (await fetch(url)).text();
        const afterSecond = await count();
        counts = [before, afterFirst, afterSecond].join('-');
        if (first !== second || !first.startsWith('asset-' + id + '-') || afterFirst !== before + 1 || afterSecond !== afterFirst)
          throw Error('Immutable asset GET counts ' + counts + '.');
      }
      const receipt = await fetch('/' + id + '/storage-check/' + phase + '/' + action + '/' + (hasOffline ? 'present' : 'absent') + '/' + counts, { cache: 'no-store' });
      if (!receipt.ok) throw Error('The local server did not acknowledge the cache observation.');
    }
    step = 'server session';
    const { authenticated } = await (await fetch('/' + id + '/who', { cache: 'no-store' })).json();
    document.documentElement.dataset.verdict = [id, authenticated ? 'yes' : 'no',
      localStorage.getItem('fixture_value') === value ? 'yes' : 'no', saved === value ? 'yes' : 'no',
      document.cookie.includes('fixture_session=') ? 'leaked' : 'private'].join('|');
  } catch (e) {
    const who = await (await fetch('/' + id + '/who', { cache: 'no-store' })).json();
    let local = 'unavailable';
    try { local = localStorage.getItem('fixture_value') === id + site; } catch { /* limited fixture only */ }
    document.documentElement.dataset.verdict = 'fixture error: ' + step + ' ' + e.name + ' ' + e.message +
      '; authenticated=' + who.authenticated + '; localStorage=' + local + '; secure=' + isSecureContext;
  }
}

async function listen(server, host, port) {
  server.listen({ host, port }); await once(server, 'listening');
}
const server = http.createServer(handler);
await listen(server, '127.0.0.1', 0);
const port = server.address().port;
const ipv6 = http.createServer(handler);
try { await listen(ipv6, '::1', port); } catch (e) { if (e.code !== 'EADDRNOTAVAIL') throw e; }
const roots = {};
for (const key of ['main', 'other']) {
  roots[key] = path.join(output, key);
  await fs.mkdir(roots[key]);
  await fs.writeFile(path.join(roots[key], 'login-fixture.id'), runId);
}
const temp = path.join(output, 'temp');
await fs.mkdir(temp);

async function* historyFiles(directory) {
  let items;
  try { items = await fs.readdir(directory, { withFileTypes: true }); }
  catch (e) { if (e.code === 'ENOENT') return; throw e; }
  for (const item of items) {
    const candidate = path.join(directory, item.name);
    if (item.isSymbolicLink()) throw new Error('Unexpected link inside synthetic profile.');
    if (item.isDirectory()) yield* historyFiles(candidate);
    else if (item.isFile() && item.name === 'History') yield candidate;
  }
}

function countVisits(file, readOnly) {
  const db = new DatabaseSync(file, { readOnly });
  try {
    const table = db.prepare("SELECT count(*) AS n FROM sqlite_master WHERE name='urls' AND type='table'").get();
    const count = suffix => table.n ? db.prepare('SELECT count(*) AS n FROM urls WHERE instr(url, ?) > 0').get('/' + runId + '/' + suffix).n : 0;
    return [count(''), count('read/crash/'), count('read/recover/')];
  } finally { db.close(); }
}

async function inspectHistory(evidence, expectation) {
  const profiles = new Set((await fs.readFile(evidence + '.profiles', 'utf8')).trim().split(/\r?\n/));
  let total = 0, crashed = 0, recovered = 0, databases = 0;
  for (const profile of profiles) {
    const relative = path.relative(output, profile);
    if (!relative || relative.startsWith('..') || path.isAbsolute(relative))
      throw new Error('The fixture profile falls outside the isolated test directory. Its history was not inspected.');
    for await (const file of historyFiles(profile)) {
      const resolved = path.relative(await fs.realpath(output), await fs.realpath(file));
      if (!resolved || resolved.startsWith('..') || path.isAbsolute(resolved)) throw new Error('Fixture history is redirected.');
      let counts;
      try {
        counts = countVisits(file, true);
      } catch (error) {
        // Chromium may hold an exclusive SQLite lock. Copy its synthetic DB and journal
        // to a disposable private test directory and let SQLite recover that copy.
        if (!/locked|busy/i.test(error.message)) throw error;
        const copy = await fs.mkdtemp(path.join(output, 'history-snapshot-'));
        try {
          const snapshot = path.join(copy, 'History');
          await fs.copyFile(file, snapshot);
          for (const suffix of ['-wal', '-journal']) {
            try { await fs.copyFile(file + suffix, snapshot + suffix); }
            catch (e) { if (e.code !== 'ENOENT') throw e; }
          }
          counts = countVisits(snapshot, false);
        } finally {
          for (const name of await fs.readdir(copy)) await fs.unlink(path.join(copy, name));
          await fs.rmdir(copy);
        }
      }
      total += counts[0]; crashed += counts[1]; recovered += counts[2]; databases++;
    }
  }
  if (expectation === 'crash-positive' && !crashed) throw new Error('The native database has not yet recorded the pre-crash visits.');
  if (expectation === 'live' && !total) throw new Error('The current synthetic visits have not yet been committed.');
  if (expectation === 'recovered' && (crashed || !recovered))
    throw new Error('Native history not yet verified after recovery: previous=' + crashed + ', current=' + recovered);
  if (expectation === 'clean' && total !== 0) throw new Error('WebView2 History still contains ' + total + ' synthetic visit(s).');
  return databases + ' synthetic History database(s), all run visits=' + total + ', pre-crash=' + crashed + ', recovery=' + recovered;
}

async function requireHistory(evidence, stage, expectation = 'clean', attempts = 45) {
  let failure;
  for (let i = 0; i < attempts; i++) {
    try {
      const description = await inspectHistory(evidence, expectation);
      report(stage + ': ' + description);
      return;
    } catch (e) { failure = e; await delay(200); }
  }
  throw failure;
}

async function run(phase) {
  currentPhase = phase;
  const evidence = path.join(output, phase + '.txt');
  const child = spawn(executable, ['--ui-smoke', evidence, '--data-root', roots[phase === 'other' ? 'other' : 'main'],
    '--login-retention-test', phase, '--login-test-id', runId, '--login-test-port', String(port),
    ...(options.includes('--exercise-clear-all') ? ['--login-check-all'] : [])],
  { cwd: path.dirname(executable), windowsHide: false, stdio: 'ignore', env: { ...process.env, TEMP: temp, TMP: temp } });
  let done = false, complete = false;
  const exited = once(child, 'exit').then(([code, signal]) => { done = true; return [code, signal]; });
  const deadline = Date.now() + 120000;
  try {
    while (!done && Date.now() < deadline) {
      if (phase === 'seed' && !complete) {
        let locked;
        try { locked = await fs.readFile(evidence + '.locked', 'utf8'); } catch (e) { if (e.code !== 'ENOENT') throw e; }
        if (locked === runId) {
          await requireHistory(evidence, 'locked before unlock');
          await fs.writeFile(evidence + '.checked', runId, { flag: 'wx' });
          complete = true;
        }
      }
      if (['crash', 'recover', 'lasttab'].includes(phase) && !complete) {
        let live;
        try { live = await fs.readFile(evidence + '.live', 'utf8'); } catch (e) { if (e.code !== 'ENOENT') throw e; }
        if (live === runId) {
          if (phase === 'crash') {
            await requireHistory(evidence, 'unlocked, before abrupt process exit', 'crash-positive', 120);
            complete = true;
            if (!child.kill()) throw new Error('Could not terminate the marked test child.');
          } else {
            if (phase === 'lasttab') await requireHistory(evidence, 'before closing the last website', 'live', 120);
            else await requireHistory(evidence, 'after recovery, before orderly shutdown', 'recovered', 120);
            await fs.writeFile(evidence + '.checked', runId, { flag: 'wx' });
            complete = true;
          }
        }
      }
      await delay(120);
    }
    if (!done) throw new Error('Fixture phase timed out: ' + phase);
    const [code, signal] = await exited;
    if (phase === 'crash') {
      if (!complete || (code === 0 && !signal)) throw new Error('The synthetic test child did not end abruptly.');
      const crashedResult = await fs.readFile(evidence, 'utf8');
      if (crashedResult.includes('closing: vault locked=True')) throw new Error('The test child closed normally.');
      await requireHistory(evidence, 'after abrupt exit, before recovery', 'crash-positive', 100);
      report('PASS observed abrupt exit (pid ' + child.pid + ')');
      return;
    }
    const result = await fs.readFile(evidence, 'utf8');
    if (phase === 'forget' && (!result.includes('confirmed removing site A cookies; A signed out with stored data intact and B stayed signed in') ||
        !result.includes('confirmed removing all cookies; both sites signed out with their stored data intact')))
      throw new Error('The two Cookie-only controls were not validated: ' + evidence);
    await requireHistory(evidence, phase + ' after process exit');
    if (code !== 0 || signal || !result.includes('LOGIN RETENTION: OK') ||
        !result.includes('closing: vault locked=True') || !result.includes('closing: browser session cleanup completed=True') ||
        (phase === 'seed' && !complete))
      throw new Error(phase + ' did not pass. Inspect ' + evidence);
    report('PASS ' + phase + ' (pid ' + child.pid + ')');
  } finally { if (!done) { child.kill(); await exited; } }
}

try {
  const phases = options.includes('--only-clear-all') ? ['seed', 'clearall', 'checkall'] :
    options.includes('--only-last-tab') ? ['seed', 'lasttab', 'logout'] :
    options.includes('--only-cookie-controls') ? ['seed', 'logout', 'forget'] :
    ['seed', ...(options.includes('--exercise-crash') ? ['crash', 'recover'] : []), ...(options.includes('--exercise-last-tab') ? ['lasttab'] : []),
    'logout', 'forget', 'decoy', 'other', 'verify', ...(options.includes('--exercise-clear-all') ? ['clearall', 'checkall'] : [])];
  for (const phase of phases) await run(phase);
  if (testHttpCache) {
    const seed = observations.find(o => o.phase === 'seed' && o.site === 'A' && o.counts === '0-1-1');
    const restart = observations.find(o => o.phase === 'crash' && o.site === 'A' && o.counts === '1-2-2');
    if (!seed || !restart || seed.serverGets !== 1 || restart.serverGets !== 2 || assetGets !== 2)
      throw Error('Expected positive HTTP cache reuse and a network GET after normal process exit.');
    const stored = (phase, site, state) => observations.some(o => o.phase === phase && o.site === site && o.op === 'read' && o.offline === state);
    for (const [phase, site, state] of [['crash','A','present'],['crash','B','present'],['forget','A','absent'],['forget','B','present']])
      if (!stored(phase, site, state)) throw Error('Missing CacheStorage proof for ' + phase + ' ' + site + ' ' + state);
    if (options.includes('--exercise-clear-all') && !['A','B'].every(site => stored('checkall',site,'absent')))
      throw Error('CacheStorage remained after clear-all and restart.');
    report('HTTP immutable resource, ordinary GETs: seed server counts 0→1→1; first app restart 1→2→2 (two total server requests).');
    report('CacheStorage offline entries survived lock and process restart; single-site removal preserved B.' +
      (options.includes('--exercise-clear-all') ? ' Both entries remained absent after all-site removal and another restart.' : ''));
  }
  if (options.includes('--only-cookie-controls')) report('PASS both Cookie-only scopes: cancellation, server sessions, localStorage, IndexedDB and CacheStorage were checked on two origins.');
  report(options.includes('--only-clear-all') ? 'PASS two-origin account and site data removal from a blank tab and after restart' :
    options.includes('--only-last-tab') ? 'PASS last website closed, native blank tab exited, and both site accounts survived restart' :
    options.includes('--only-cookie-controls') ? 'PASS focused Cookie-only controls and storage preservation' :
    'PASS complete cross-process login, logout, origin removal, isolation and history checks');
} catch (e) {
  report('FAIL ' + e.message); process.exitCode = 1;
} finally {
  server.close(); if (ipv6.listening) ipv6.close();
  if (testCache) await fs.writeFile(path.join(output, 'cache-observations.json'), JSON.stringify({ assetGets, observations }, null, 2));
  await fs.writeFile(path.join(output, 'summary.txt'), logs.join('\n') + '\n');
}
