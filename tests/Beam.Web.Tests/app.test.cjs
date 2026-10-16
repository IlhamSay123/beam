const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const { chromium, webkit } = require('playwright');

let server, browser, origin;
const webroot = path.resolve(__dirname, '../../src/Beam.Server/wwwroot');
before(async () => {
  server = http.createServer(async (request, response) => {
    const filename = new URL(request.url, 'http://localhost').pathname.split('/').pop() || 'index.html';
    const types = { 'index.html': 'text/html', 'app.js': 'text/javascript', 'styles.css': 'text/css' };
    if (!types[filename]) { response.writeHead(404).end(); return; }
    response.writeHead(200, { 'Content-Type': types[filename] });
    response.end(await fs.readFile(path.join(webroot, filename)));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
  browser = process.env.BEAM_ENGINE === 'webkit' ? await webkit.launch() : await chromium.launch({ headless: true, channel: process.env.BEAM_BROWSER || 'msedge' });
});
after(async () => {
  await browser?.close();
  if (server) await new Promise(resolve => server.close(resolve));
});

const textItem = { id: 'note', name: 'Text', isText: true, textContent: 'hello from PC' };
const fileItem = { id: 'file', name: 'sample.txt', isText: false, sizeBytes: 9, contentType: 'text/plain' };

async function open(items, extra = {}) {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  page.setDefaultTimeout(5000);
  let ackCount = 0;
  await page.route('**/outbox', route => route.fulfill({ json: items }));
  await page.route('**/outbox/*/content', route => route.fulfill({ body: 'file body', contentType: 'text/plain' }));
  await page.route('**/outbox/*/ack', route => {
    ackCount++;
    return route.fulfill({ status: extra.ackStatus || 200 });
  });
  if (extra.init) await page.addInitScript(extra.init);
  await page.goto(`${origin}/app/?token=test-token`);
  await page.waitForFunction(() => document.querySelectorAll('#items li').length > 0);
  return { page, acknowledgements: () => ackCount };
}

test('pairing adopts the QR token and hides setup', async () => {
  const { page } = await open([textItem]);
  try {
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), 'test-token');
    assert.equal(new URL(page.url()).search, '');
    assert.equal(await page.locator('#setup').isVisible(), false,
      JSON.stringify(await page.locator('#setup').evaluate(e => ({ hidden: e.hidden, display: getComputedStyle(e).display }))));
  } finally { await page.close(); }
});

test('plain HTTP clipboard fallback copies text before acknowledging', async () => {
  const { page, acknowledgements } = await open([textItem], {
    init: () => Object.defineProperty(navigator, 'clipboard', { value: undefined })
  });
  try {
    await page.getByRole('button', { name: /Copy/ }).click();
    await page.waitForFunction(() => document.getElementById('status')?.textContent.includes('Copied'));
    assert.equal(acknowledgements(), 1);
  } finally { await page.close(); }
});

test('a failed copy keeps selectable text and does not acknowledge', async () => {
  const { page, acknowledgements } = await open([textItem], {
    init: () => {
      Object.defineProperty(navigator, 'clipboard', { value: undefined });
      document.execCommand = () => false;
    }
  });
  try {
    await page.getByRole('button', { name: /Copy/ }).click();
    await page.waitForFunction(() => document.querySelector('#items textarea') !== null);
    assert.equal(await page.locator('#items textarea').inputValue(), 'hello from PC');
    assert.equal(acknowledgements(), 0);
    assert.equal(await page.locator('#items textarea').isVisible(), true);
  } finally { await page.close(); }
});

test('preparing a file keeps it unread until a direct save tap', async () => {
  const { page, acknowledgements } = await open([fileItem]);
  try {
    await page.getByRole('button', { name: /Download sample.txt/ }).click();
    const link = page.getByRole('link', { name: /Save sample.txt/ });
    await link.waitFor();
    assert.equal(acknowledgements(), 0);
    await page.evaluate(() => loadOutbox());
    assert.equal(await link.isVisible(), true);
    const downloaded = page.waitForEvent('download');
    await link.click();
    const download = await downloaded;
    assert.equal(download.suggestedFilename(), 'sample.txt');
    assert.equal(await download.failure(), null);
    assert.equal(await fs.readFile(await download.path(), 'utf8'), 'file body');
    await page.waitForFunction(() => document.getElementById('status')?.textContent.includes('Download started'));
    assert.equal(acknowledgements(), 1);
  } finally { await page.close(); }
});

test('invalid token clears stale items and returns to pairing', async () => {
  const { page } = await open([textItem]);
  try {
    await page.route('**/outbox', route => route.fulfill({ status: 401 }));
    await page.evaluate(() => loadOutbox());
    assert.equal(await page.locator('#setup').isVisible(), true);
    assert.equal(await page.locator('#items li').count(), 0);
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), null);
  } finally { await page.close(); }
});

test('network failure is visible and retains items for retry', async () => {
  const { page } = await open([textItem]);
  try {
    await page.route('**/outbox', route => route.abort());
    await page.evaluate(() => loadOutbox());
    assert.match(await page.locator('#status').textContent(), /connect/i);
    assert.equal(await page.locator('#items li').count(), 1);
  } finally { await page.close(); }
});

test('failed acknowledgement reports the failure', async () => {
  const { page } = await open([textItem], { ackStatus: 500 });
  try {
    await page.getByRole('button', { name: /Copy/ }).click();
    await page.waitForFunction(() => document.getElementById('status')?.textContent.includes('confirm'));
    assert.equal(await page.locator('#items li').count(), 1);
  } finally { await page.close(); }
});

test('long names and text fit mobile and desktop widths', async () => {
  const { page } = await open([
    { ...fileItem, name: 'a'.repeat(180) + '.jpg' },
    { ...textItem, textContent: 'b'.repeat(300) }
  ]);
  try {
    for (const width of [320, 390, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      const overflowing = await page.locator('.item').evaluateAll(elements => elements.some(e => e.scrollWidth > e.clientWidth));
      assert.equal(overflowing, false);
      if (process.env.BEAM_SCREENSHOTS) {
        await page.waitForTimeout(250);
        await fs.mkdir(process.env.BEAM_SCREENSHOTS, { recursive: true });
        await page.screenshot({ path: path.join(process.env.BEAM_SCREENSHOTS, `beam-${width}.png`), fullPage: true });
      }
    }
  } finally { await page.close(); }
});

test('file preparation survives the shorter metadata timeout', async () => {
  const { page } = await open([fileItem], {
    init: () => {
      const timeout = AbortSignal.timeout.bind(AbortSignal);
      AbortSignal.timeout = () => timeout(50);
    }
  });
  try {
    await page.route('**/outbox/*/content', async route => {
      await new Promise(resolve => setTimeout(resolve, 150));
      await route.fulfill({ body: 'file body' });
    });
    await page.getByRole('button', { name: /Download sample.txt/ }).click();
    await page.getByRole('link', { name: /Save sample.txt/ }).waitFor();
  } finally { await page.close(); }
});

test('a new polled item does not strand an in-progress download', async () => {
  const { page } = await open([fileItem]);
  let finish;
  const ready = new Promise(resolve => { finish = resolve; });
  try {
    await page.route('**/outbox/*/content', async route => {
      await ready;
      await route.fulfill({ body: 'file body' });
    });
    await page.getByRole('button', { name: /Download sample.txt/ }).click();
    await page.route('**/outbox', route => route.fulfill({ json: [textItem, fileItem] }));
    await page.evaluate(() => loadOutbox());
    finish();
    await page.getByRole('link', { name: /Save sample.txt/ }).waitFor();
    assert.equal(await page.getByRole('button', { name: /Copy/ }).count(), 1);
  } finally { finish(); await page.close(); }
});
