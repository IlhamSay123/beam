const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const { chromium, webkit } = require('playwright');
let server, browser, origin;
before(async () => {
  const root = path.resolve(__dirname, '../../src/Beam.Server/wwwroot');
  server = http.createServer(async (req, res) => {
    const filename = new URL(req.url, 'http://localhost').pathname.split('/').pop() || 'index.html';
    if (!['index.html', 'app.js', 'styles.css'].includes(filename)) return res.writeHead(404).end();
    res.writeHead(200, { 'Content-Type': filename.endsWith('.js') ? 'text/javascript' : filename.endsWith('.css') ? 'text/css' : 'text/html' });
    res.end(await fs.readFile(path.join(root, filename)));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
  browser = process.env.BEAM_ENGINE === 'webkit' ? await webkit.launch() : await chromium.launch({ channel: process.env.BEAM_BROWSER || 'msedge' });
});
after(async () => { await browser?.close(); if (server) await new Promise(resolve => server.close(resolve)); });
async function open(handler, maxUploadBytes = 104857600) {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  page.setDefaultTimeout(4000);
  await page.route('**/outbox', route => route.fulfill({ json: [] }));
  await page.route('**/capabilities', route => route.fulfill({ json: { maxUploadBytes, maxTextBytes: 65536 } }));
  await page.route('**/receive', handler);
  await page.goto(`${origin}/app/?token=test-token`);
  await page.getByRole('tab', { name: 'Send' }).click();
  return page;
}
const file = (name, content = 'photo bytes') => ({ name, mimeType: 'image/png', buffer: Buffer.from(content) });

test('Safari composer sends notes and URLs using authenticated form data', async () => {
  let request;
  const page = await open(route => { request = route.request(); return route.fulfill({ json: { ids: ['note-id'] } }); });
  try {
    await page.getByLabel('Note or link').fill('https://example.com/hello\nA note');
    await page.getByRole('button', { name: 'Send note' }).click();
    await page.getByText('Sent to PC', { exact: true }).waitFor();
    assert.equal(request.headers()['x-beam-token'], 'test-token');
    assert.match(request.headers()['content-type'], /multipart\/form-data; boundary=/);
    assert.match(request.postData(), /name="text"/);
    assert.match(request.postData(), /https:\/\/example.com\/hello/);
    assert.equal(await page.getByLabel('Note or link').inputValue(), '');
  } finally { await page.close(); }
});

test('multiple files keep names and are confirmed separately', async () => {
  const requests = [];
  const page = await open(route => { requests.push(route.request()); return route.fulfill({ json: { ids: ['file-id'] } }); });
  try {
    await page.locator('#fileInput').setInputFiles([file('first.png', 'first bytes'), file('second.png', 'second bytes')]);
    await page.waitForFunction(() => document.querySelectorAll('[data-state="sent"]').length === 2);
    assert.equal(requests.length, 2);
    assert.match(requests[0].postData(), /filename="first.png"/);
    assert.match(requests[1].postData(), /filename="second.png"/);
    // WebKit interception can omit file bodies; integration.test.cjs checks real saved bytes.
    assert.equal(requests[0].headers()['x-beam-token'], 'test-token');
    if (process.env.BEAM_SCREENSHOTS) {
      await fs.mkdir(process.env.BEAM_SCREENSHOTS, { recursive: true });
      await page.screenshot({ path: path.join(process.env.BEAM_SCREENSHOTS, 'beam-send-demo.png'), fullPage: true });
    }
  } finally { await page.close(); }
});

test('failure stays visible and retry does not resend successful files', async () => {
  const names = [];
  let failed = false;
  const page = await open(route => {
    const name = /filename="([^"]+)"/.exec(route.request().postData())[1]; names.push(name);
    if (name === 'second.png' && !failed) { failed = true; return route.fulfill({ status: 500 }); }
    return route.fulfill({ json: { ids: ['saved'] } });
  });
  try {
    await page.locator('#fileInput').setInputFiles([file('first.png'), file('second.png')]);
    await page.getByRole('button', { name: 'Retry second.png' }).click();
    await page.waitForFunction(() => document.querySelectorAll('[data-state="sent"]').length === 2);
    assert.deepEqual(names, ['first.png', 'second.png', 'second.png']);
  } finally { await page.close(); }
});

test('an empty success response is not reported as sent', async () => {
  const page = await open(route => route.fulfill({ json: { ids: [] } }));
  try {
    await page.locator('#fileInput').setInputFiles(file('photo.png'));
    await page.getByRole('button', { name: 'Retry photo.png' }).waitFor();
    assert.equal(await page.locator('[data-state="sent"]').count(), 0);
    assert.match(await page.locator('#uploadList').textContent(), /confirm/i);
  } finally { await page.close(); }
});

test('uploaded bytes are not reported as saved until the PC confirms', async () => {
  let release; const pending = new Promise(resolve => { release = resolve; });
  const page = await open(async route => { await pending; await route.fulfill({ json: { ids: ['confirmed'] } }); });
  try {
    await page.locator('#fileInput').setInputFiles(file('photo.png'));
    await page.locator('[data-state="uploading"], [data-state="confirming"]').waitFor();
    assert.equal(await page.locator('[data-state="sent"]').count(), 0);
    assert.ok(await page.getByRole('progressbar').isVisible());
    release();
    await page.getByText('Sent to PC', { exact: true }).waitFor();
  } finally { release(); await page.close(); }
});

test('cancel does not claim success and a late response cannot change it', async () => {
  let release; const pending = new Promise(resolve => { release = resolve; });
  const page = await open(async route => { await pending; await route.fulfill({ json: { ids: ['late'] } }).catch(() => {}); });
  try {
    await page.locator('#fileInput').setInputFiles(file('photo.png'));
    await page.getByRole('button', { name: 'Cancel photo.png' }).click();
    release();
    await page.getByRole('button', { name: 'Retry photo.png' }).waitFor();
    assert.equal(await page.locator('[data-state="sent"]').count(), 0);
    assert.match(await page.locator('#uploadList').textContent(), /cancel/i);
  } finally { release(); await page.close(); }
});

test('401 pauses queued files and returns to pairing', async () => {
  let attempts = 0;
  const page = await open(route => { attempts++; return route.fulfill({ status: 401 }); });
  try {
    await page.locator('#fileInput').setInputFiles([file('one.png'), file('two.png')]);
    await page.locator('#setup').waitFor({ state: 'visible' });
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), null);
    assert.equal(attempts, 1);
    assert.equal(await page.locator('[data-state="sent"]').count(), 0);
  } finally { await page.close(); }
});

test('client size limit rejects a large file without sending it', async () => {
  let attempts = 0;
  const page = await open(route => { attempts++; return route.fulfill({ json: { ids: ['saved'] } }); }, 10);
  try {
    await page.waitForFunction(() => document.getElementById('uploadLimit').textContent.includes('10 B'));
    await page.locator('#fileInput').setInputFiles(file('too-big.png', '12345678901'));
    await page.getByText(/File exceeds/).waitFor();
    assert.equal(attempts, 0);
  } finally { await page.close(); }
});

test('a delayed rejection from the old token cannot discard a successful re-pair', async () => {
  const page = await browser.newPage();
  page.setDefaultTimeout(4000);
  let release, started;
  const pending = new Promise(resolve => { release = resolve; });
  const requested = new Promise(resolve => { started = resolve; });
  await page.route('**/outbox', route => route.fulfill(route.request().headers()['x-beam-token'] === 'old-token' ? { status: 401 } : { json: [] }));
  await page.route('**/capabilities', async route => {
    if (route.request().headers()['x-beam-token'] === 'old-token') {
      started(); await pending;
      return route.fulfill({ status: 401 }).catch(() => {});
    }
    return route.fulfill({ json: { maxUploadBytes: 104857600, maxTextBytes: 65536 } });
  });
  try {
    await page.goto(`${origin}/app/?token=old-token`);
    await requested;
    await page.locator('#setup').waitFor({ state: 'visible' });
    await page.locator('#tokenInput').fill('new-token');
    await page.locator('#saveTokenButton').click();
    await page.getByText('Connected', { exact: true }).waitFor();
    release();
    await page.waitForFunction(() => !document.getElementById('fileInput').disabled);
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), 'new-token');
    assert.equal(await page.locator('#setup').isVisible(), false);
  } finally { release(); await page.close(); }
});

test('an old upload rejection cannot clear a token corrected in another tab', async () => {
  let release; const pending = new Promise(resolve => { release = resolve; });
  let started; const requested = new Promise(resolve => { started = resolve; });
  const page = await open(async route => { started(); await pending; await route.fulfill({ status: 401 }).catch(() => {}); });
  try {
    await page.locator('#fileInput').setInputFiles(file('photo.png'));
    await requested;
    await page.evaluate(() => localStorage.setItem('beam-token', 'corrected-token'));
    release();
    await page.waitForFunction(() => document.querySelector('[data-state="failed"]'));
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), 'corrected-token');
    assert.equal(await page.locator('#setup').isVisible(), false);
  } finally { release(); await page.close(); }
});

test('send and receive remain usable at small phone and desktop widths', async () => {
  const page = await open(route => route.fulfill({ json: { ids: ['saved'] } }));
  try {
    await page.locator('#fileInput').setInputFiles(file('a'.repeat(160) + '.png'));
    await page.getByText('Sent to PC', { exact: true }).waitFor();
    for (const width of [320, 390, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      if (process.env.BEAM_SCREENSHOTS) {
        await fs.mkdir(process.env.BEAM_SCREENSHOTS, { recursive: true });
        await page.screenshot({ path: path.join(process.env.BEAM_SCREENSHOTS, `beam-send-${width}.png`), fullPage: true });
      }
    }
  } finally { await page.close(); }
});
