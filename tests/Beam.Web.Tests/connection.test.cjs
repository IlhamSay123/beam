const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const { chromium, webkit } = require('playwright');

let server, browser, origin;
const root = path.resolve(__dirname, '../../src/Beam.Server/wwwroot');
const engine = process.env.BEAM_ENGINE || 'chromium';
const file = { id: 'file', name: 'retained.txt', isText: false, sizeBytes: 9, contentType: 'text/plain' };
const vaultFile = { ...file, id: 'vault-file', name: 'vault.txt', direction: 'inbound', read: false, available: true, timestampUtc: '2026-10-16T12:00:00Z' };
before(async () => {
  server = http.createServer(async (req, res) => {
    const filename = new URL(req.url, 'http://localhost').pathname.split('/').pop() || 'index.html';
    if (!['index.html', 'app.js', 'vault.js', 'motion.js', 'lucide.min.js', 'styles.css'].includes(filename)) return res.writeHead(404).end();
    const body = await fs.readFile(path.join(root, filename));
    res.writeHead(200, { 'Content-Type': filename.endsWith('.js') ? 'text/javascript' : filename.endsWith('.css') ? 'text/css' : 'text/html' }).end(body);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
  browser = engine === 'webkit' ? await webkit.launch() : await chromium.launch({ channel: process.env.BEAM_BROWSER || 'msedge' });
});
after(async () => { await browser?.close(); if (server) await new Promise(resolve => server.close(resolve)); });

async function open(options = {}) {
  const page = options.context ? await options.context.newPage() : await browser.newPage({ viewport: { width: 390, height: 844 } });
  page.setDefaultTimeout(3000);
  const beats = [];
  await page.route('**/outbox', options.outbox || (route => route.fulfill({ json: [file] })));
  await page.route('**/capabilities', options.capabilities || (route => route.fulfill({ json: { maxUploadBytes: 104857600, maxTextBytes: 65536, safeRetryVersion: 1 } })));
  await page.route('**/connection/heartbeat', route => {
    beats.push(route.request());
    return options.heartbeat ? options.heartbeat(route) : route.fulfill({ status: 204 });
  });
  await page.route('**/vault?*', route => route.fulfill({ json: { items: [vaultFile], total: 1, hasMore: false } }));
  await page.route('**/*/content', route => route.fulfill({ body: 'file body', contentType: 'text/plain' }));
  await page.addInitScript(() => {
    // Safari on a plain LAN HTTP origin has getRandomValues but no randomUUID.
    Object.defineProperty(crypto, 'randomUUID', { value: undefined });
    let hidden = false;
    Object.defineProperty(document, 'hidden', { get: () => hidden });
    window.setPageHidden = value => { hidden = value; document.dispatchEvent(new Event('visibilitychange')); };
  });
  if (options.init) await page.addInitScript(options.init);
  await page.clock.install();
  await page.goto(`${origin}/app/${options.unpaired ? '' : '?token=old-token'}`);
  return { page, beats };
}
async function connected(page) { await page.waitForFunction(() => document.getElementById('connectionStatus').textContent === 'Connected'); }

test('a stored token stays connecting until a valid outbox response, then announces activity with a plain-HTTP session ID', async () => {
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  const { page, beats } = await open({ outbox: async route => { await pending; await route.fulfill({ json: [file] }); } });
  try {
    assert.equal(await page.locator('#connectionStatus').innerText(), 'Connecting');
    assert.equal(beats.length, 0);
    const heartbeat = page.waitForRequest('**/connection/heartbeat');
    release();
    await connected(page);
    await heartbeat;
    assert.equal(beats.length, 1);
    assert.equal(beats[0].method(), 'POST');
    assert.equal(beats[0].headers()['x-beam-token'], 'old-token');
    assert.match(beats[0].headers()['content-type'], /application\/json/);
    assert.match(beats[0].postDataJSON().sessionId, /^[0-9a-f]{32}$/i);
    assert.equal(await page.evaluate(() => Object.keys(localStorage).some(key => /session/i.test(key))), false);
  } finally { release(); await page.close(); }
});

test('invalid outbox JSON never claims a connected pairing or starts activity', async () => {
  const { page, beats } = await open({ outbox: route => route.fulfill({ json: { items: [] } }) });
  try {
    await page.waitForFunction(() => document.getElementById('connectionStatus').textContent !== 'Connecting');
    assert.equal(await page.locator('#connectionStatus').innerText(), 'PC unavailable');
    assert.equal(beats.length, 0);
    assert.equal(await page.locator('#connectionNotice').isVisible(), true);
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), 'old-token');
  } finally { await page.close(); }
});

test('visible activity repeats at five seconds using the same in-memory ID and pauses while hidden', async () => {
  const { page, beats } = await open();
  try {
    await connected(page);
    await page.waitForFunction(() => heartbeatStarted && heartbeatAttempt === null);
    await page.clock.pauseAt(await page.evaluate(() => Date.now()));
    assert.equal(beats.length, 1);
    const id = beats[0].postDataJSON().sessionId;
    const nextHeartbeat = page.waitForResponse('**/connection/heartbeat');
    await page.clock.runFor(5000);
    await nextHeartbeat;
    assert.equal(beats.length, 2);
    await page.evaluate(() => window.setPageHidden(true));
    await page.clock.runFor(15000);
    assert.equal(beats.length, 2);
    await page.evaluate(() => window.setPageHidden(false));
    await page.waitForTimeout(50);
    assert.equal(beats.length, 3);
    assert.ok(beats.every(request => request.postDataJSON().sessionId === id));
    await page.reload();
    await connected(page);
    await page.waitForTimeout(50);
    assert.notEqual(beats.at(-1).postDataJSON().sessionId, id);
  } finally { await page.close(); }
});

test('a pending heartbeat times out after five seconds without overlapping requests', async () => {
  const { page } = await open({ init: () => {
    const original = window.fetch;
    window.heartbeatCount = 0; window.heartbeatActive = 0; window.heartbeatMax = 0;
    window.fetch = (url, options) => {
      if (url !== '/connection/heartbeat') return original(url, options);
      window.heartbeatCount++; window.heartbeatActive++;
      window.heartbeatMax = Math.max(window.heartbeatMax, window.heartbeatActive);
      return new Promise((resolve, reject) => options.signal.addEventListener('abort', () => { window.heartbeatActive--; reject(new DOMException('Aborted', 'AbortError')); }, { once: true }));
    };
  } });
  try {
    await connected(page);
    await page.waitForFunction(() => window.heartbeatCount === 1);
    await page.route('**/outbox', route => route.abort());
    await page.clock.pauseAt(await page.evaluate(() => Date.now()));
    await page.clock.runFor(4999);
    assert.equal(await page.evaluate(() => window.heartbeatCount), 1);
    await page.clock.runFor(50);
    await page.waitForFunction(() => document.getElementById('connectionStatus').textContent === 'PC unavailable');
    assert.equal(await page.evaluate(() => window.heartbeatActive), 0);
    assert.equal(await page.evaluate(() => window.heartbeatMax), 1);
    assert.equal(await page.locator('#connectionNotice').isVisible(), true);
  } finally { await page.close(); }
});

test('hiding and superseding activity aborts attempts without reporting a network failure', async () => {
  const { page } = await open({ init: () => {
    const original = window.fetch;
    window.heartbeatCount = 0; window.heartbeatAborts = 0;
    window.fetch = (url, options) => {
      if (url !== '/connection/heartbeat') return original(url, options);
      window.heartbeatCount++;
      return new Promise((resolve, reject) => options.signal.addEventListener('abort', () => { window.heartbeatAborts++; reject(new DOMException('Aborted', 'AbortError')); }, { once: true }));
    };
  } });
  try {
    await connected(page);
    await page.waitForFunction(() => window.heartbeatCount === 1);
    await page.evaluate(() => window.setPageHidden(true));
    assert.equal(await page.evaluate(() => window.heartbeatAborts), 1);
    await page.evaluate(() => window.setPageHidden(false));
    await page.waitForFunction(() => window.heartbeatCount === 2);
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.waitForFunction(() => window.heartbeatCount === 3);
    assert.equal(await page.evaluate(() => window.heartbeatAborts), 2);
    assert.notEqual(await page.locator('#connectionStatus').innerText(), 'PC unavailable');
  } finally { await page.close(); }
});

test('network and server failures are global across tabs; accessible retry verifies recovery', async () => {
  let fail = false;
  const { page } = await open({ heartbeat: route => fail ? route.abort() : route.fulfill({ status: 204 }) });
  try {
    await connected(page);
    fail = true;
    await page.route('**/outbox', route => route.abort());
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.waitForFunction(() => document.getElementById('connectionStatus').textContent === 'PC unavailable');
    for (const tab of ['Send', 'Receive', 'Vault']) {
      await page.getByRole('tab', { name: tab, exact: true }).click();
      assert.equal(await page.locator('#connectionNotice').isVisible(), true);
      assert.equal(await page.getByRole('button', { name: 'Retry connection' }).isVisible(), true);
    }
    assert.equal(await page.locator('#tokenError').isVisible(), false);
    assert.match(await page.locator('#status').innerText(), /connect/i);
    assert.notEqual(await page.locator('#status').innerText(), await page.locator('#connectionMessage').innerText(), 'Receive must not repeat the global error paragraph');
    fail = false;
    await page.route('**/outbox', route => route.fulfill({ json: [file] }));
    await page.getByRole('button', { name: 'Retry connection' }).click();
    await connected(page);
    assert.equal(await page.locator('#connectionNotice').isVisible(), false);
    await page.route('**/outbox', route => route.fulfill({ status: 503 }));
    await page.evaluate(() => loadOutbox());
    assert.match(await page.locator('#connectionMessage').innerText(), /503/);
    assert.equal(await page.locator('#tokenError').isVisible(), false);
  } finally { await page.close(); }
});

test('authentication failure returns to pairing without diagnosing a typo or being overwritten by pending validation', async () => {
  let reject = false;
  const { page } = await open({ heartbeat: route => route.fulfill({ status: reject ? 401 : 204 }) });
  try {
    await connected(page); reject = true;
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.locator('#tokenError').waitFor();
    assert.equal(await page.locator('#connectionStatus').innerText(), 'Pairing needed');
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), null);
    assert.doesNotMatch(await page.locator('#tokenError').innerText(), /typo|wrong token/i);
    assert.equal(await page.locator('#connectionNotice').isVisible(), false);
  } finally { await page.close(); }
});

test('heartbeat loss and recovery cannot replace a confirmed copy result', async () => {
  const note = { id: 'note', name: 'Text', isText: true, textContent: 'Keep this result' };
  const { page } = await open({
    outbox: route => route.fulfill({ json: [note] }),
    init: () => { Object.defineProperty(navigator, 'clipboard', { value: undefined }); document.execCommand = () => true; }
  });
  await page.route('**/outbox/*/ack', route => route.fulfill({ status: 200 }));
  try {
    await connected(page);
    await page.waitForFunction(() => heartbeatAttempt === null && !loading);
    await page.route('**/connection/heartbeat', route => route.fulfill({ status: 503 }));
    await page.evaluate(() => sendHeartbeat());
    assert.equal(await page.locator('#connectionNotice').isVisible(), true);
    await page.getByRole('button', { name: 'Copy: Keep this result' }).click();
    await page.getByText('Copied to clipboard.', { exact: true }).waitFor();
    await page.waitForFunction(() => !loading);
    assert.equal(await page.locator('#status').innerText(), 'Copied to clipboard.');
    await page.route('**/outbox', route => route.abort());
    await page.evaluate(() => loadOutbox());
    assert.equal(await page.locator('#connectionNotice').isVisible(), true);
    assert.equal(await page.locator('#status').innerText(), 'Copied to clipboard.', 'A later connection failure must preserve the transfer result');
  } finally { await page.close(); }
});

test('an old token cannot block new outbox/capabilities or overwrite the new pairing with delayed failures', async () => {
  const { page } = await open({ init: () => {
    const original = window.fetch;
    window.oldRequests = [];
    window.fetch = (url, options) => {
      if (options?.headers?.['X-Beam-Token'] === 'old-token' && ['/outbox', '/capabilities'].includes(url)) {
        return new Promise(resolve => window.oldRequests.push(() => resolve(new Response('', { status: 401 }))));
      }
      return original(url, options);
    };
  } });
  try {
    await page.waitForFunction(() => window.oldRequests.length === 2);
    await page.evaluate(() => { document.getElementById('tokenInput').value = 'new-token'; document.getElementById('saveTokenButton').click(); });
    await connected(page);
    await page.waitForFunction(() => !document.getElementById('noteInput').disabled);
    await page.evaluate(() => window.oldRequests.forEach(release => release()));
    await page.waitForTimeout(50);
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), 'new-token');
    assert.equal(await page.locator('#connectionStatus').innerText(), 'Connected');
    assert.equal(await page.locator('#noteInput').isEnabled(), true);
  } finally { await page.close(); }
});

for (const status of [401, 503, 204]) test(`a delayed old heartbeat ${status} cannot relabel a newly validated pairing`, async () => {
  const { page } = await open({ init: () => {
    const original = window.fetch;
    window.oldHeartbeat = null;
    window.fetch = (url, options) => url === '/connection/heartbeat' && options.headers['X-Beam-Token'] === 'old-token'
      ? new Promise(resolve => { window.oldHeartbeat = value => resolve(new Response(null, { status: value })); })
      : original(url, options);
  } });
  try {
    await connected(page);
    await page.waitForFunction(() => window.oldHeartbeat !== null);
    await page.evaluate(() => { document.getElementById('tokenInput').value = 'new-token'; document.getElementById('saveTokenButton').click(); });
    await connected(page);
    await page.evaluate(value => window.oldHeartbeat(value), status);
    await page.waitForTimeout(50);
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), 'new-token');
    assert.equal(await page.locator('#connectionStatus').innerText(), 'Connected');
  } finally { await page.close(); }
});

test('foreground/pageshow/online recovery keeps prepared downloads and Vault and never replays a failed upload', async () => {
  const { page, beats } = await open();
  let uploads = 0;
  await page.route('**/receive', route => { uploads++; return route.abort(); });
  try {
    await connected(page);
    await page.getByRole('button', { name: 'Download retained.txt' }).click();
    await page.getByRole('link', { name: 'Save retained.txt' }).waitFor();
    const prepared = await page.getByRole('link', { name: 'Save retained.txt' }).getAttribute('href');
    await page.getByRole('tab', { name: 'Vault', exact: true }).click();
    await page.getByRole('button', { name: 'Download vault.txt' }).click();
    await page.getByRole('link', { name: 'Save vault.txt' }).waitFor();
    const vaultPrepared = await page.getByRole('link', { name: 'Save vault.txt' }).getAttribute('href');
    await page.getByRole('tab', { name: 'Send', exact: true }).click();
    await page.getByLabel('Note or link').fill('Keep this draft');
    await page.getByRole('button', { name: 'Send note' }).click();
    await page.locator('#uploadList [data-state="failed"]').waitFor();
    const before = beats.length;
    await page.evaluate(() => { window.setPageHidden(true); window.setPageHidden(false); window.dispatchEvent(new Event('pageshow')); window.dispatchEvent(new Event('online')); });
    await connected(page);
    await page.waitForTimeout(100);
    assert.ok(beats.length > before);
    assert.equal(uploads, 1);
    assert.equal(await page.getByLabel('Note or link').inputValue(), 'Keep this draft');
    assert.equal(await page.locator('#items a').getAttribute('href'), prepared);
    assert.equal(await page.locator('#vaultItems a').getAttribute('href'), vaultPrepared);
  } finally { await page.close(); }
});

test('connected, unavailable and pairing states fit narrow and desktop widths with screenshots', async () => {
  const { page } = await open();
  const output = path.resolve(__dirname, '../../artifacts/connection-setup/browser');
  try {
    await connected(page);
    await fs.mkdir(output, { recursive: true });
    for (const state of ['connected', 'unavailable', 'pairing']) {
      if (state === 'unavailable') {
        await page.route('**/connection/heartbeat', route => route.abort());
        await page.route('**/outbox', route => route.abort());
        await page.evaluate(() => loadOutbox());
      }
      if (state === 'pairing') {
        await page.route('**/outbox', route => route.fulfill({ status: 401 }));
        await page.evaluate(() => loadOutbox());
      }
      for (const width of [320, 390, 1440]) {
        await page.setViewportSize({ width, height: 900 });
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `${state} at ${width}`);
        const controls = await page.locator(state === 'unavailable' ? '#connectionNotice' : state === 'pairing' ? '#setup' : 'header').evaluate(e => e.scrollWidth <= e.clientWidth);
        assert.equal(controls, true, `${state} controls at ${width}`);
        await page.screenshot({ path: path.join(output, `${engine}-${state}-${width}.png`), fullPage: true });
      }
    }
  } finally { await page.close(); }
});

test('another tab changing pairing immediately clears earlier Receive content and prepared downloads', async () => {
  const note = { id: 'old-note', name: 'Text', isText: true, textContent: 'Earlier pairing note', sizeBytes: 20, contentType: 'text/plain' };
  const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
  const { page } = await open({ context, outbox: route => route.fulfill({ json: [file, note] }) });
  let other;
  try {
    await connected(page);
    await page.getByRole('button', { name: 'Download retained.txt' }).click();
    const save = page.getByRole('link', { name: 'Save retained.txt' });
    await save.waitFor();
    const prepared = await save.getAttribute('href');
    assert.match(await page.locator('#items').innerText(), /Earlier pairing note/);
    assert.equal(await page.evaluate(async url => (await fetch(url)).ok, prepared), true);
    await page.route('**/outbox', route => route.abort());
    // Use a real same-origin second tab so the browser delivers the storage event.
    other = await page.context().newPage();
    await other.route('**/*', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>Paired tab</title>' }));
    await other.goto(`${origin}/paired-tab`);
    await other.evaluate(() => localStorage.setItem('beam-token', 'new-token'));
    await page.waitForFunction(() => document.getElementById('connectionStatus').textContent === 'PC unavailable');
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), 'new-token');
    assert.equal(await page.locator('#items').innerText(), '');
    assert.equal(await page.locator('#receiveCount').innerText(), '');
    assert.equal(await page.evaluate(async url => {
      try { await fetch(url); return true; } catch { return false; }
    }, prepared), false, 'The earlier pairing blob must be revoked immediately.');
  } finally { await context.close(); }
});
