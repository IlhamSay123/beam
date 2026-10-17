const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const { chromium, webkit } = require('playwright');

let server, browser, origin;
const engine = process.env.BEAM_ENGINE || 'chromium';
const root = path.resolve(__dirname, '../../src/Beam.Server/wwwroot');
const artifacts = path.resolve(__dirname, '../../artifacts/safe-retries/browser');
const limits = { maxUploadBytes: 104857600, maxTextBytes: 65536, safeRetryVersion: 1 };
const file = (name = 'same.txt', content = 'original bytes') => ({ name, mimeType: 'text/plain', buffer: Buffer.from(content) });
before(async () => {
  server = http.createServer(async (req, res) => {
    const name = new URL(req.url, 'http://localhost').pathname.split('/').pop() || 'index.html';
    const types = { 'index.html': 'text/html', 'app.js': 'text/javascript', 'vault.js': 'text/javascript', 'motion.js': 'text/javascript', 'lucide.min.js': 'text/javascript', 'styles.css': 'text/css' };
    if (!types[name]) return res.writeHead(404).end();
    res.writeHead(200, { 'Content-Type': types[name] }).end(await fs.readFile(path.join(root, name)));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
  browser = engine === 'webkit' ? await webkit.launch() : await chromium.launch({ channel: process.env.BEAM_BROWSER || 'msedge' });
});
after(async () => { await browser?.close(); if (server) await new Promise(resolve => server.close(resolve)); });

async function open(options = {}) {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  page.setDefaultTimeout(3000);
  const posts = [], checks = [], order = [];
  await page.route('**/outbox', route => route.fulfill({ json: [] }));
  await page.route('**/connection/heartbeat', route => route.fulfill({ status: 204 }));
  await page.route('**/capabilities', route => route.fulfill({ json: options.capabilities || limits }));
  await page.route('**/receive', route => {
    posts.push(route.request()); order.push('post');
    return options.post ? options.post(route, posts.length) : route.fulfill({ status: 500 });
  });
  await page.route('**/receive/receipts/*', route => {
    checks.push(route.request()); order.push('check');
    return options.receipt ? options.receipt(route, checks.length) : route.fulfill({ status: 404 });
  });
  await page.addInitScript(() => {
    Object.defineProperty(crypto, 'randomUUID', { value: undefined });
    Object.defineProperty(crypto, 'subtle', { value: undefined });
    const send = XMLHttpRequest.prototype.send;
    window.uploadCallbacks = [];
    XMLHttpRequest.prototype.send = function (...args) {
      window.uploadCallbacks.push({ xhr: this, load: this.onload, error: this.onerror, timeout: this.ontimeout, abort: this.onabort, progress: this.upload.onprogress });
      return send.apply(this, args);
    };
  });
  if (options.init) await page.addInitScript(options.init);
  await page.goto(`${origin}/app/?token=test-token`);
  await page.getByRole('tab', { name: 'Send', exact: true }).click();
  return { page, posts, checks, order };
}
async function failed(page) { await page.locator('#uploadList [data-state="failed"]').waitFor(); }
async function sent(page, count = 1) { await page.waitForFunction(n => document.querySelectorAll('#uploadList [data-state="sent"]').length === n, count); }
async function retry(page, name = 'same.txt') { await page.getByRole('button', { name: 'Retry ' + name, exact: true }).click(); }
async function shot(page, state) {
  await fs.mkdir(artifacts, { recursive: true });
  for (const width of [320, 390]) {
    await page.setViewportSize({ width, height: 844 });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), state + ' must fit');
    await page.screenshot({ path: path.join(artifacts, `${engine}-${state}-${width}.png`), fullPage: true });
  }
}

for (const version of [undefined, 0, 2, '1']) test(`unsupported safeRetryVersion ${version} blocks new sends`, async () => {
  const { page, posts } = await open({ capabilities: { ...limits, safeRetryVersion: version } });
  try {
    await page.waitForFunction(() => document.getElementById('sendStatus').textContent.includes('Sending unavailable'));
    assert.equal(await page.locator('#fileInput').isDisabled(), true);
    assert.equal(await page.locator('#sendNoteButton').isDisabled(), true);
    assert.match(await page.locator('#sendStatus').innerText(), /update|safe retr/i);
    assert.equal(posts.length, 0);
  } finally { await page.close(); }
});

test('lost response is confirmed by receipt without a second upload, and releases bytes', async () => {
  const { page, posts, checks, order } = await open({
    post: route => route.abort(), receipt: route => route.fulfill({ json: { ids: ['original-record'] } }),
  });
  try {
    await page.locator('#fileInput').setInputFiles(file());
    await failed(page);
    assert.match(posts[0].headers()['x-beam-transfer-id'] || '', /^[a-f0-9]{32}$/);
    assert.equal(await page.evaluate(() => uploadJobs[0].file.size), 14);
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.waitForFunction(() => !document.getElementById('fileInput').disabled);
    assert.equal(posts.length, 1, 'recovery must never replay a failed upload');
    await retry(page); await sent(page);
    assert.equal(posts.length, 1);
    assert.deepEqual(order, ['post', 'check']);
    assert.equal(new URL(checks[0].url()).pathname, '/receive/receipts/' + posts[0].headers()['x-beam-transfer-id']);
    assert.equal(checks[0].headers()['x-beam-token'], 'test-token');
    assert.equal(await page.evaluate(() => uploadJobs[0].file), null);
    assert.equal(await page.locator('#uploadList .upload-state').innerText(), 'Already received on PC');
    assert.equal(await page.locator('#motionState').innerText(), 'Already received on PC');
    assert.equal(await page.locator('.motion-packet').evaluate(packet => packet.getAnimations().length), 0,
      'confirming an existing receipt must not animate another transfer of bytes');
    await shot(page, 'already-received');
  } finally { await page.close(); }
});

test('404 resends the retained file with the same key; intentional identical jobs get new keys', async () => {
  const { page, posts, order } = await open({ post: (route, n) => route.fulfill(n === 1 ? { status: 500 } : { json: { ids: ['saved-' + n] } }) });
  try {
    await page.locator('#fileInput').setInputFiles(file()); await failed(page);
    assert.equal(await page.evaluate(async () => new TextDecoder().decode(await uploadJobs[0].file.arrayBuffer())), 'original bytes');
    await retry(page); await sent(page);
    await page.locator('#fileInput').setInputFiles([file(), file()]); await sent(page, 3);
    const keys = posts.map(r => r.headers()['x-beam-transfer-id']);
    assert.deepEqual(order, ['post', 'check', 'post', 'post', 'post']);
    assert.equal(keys[0], keys[1]);
    assert.equal(new Set(keys).size, 3);
    for (const request of posts) {
      assert.match(request.headers()['x-beam-transfer-id'], /^[a-f0-9]{32}$/);
      assert.match(request.postData(), /filename="same.txt"/);
      assert.doesNotMatch(request.postData(), /name="text"/);
    }
  } finally { await page.close(); }
});

for (const version of [undefined, 2]) test(`capability rejection ${version} during a receipt check blocks its 404 resend until explicit retry`, async () => {
  let release, requested;
  const pending = new Promise(resolve => { release = resolve; });
  const started = new Promise(resolve => { requested = resolve; });
  const { page, posts, checks } = await open({
    post: (route, n) => route.fulfill(n === 1 ? { status: 500 } : { json: { ids: ['saved'] } }),
    receipt: async (route, n) => {
      if (n === 1) { requested(); await pending; }
      await route.fulfill({ status: 404 }).catch(() => {});
    },
    init: () => {
      const send = XMLHttpRequest.prototype.send;
      window.retryFiles = [];
      XMLHttpRequest.prototype.send = function (body) {
        if (body instanceof FormData) window.retryFiles.push(body.get('files'));
        return send.call(this, body);
      };
    },
  });
  try {
    await page.locator('#fileInput').setInputFiles(file()); await failed(page);
    const key = posts[0].headers()['x-beam-transfer-id'];
    await page.waitForFunction(() => loadingCapabilities === null);
    await retry(page); await started;
    await page.locator('[data-state="checking"]').waitFor();
    await page.route('**/capabilities', route => route.fulfill({ json: { ...limits, safeRetryVersion: version } }));
    await page.evaluate(() => loadCapabilities());
    assert.equal(await page.locator('#fileInput').isDisabled(), true);
    assert.match(await page.locator('#sendStatus').innerText(), /Sending unavailable/);
    release();
    await page.waitForFunction(() => uploadJobs[0].attempt === null);
    assert.equal(posts.length, 1, 'a receipt 404 must not bypass rejected safe-retry capabilities');
    await failed(page);
    assert.match(await page.locator('#uploadList .upload-state').innerText(), /unconfirmed/i);
    assert.equal(await page.evaluate(async () => new TextDecoder().decode(await uploadJobs[0].file.arrayBuffer())), 'original bytes');
    assert.equal(await page.getByRole('button', { name: 'Retry same.txt' }).isDisabled(), true);

    await page.route('**/capabilities', route => route.fulfill({ json: limits }));
    await page.evaluate(() => loadCapabilities());
    assert.equal(await page.locator('#fileInput').isDisabled(), false);
    assert.equal(posts.length, 1, 'fresh capabilities must not automatically replay the blocked attempt');
    await retry(page); await sent(page);
    assert.equal(posts.length, 2);
    assert.equal(checks.length, 2);
    assert.equal(posts[1].headers()['x-beam-transfer-id'], key);
    assert.ok(checks.every(request => new URL(request.url()).pathname === '/receive/receipts/' + key));
    assert.deepEqual(await page.evaluate(async () => Promise.all(window.retryFiles.map(async value => new TextDecoder().decode(await value.arrayBuffer())))), ['original bytes', 'original bytes']);
  } finally { release(); await page.close(); }
});

test('notes retain exact text and key through retry, then clear only after confirmation', async () => {
  const { page, posts } = await open({ post: (route, n) => route.fulfill(n === 1 ? { status: 429 } : { json: { ids: ['note'] } }) });
  try {
    await page.locator('#noteInput').fill('  https://example.com\nSame note  ');
    await page.locator('#sendNoteButton').click(); await failed(page);
    assert.equal(await page.locator('#noteInput').inputValue(), '  https://example.com\nSame note  ');
    await page.locator('#uploadList button', { hasText: 'Retry' }).click(); await sent(page);
    assert.match(posts[0].headers()['x-beam-transfer-id'] || '', /^[a-f0-9]{32}$/);
    assert.equal(posts[0].headers()['x-beam-transfer-id'], posts[1].headers()['x-beam-transfer-id']);
    assert.match(posts[1].postData(), /  https:\/\/example.com\r?\nSame note  /);
    assert.doesNotMatch(posts[1].postData(), /filename=/);
    assert.equal(await page.locator('#noteInput').inputValue(), '');
  } finally { await page.close(); }
});

test('pending receipts ask to check again without resending', async () => {
  const { page, posts, checks } = await open({ receipt: (route, n) => route.fulfill(n === 1 ? { status: 202 } : { json: { ids: ['saved'] } }) });
  try {
    await page.locator('#fileInput').setInputFiles(file()); await failed(page); await retry(page);
    await page.getByText(/still saving/i).waitFor();
    assert.equal(await page.locator('#uploadList [data-state="sent"]').count(), 0);
    assert.equal(posts.length, 1); assert.equal(checks.length, 1);
    await shot(page, 'pending');
    await page.getByRole('button', { name: 'Check again same.txt' }).click(); await sent(page);
    assert.equal(posts.length, 1); assert.equal(checks.length, 2);
  } finally { await page.close(); }
});

for (const response of [{ status: 500 }, { status: 400 }, { status: 429 }, { status: 204 }, { json: { ids: [] } }, { json: { ids: ['one', 'two'] } }, { json: { ids: [42] } }, { json: null }, { body: 'not json', contentType: 'application/json' }, { abort: true }]) {
  test(`receipt ${JSON.stringify(response)} never permits another POST`, async () => {
    const { page, posts, checks } = await open({ receipt: route => response.abort ? route.abort() : route.fulfill(response) });
    try {
      await page.locator('#fileInput').setInputFiles(file()); await failed(page); await retry(page); await failed(page);
      assert.equal(posts.length, 1); assert.equal(checks.length, 1);
      assert.equal(await page.locator('#uploadList [data-state="sent"]').count(), 0);
      assert.equal(await page.evaluate(() => uploadJobs[0].file.size), 14);
    } finally { await page.close(); }
  });
}

test('receipt 401 returns to pairing without posting again', async () => {
  const { page, posts } = await open({ receipt: route => route.fulfill({ status: 401 }) });
  try {
    await page.locator('#fileInput').setInputFiles(file()); await failed(page); await retry(page);
    await page.locator('#setup').waitFor({ state: 'visible' });
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), null);
    assert.equal(posts.length, 1);
  } finally { await page.close(); }
});

for (const endpoint of ['upload', 'receipt']) test(`${endpoint} 409 conflict is nonretryable and preserves the failed bytes`, async () => {
  const { page, posts } = await open({
    post: route => route.fulfill({ status: endpoint === 'upload' ? 409 : 500 }),
    receipt: route => route.fulfill({ status: 409 }),
  });
  try {
    await page.locator('#fileInput').setInputFiles(file()); await failed(page);
    if (endpoint === 'receipt') { await retry(page); await failed(page); }
    assert.equal(await page.getByRole('button', { name: 'Retry same.txt' }).count(), 0);
    assert.match(await page.locator('#uploadList').innerText(), /conflict/i);
    assert.equal(await page.evaluate(() => uploadJobs[0].file.size), 14);
    assert.equal(posts.length, 1);
  } finally { await page.close(); }
});

test('receipt checking is cancellable, double-click safe and survives Clear finished', async () => {
  let release; const pending = new Promise(resolve => { release = resolve; });
  const { page, posts, checks } = await open({ receipt: async route => { await pending; await route.fulfill({ json: { ids: ['late'] } }).catch(() => {}); } });
  try {
    await page.locator('#fileInput').setInputFiles(file()); await failed(page);
    await page.evaluate(() => { const retry = document.querySelector('#uploadList button'); retry.click(); retry.click(); });
    await page.locator('[data-state="checking"]').waitFor();
    await page.getByRole('button', { name: 'Clear finished' }).click();
    assert.equal(await page.locator('#uploadList li').count(), 1);
    assert.equal(checks.length, 1); assert.equal(posts.length, 1);
    assert.equal(await page.locator('#motionState').innerText(), 'Checking PC...');
    await shot(page, 'checking');
    await page.getByRole('button', { name: 'Cancel same.txt' }).click();
    release();
    await page.locator('[data-state="cancelled"]').waitFor();
    assert.equal(await page.locator('[data-state="sent"]').count(), 0);
    assert.equal(posts.length, 1);
  } finally { release(); await page.close(); }
});

test('uncertain cancellation reuses the key and stale XHR callbacks cannot change a newer attempt', async () => {
  let release; const pending = new Promise(resolve => { release = resolve; });
  const { page, posts } = await open({ post: async (route, n) => {
    if (n === 1) await pending;
    await route.fulfill({ json: { ids: ['saved'] } }).catch(() => {});
  } });
  try {
    await page.locator('#fileInput').setInputFiles(file());
    await page.waitForFunction(() => window.uploadCallbacks.length === 1);
    await page.getByRole('button', { name: 'Cancel same.txt' }).click(); await retry(page); await sent(page);
    await page.evaluate(() => {
      const old = window.uploadCallbacks[0];
      Object.defineProperty(old.xhr, 'status', { value: 401 });
      old.load(); old.error(); old.timeout(); old.abort(); old.progress({ lengthComputable: true, loaded: 1, total: 2 });
    });
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), 'test-token');
    assert.equal(await page.locator('#fileInput').isDisabled(), false);
    assert.equal(await page.locator('[data-state="sent"]').count(), 1);
    assert.match(posts[0].headers()['x-beam-transfer-id'] || '', /^[a-f0-9]{32}$/);
    assert.equal(posts[0].headers()['x-beam-transfer-id'], posts[1].headers()['x-beam-transfer-id']);
  } finally { release(); await page.close(); }
});

for (const change of ['pairing', 'attempt']) for (const status of [200, 401, 404]) test(`stale receipt ${status} cannot affect a newer ${change} even if abort is ignored`, async () => {
  const { page, posts } = await open({ init: () => {
    const original = window.fetch;
    let held = false;
    window.fetch = (url, options) => {
      if (String(url).includes('/receive/receipts/') && !held) {
        held = true;
        return new Promise(resolve => { window.releaseReceipt = status => resolve(new Response(JSON.stringify({ ids: ['old-record'] }), { status })); });
      }
      return original(url, options);
    };
  }, receipt: route => route.fulfill({ json: { ids: ['new-record'] } }) });
  try {
    await page.locator('#fileInput').setInputFiles(file()); await failed(page); await retry(page);
    await page.waitForFunction(() => Boolean(window.releaseReceipt));
    if (change === 'pairing') {
      await page.evaluate(() => {
        // Re-pairing with the same token must still invalidate the previous version.
        window.dispatchEvent(new StorageEvent('storage', { key: 'beam-token' }));
      });
    } else await page.getByRole('button', { name: 'Cancel same.txt' }).click();
    await page.waitForFunction(() => !document.getElementById('fileInput').disabled);
    await retry(page); await sent(page);
    await page.evaluate(status => window.releaseReceipt(status), status);
    await page.evaluate(() => new Promise(resolve => setTimeout(resolve, 0)));
    assert.equal(posts.length, 1);
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), 'test-token');
    assert.equal(await page.locator('[data-state="sent"]').count(), 1);
    assert.equal(await page.locator('#setup').isVisible(), false);
  } finally { await page.close(); }
});
