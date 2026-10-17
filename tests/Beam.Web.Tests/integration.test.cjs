const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const { chromium, webkit } = require('playwright');
let child, browser, origin, directory;
before(async () => {
  directory = await fs.mkdtemp(path.join(os.tmpdir(), 'beam-browser-'));
  const dll = path.resolve(__dirname, '../Beam.Web.Host/bin/Release/net10.0/Beam.Web.Host.dll');
  await fs.access(dll).catch(() => { throw new Error('Build tests/Beam.Web.Host/Beam.Web.Host.csproj --configuration Release before npm test.'); });
  child = spawn('dotnet', [dll, directory], { stdio: ['pipe', 'pipe', 'pipe'] });
  origin = await new Promise((resolve, reject) => {
    let output = '';
    const timeout = setTimeout(() => reject(new Error('Integration server timed out: ' + output)), 20000);
    child.stdout.on('data', chunk => {
      output += chunk;
      const match = output.match(/BEAM_TEST_URL=(http:\/\/[^\s]+)/);
      if (match) { clearTimeout(timeout); resolve(match[1]); }
    });
    child.stderr.on('data', chunk => { output += chunk; });
    child.once('error', error => { clearTimeout(timeout); reject(error); });
    child.once('exit', code => { clearTimeout(timeout); reject(new Error('Integration server exited: ' + code + ' ' + output)); });
  });
  browser = process.env.BEAM_ENGINE === 'webkit' ? await webkit.launch() : await chromium.launch({ channel: process.env.BEAM_BROWSER || 'msedge' });
});
after(async () => {
  await browser?.close();
  if (child && child.exitCode === null) {
    const exited = once(child, 'exit');
    child.stdin.end('stop\n');
    const timeout = setTimeout(() => child.kill(), 5000);
    try { await exited; } finally { clearTimeout(timeout); }
  }
  if (directory) await fs.rm(directory, { recursive: true, force: true });
});

test('real connection heartbeat validates pairing without acknowledging or creating transfers', async () => {
  const beforeHistory = await fs.readFile(path.join(directory, 'history.json'), 'utf8');
  const endpoint = `${origin}/connection/heartbeat`;
  const sessionId = '00112233445566778899aabbccddeeff';
  const send = (body, token = 'browser-test-token') => fetch(endpoint, {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Beam-Token': token }, body: JSON.stringify(body)
  });
  const rejected = await send({ sessionId }, 'wrong-token');
  assert.equal(rejected.status, 401);
  const accepted = await send({ sessionId });
  assert.equal(accepted.status, 204);
  assert.equal(accepted.headers.get('cache-control'), 'no-store');
  assert.equal(await accepted.text(), '');
  assert.equal((await send({ sessionId: 'not-a-browser' })).status, 400);
  assert.equal(await fs.readFile(path.join(directory, 'history.json'), 'utf8'), beforeHistory);
});

test('real Safari page verifies reachability with a token-authenticated foreground heartbeat', async () => {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  try {
    const heartbeat = page.waitForResponse(response => new URL(response.url()).pathname === '/connection/heartbeat');
    await page.goto(`${origin}/app/?token=browser-test-token`);
    const response = await heartbeat;
    assert.equal(response.status(), 204);
    assert.equal(response.request().method(), 'POST');
    assert.equal(response.request().headers()['x-beam-token'], 'browser-test-token');
    const body = response.request().postDataJSON();
    assert.match(body.sessionId, /^[a-f0-9]{32}$/i);
    assert.deepEqual(Object.keys(body), ['sessionId']);
    await page.waitForFunction(() => document.getElementById('connectionStatus').textContent === 'Connected');
    assert.equal(new URL(page.url()).search, '');
    assert.equal(await page.locator('#setup').isVisible(), false);
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), 'browser-test-token');
  } finally { await page.close(); }
});

test('real server receives browser batches, original names, bytes, and notes without Shortcuts', async () => {
  const page = await browser.newPage();
  try {
    await page.goto(`${origin}/app/?token=browser-test-token`);
    await page.getByRole('tab', { name: 'Send' }).click();
    await page.locator('#fileInput').setInputFiles([
      { name: 'caf\u00e9.png', mimeType: 'image/png', buffer: Buffer.from([137, 80, 78, 71, 0, 255, 10]) },
      { name: 'same.txt', mimeType: 'text/plain', buffer: Buffer.from('one') },
      { name: 'same.txt', mimeType: 'text/plain', buffer: Buffer.from('two') }
    ]);
    await page.waitForFunction(() => document.querySelectorAll('[data-state="sent"]').length === 3);
    await page.getByLabel('Note or link').fill('Safari note\nhttps://example.com');
    await page.getByRole('button', { name: 'Send note' }).click();
    await page.waitForFunction(() => document.querySelectorAll('[data-state="sent"]').length === 4);
    const items = JSON.parse(await fs.readFile(path.join(directory, 'history.json'), 'utf8')).filter(item => item.Direction === 'inbound');
    assert.equal(items.length, 4);
    const image = items.find(item => item.Name === 'caf\u00e9.png');
    assert.ok(image);
    assert.equal(image.ContentType, 'image/png');
    assert.deepEqual(await fs.readFile(image.FilePath), Buffer.from([137, 80, 78, 71, 0, 255, 10]));
    const duplicates = items.filter(item => item.Name === 'same.txt');
    assert.equal(new Set(duplicates.map(item => item.FilePath)).size, 2);
    assert.deepEqual((await Promise.all(duplicates.map(item => fs.readFile(item.FilePath, 'utf8')))).sort(), ['one', 'two']);
    assert.equal(items.find(item => item.IsText).TextContent.replace(/\r\n/g, '\n'), 'Safari note\nhttps://example.com');
  } finally { await page.close(); }
});

test('real server downloads and acknowledges PC files in the same browser session', async () => {
  const page = await browser.newPage();
  try {
    await page.goto(`${origin}/app/?token=browser-test-token`);
    await page.getByRole('button', { name: 'Download from-pc.txt' }).click();
    const downloadEvent = page.waitForEvent('download');
    await page.getByRole('link', { name: 'Save from-pc.txt' }).click();
    const download = await downloadEvent;
    assert.equal(await fs.readFile(await download.path(), 'utf8'), 'Bytes from the PC');
    await page.waitForFunction(() => document.getElementById('status').textContent.includes('Download started'));
    const item = JSON.parse(await fs.readFile(path.join(directory, 'history.json'), 'utf8')).find(item => item.Direction === 'outbound' && !item.IsText);
    assert.equal(item.Read, true);
    assert.ok(await page.getByRole('button', { name: 'Copy: Note from the PC' }).isVisible());
  } finally { await page.close(); }
});

test('PC messages leave Receive after copying but remain reusable in the real Vault', async () => {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  try {
    await page.goto(`${origin}/app/?token=browser-test-token`);
    await page.getByRole('button', { name: 'Copy: Note from the PC' }).click();
    await page.waitForFunction(() => !document.querySelector('#items')?.textContent.includes('Note from the PC'));
    await page.getByRole('tab', { name: 'Vault', exact: true }).click();
    const entry = page.locator('#vaultItems .vault-item').filter({ hasText: 'Note from the PC' });
    await entry.waitFor();
    await entry.locator('[data-action="copy"]').click();
    await page.waitForFunction(() => document.getElementById('vaultStatus').textContent.includes('Copied'));
    await page.reload();
    await page.getByRole('tab', { name: 'Vault', exact: true }).click();
    await page.locator('#vaultItems .vault-item').filter({ hasText: 'Note from the PC' }).waitFor();
    const history = JSON.parse(await fs.readFile(path.join(directory, 'history.json'), 'utf8'));
    assert.equal(history.find(item => item.TextContent === 'Note from the PC').Read, true);
  } finally { await page.close(); }
});

test('real Vault returns phone files byte-for-byte after reload and renders authenticated image previews', async () => {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  const imageBytes = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1kAAAAASUVORK5CYII=', 'base64');
  const original = Buffer.from([0, 1, 2, 255, 13, 10, 128]);
  try {
    await page.goto(`${origin}/app/?token=browser-test-token`);
    await page.getByRole('tab', { name: 'Send', exact: true }).click();
    await page.locator('#fileInput').setInputFiles([
      { name: 'vault-roundtrip.bin', mimeType: 'application/octet-stream', buffer: original },
      { name: 'vault-preview.png', mimeType: 'image/png', buffer: imageBytes }
    ]);
    await page.waitForFunction(() => document.querySelectorAll('#uploadList [data-state="sent"]').length === 2);
    await page.reload();
    await page.getByRole('tab', { name: 'Vault', exact: true }).click();
    const entry = page.locator('#vaultItems .vault-item').filter({ hasText: 'vault-roundtrip.bin' });
    await entry.locator('[data-action="download"]').click();
    const event = page.waitForEvent('download');
    await entry.getByRole('link', { name: 'Save vault-roundtrip.bin' }).click();
    const download = await event;
    assert.deepEqual(await fs.readFile(await download.path()), original);
    await page.waitForFunction(() => [...document.querySelectorAll('#vaultItems .vault-item')].find(item => item.textContent.includes('vault-preview.png'))?.querySelector('img')?.naturalWidth > 0);
    const history = JSON.parse(await fs.readFile(path.join(directory, 'history.json'), 'utf8'));
    assert.equal(history.find(item => item.Name === 'vault-roundtrip.bin').Read, false, 'Vault access does not acknowledge phone-origin files');
    await page.getByRole('button', { name: 'Refresh vault' }).click();
    await entry.waitFor();
    for (const width of [320, 390, 1440]) {
      await page.setViewportSize({ width, height: 1000 });
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Vault fits ' + width);
      if (process.env.BEAM_SCREENSHOTS) {
        await fs.mkdir(process.env.BEAM_SCREENSHOTS, { recursive: true });
        await page.screenshot({ path: path.join(process.env.BEAM_SCREENSHOTS, `beam-vault-${width}.png`), fullPage: true });
      }
    }
  } finally { await page.close(); }
});

test('real retry protocol returns one durable file receipt for different multipart boundaries', async () => {
  const key = 'a'.repeat(32);
  const send = () => {
    const form = new FormData();
    form.append('files', new Blob([Buffer.from([0, 255, 42])], { type: 'application/octet-stream' }), 'protocol-retry.bin');
    return fetch(`${origin}/receive`, { method: 'POST', headers: { 'X-Beam-Token': 'browser-test-token', 'X-Beam-Transfer-Id': key }, body: form });
  };
  const first = await send();
  assert.equal(first.status, 200);
  const original = await first.json();
  const repeated = await send();
  assert.equal(repeated.status, 200);
  assert.deepEqual(await repeated.json(), original);
  const receipt = await fetch(`${origin}/receive/receipts/${key}`, { headers: { 'X-Beam-Token': 'browser-test-token' } });
  assert.equal(receipt.status, 200);
  assert.deepEqual(await receipt.json(), original);
  const rows = JSON.parse(await fs.readFile(path.join(directory, 'history.json'), 'utf8')).filter(item => item.Name === 'protocol-retry.bin');
  assert.equal(rows.length, 1);
  assert.deepEqual(await fs.readFile(rows[0].FilePath), Buffer.from([0, 255, 42]));
});

for (const kind of ['file', 'note']) test(`real ${kind} saved before a dropped response is confirmed by Retry without another upload`, async () => {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  const name = kind === 'file' ? 'lost-response.bin' : 'Lost response note';
  const bytes = Buffer.from([0, 7, 255, 128, 13, 10]);
  let posts = 0, transferId, receiptId;
  const savedResponses = [];
  // Forward native multipart bytes unchanged; WebKit's route.fetch omits file bytes.
  const proxy = http.createServer((request, response) => {
    const receiving = request.method === 'POST' && request.url === '/receive';
    if (receiving) { posts++; transferId = request.headers['x-beam-transfer-id']; }
    const upstream = http.request(new URL(request.url, origin), { method: request.method, headers: request.headers }, incoming => {
      if (!receiving) {
        response.writeHead(incoming.statusCode, incoming.headers);
        incoming.pipe(response);
        return;
      }
      const chunks = [];
      incoming.on('data', chunk => chunks.push(chunk));
      incoming.on('end', () => {
        savedResponses.push({ status: incoming.statusCode, body: Buffer.concat(chunks).toString('utf8') });
        response.destroy();
      });
    });
    upstream.on('error', () => response.destroy());
    request.pipe(upstream);
  });
  try {
    await new Promise(resolve => proxy.listen(0, '127.0.0.1', resolve));
    const proxyOrigin = `http://127.0.0.1:${proxy.address().port}`;
    const beforeFiles = (await fs.readdir(path.join(directory, 'Inbox'))).length;
    await page.goto(`${proxyOrigin}/app/?token=browser-test-token`);
    await page.getByRole('tab', { name: 'Send', exact: true }).click();
    if (kind === 'file') await page.locator('#fileInput').setInputFiles({ name, mimeType: 'application/octet-stream', buffer: bytes });
    else {
      await page.getByLabel('Note or link').fill(name);
      await page.getByRole('button', { name: 'Send note', exact: true }).click();
    }
    const row = page.locator('#uploadList > li').filter({ hasText: name });
    await page.waitForFunction(() => document.querySelector('#uploadList [data-state="failed"]'));
    // Chromium can transparently replay a POST after a socket reset. Those
    // attempts must also share one receipt, before the app offers explicit Retry.
    assert.ok(savedResponses.length >= 1);
    receiptId = JSON.parse(savedResponses[0].body).ids[0];
    for (const response of savedResponses) {
      assert.equal(response.status, 200);
      assert.deepEqual(JSON.parse(response.body), { ids: [receiptId] });
    }
    const initialPosts = posts;
    await page.evaluate(() => loadCapabilities());
    assert.equal(posts, initialPosts, 'application reconnection does not replay an uncertain upload');
    await row.getByRole('button', { name: 'Retry ' + name, exact: true }).click();
    await page.waitForFunction(() => document.querySelector('#uploadList [data-state="sent"]'));
    assert.equal(posts, initialPosts, 'known receipt confirms without sending file/note bytes again');
    const history = JSON.parse(await fs.readFile(path.join(directory, 'history.json'), 'utf8'));
    const rows = history.filter(item => item.TransferId === transferId);
    assert.equal(rows.length, 1);
    assert.equal(rows[0].Id, receiptId);
    if (kind === 'file') assert.deepEqual(await fs.readFile(rows[0].FilePath), bytes);
    else assert.equal(rows[0].TextContent, name);
    assert.equal((await fs.readdir(path.join(directory, 'Inbox'))).length - beforeFiles, kind === 'file' ? 1 : 0);
    await page.getByRole('tab', { name: 'Vault', exact: true }).click();
    await page.locator('#vaultItems .vault-item').filter({ hasText: name }).waitFor();
  } finally { await page.close(); await new Promise(resolve => proxy.close(resolve)); }
});

test('real unknown receipt retries the same ID and a fresh deliberate send remains separate', async () => {
  const page = await browser.newPage();
  const name = 'not-yet-received.txt';
  const keys = [];
  try {
    await page.route('**/receive', route => {
      keys.push(route.request().headers()['x-beam-transfer-id']);
      return keys.length === 1 ? route.abort('failed') : route.continue();
    });
    await page.goto(`${origin}/app/?token=browser-test-token`);
    await page.getByRole('tab', { name: 'Send', exact: true }).click();
    const payload = { name, mimeType: 'text/plain', buffer: Buffer.from('one original payload') };
    await page.locator('#fileInput').setInputFiles(payload);
    await page.waitForFunction(() => document.querySelector('#uploadList [data-state="failed"]'));
    await page.evaluate(() => loadCapabilities());
    await page.getByRole('button', { name: 'Retry ' + name, exact: true }).click();
    await page.waitForFunction(() => document.querySelectorAll('#uploadList [data-state="sent"]').length === 1);
    assert.equal(keys.length, 2);
    assert.equal(keys[0], keys[1]);
    await page.locator('#fileInput').setInputFiles(payload);
    await page.waitForFunction(() => document.querySelectorAll('#uploadList [data-state="sent"]').length === 2);
    assert.equal(keys.length, 3);
    assert.notEqual(keys[1], keys[2]);
    const rows = JSON.parse(await fs.readFile(path.join(directory, 'history.json'), 'utf8')).filter(item => item.Name === name);
    assert.equal(rows.length, 2);
    assert.equal(new Set(rows.map(item => item.TransferId)).size, 2);
    assert.equal(new Set(rows.map(item => item.FilePath)).size, 2);
    for (const item of rows) assert.equal(await fs.readFile(item.FilePath, 'utf8'), 'one original payload');
  } finally { await page.close(); }
});

test('real pairing reset revokes old pages and prepared Vault content, then explicitly re-pairs without losing history', async () => {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  let oldQrPage;
  try {
    await page.goto(`${origin}/app/?token=browser-test-token`);
    await page.waitForFunction(() => document.getElementById('connectionStatus').textContent === 'Connected');
    await page.getByRole('tab', { name: 'Vault', exact: true }).click();
    await page.getByRole('button', { name: 'Download from-pc.txt', exact: true }).click();
    const save = page.getByRole('link', { name: 'Save from-pc.txt', exact: true });
    await save.waitFor();
    const prepared = await save.getAttribute('href');
    const historyBefore = await fs.readFile(path.join(directory, 'history.json'));
    const replacement = await new Promise((resolve, reject) => {
      let output = '';
      const cleanup = () => { clearTimeout(timeout); child.stdout.off('data', onData); child.off('exit', onExit); };
      const onData = chunk => {
        output += chunk;
        const match = output.match(/BEAM_TEST_ROTATED=([A-Za-z0-9_-]+)/);
        if (match) { cleanup(); resolve(match[1]); }
      };
      const onExit = () => { cleanup(); reject(new Error('Fixture exited before pairing reset completed.')); };
      const timeout = setTimeout(() => { cleanup(); reject(new Error('Fixture pairing reset timed out.')); }, 5000);
      child.stdout.on('data', onData); child.once('exit', onExit);
      child.stdin.write('rotate\n');
    });
    assert.notEqual(replacement, 'browser-test-token');
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.locator('#setup').waitFor({ state: 'visible' });
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), null);
    assert.equal(await page.locator('#vaultItems').innerText(), '');
    assert.equal(await page.locator('#items').innerText(), '');
    assert.equal(await page.evaluate(async url => { try { await fetch(url); return true; } catch { return false; } }, prepared), false);
    const oldAccess = await fetch(origin + '/vault', { headers: { 'X-Beam-Token': 'browser-test-token' } });
    assert.equal(oldAccess.status, 401);
    oldQrPage = await browser.newPage();
    await oldQrPage.goto(`${origin}/app/?token=browser-test-token`);
    await oldQrPage.locator('#setup').waitFor({ state: 'visible' });
    await page.getByLabel('PC token', { exact: true }).fill(replacement);
    await page.locator('#saveTokenButton').click();
    await page.waitForFunction(() => document.getElementById('connectionStatus').textContent === 'Connected');
    await page.getByRole('tab', { name: 'Vault', exact: true }).click();
    await page.getByRole('button', { name: 'Download from-pc.txt', exact: true }).click();
    await page.getByRole('link', { name: 'Save from-pc.txt', exact: true }).waitFor();
    assert.deepEqual(await fs.readFile(path.join(directory, 'history.json')), historyBefore);
  } finally { await oldQrPage?.close(); await page.close(); }
});
