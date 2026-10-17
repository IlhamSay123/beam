const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const { chromium, webkit } = require('playwright');

let server, browser, origin;
const root = path.resolve(__dirname, '../../src/Beam.Server/wwwroot');
before(async () => {
  server = http.createServer(async (req, res) => {
    if (req.url === '/connection/heartbeat') return res.writeHead(204).end();
    const filename = new URL(req.url, 'http://localhost').pathname.split('/').pop() || 'index.html';
    if (!['index.html', 'app.js', 'motion.js', 'vault.js', 'lucide.min.js', 'styles.css'].includes(filename)) return res.writeHead(404).end();
    try {
      const body = await fs.readFile(path.join(root, filename));
      res.writeHead(200, { 'Content-Type': filename.endsWith('.js') ? 'text/javascript' : filename.endsWith('.css') ? 'text/css' : 'text/html' }).end(body);
    } catch { res.writeHead(404).end(); }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
  browser = process.env.BEAM_ENGINE === 'webkit' ? await webkit.launch() : await chromium.launch({ channel: process.env.BEAM_BROWSER || 'msedge' });
});
after(async () => { await browser?.close(); if (server) await new Promise(resolve => server.close(resolve)); });
const note = { id: 'note', name: 'Text', isText: true, textContent: 'A retained message', direction: 'outbound', read: true, available: true, timestampUtc: '2026-10-16T12:00:00Z', sizeBytes: 18, contentType: 'text/plain' };
const file = { id: 'phone-file', name: 'from-phone.txt', isText: false, direction: 'inbound', read: false, available: true, timestampUtc: '2026-10-16T11:00:00Z', sizeBytes: 10, contentType: 'text/plain' };

async function open(items = [note, file], options = {}) {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  page.setDefaultTimeout(5000);
  const requests = [], acknowledgements = [];
  await page.route('**/outbox', route => route.fulfill({ json: options.pending || [] }));
  await page.route('**/capabilities', route => route.fulfill({ json: { maxUploadBytes: 104857600, maxTextBytes: 65536, safeRetryVersion: 1 } }));
  await page.route('**/vault?*', route => {
    requests.push(route.request());
    return options.vault ? options.vault(route) : route.fulfill({ json: { items, total: items.length, hasMore: false } });
  });
  await page.route('**/vault/*/content', route => route.fulfill({ body: 'phone file', contentType: 'text/plain' }));
  await page.route('**/outbox/*/ack', route => { acknowledgements.push(route.request().url()); return route.fulfill({ status: 200 }); });
  await page.goto(`${origin}/app/?token=test-token`);
  await page.getByRole('tab', { name: 'Vault', exact: true }).click();
  return { page, requests, acknowledgements };
}

test('phone Vault shows both directions and keeps copied text after refresh', async () => {
  const { page } = await open();
  try {
    await page.locator('#vaultItems [data-id="note"]').waitFor();
    assert.match(await page.locator('#vaultItems').innerText(), /Received from PC/);
    assert.match(await page.locator('#vaultItems').innerText(), /Sent to PC/);
    await page.locator('#vaultItems [data-id="note"] button[data-action="copy"]').click();
    await page.waitForFunction(() => document.getElementById('vaultStatus').textContent.includes('Copied'));
    await page.reload();
    await page.getByRole('tab', { name: 'Vault', exact: true }).click();
    await page.locator('#vaultItems [data-id="note"]').waitFor();
    assert.equal(await page.locator('#vaultItems [data-id="note"]').count(), 1);
  } finally { await page.close(); }
});

test('phone-origin files can be downloaded again without acknowledgement', async () => {
  const { page, acknowledgements } = await open();
  try {
    await page.locator('#vaultItems [data-id="phone-file"] button[data-action="download"]').click();
    const link = page.locator('#vaultItems [data-id="phone-file"] a');
    await link.waitFor();
    const downloadEvent = page.waitForEvent('download');
    await link.click();
    const download = await downloadEvent;
    assert.equal(download.suggestedFilename(), 'from-phone.txt');
    assert.equal(await fs.readFile(await download.path(), 'utf8'), 'phone file');
    assert.deepEqual(acknowledgements, []);
    await page.getByRole('button', { name: 'Refresh vault' }).click();
    assert.equal(await page.locator('#vaultItems [data-id="phone-file"]').count(), 1);
  } finally { await page.close(); }
});

test('Vault search, direction, type and pagination use the bounded API', async () => {
  const { page, requests } = await open([note], { vault: route => route.fulfill({ json: { items: [note], total: 42, hasMore: true } }) });
  try {
    await page.locator('#vaultItems [data-id="note"]').waitFor();
    await page.getByLabel('Search vault').fill('project');
    await page.getByLabel('Vault direction').selectOption('inbound');
    await page.getByLabel('Vault file type').selectOption('image');
    await page.waitForFunction(() => !document.getElementById('vaultRefresh').disabled);
    await page.getByRole('button', { name: 'Load more' }).click();
    await page.waitForTimeout(350);
    const query = new URL(requests.at(-1).url()).searchParams;
    assert.equal(query.get('search'), 'project');
    assert.equal(query.get('direction'), 'inbound');
    assert.equal(query.get('kind'), 'image');
    assert.ok(Number(query.get('limit')) <= 100);
    assert.ok(Number(query.get('offset')) > 0);
    assert.equal(await page.locator('#vaultItems [data-id="note"]').count(), 1, 'Duplicate IDs are not duplicated on append');
  } finally { await page.close(); }
});

test('missing Vault files remain visible without an active download control', async () => {
  const { page } = await open([{ ...file, available: false }]);
  try {
    await page.getByText('File missing', { exact: true }).waitFor();
    assert.equal(await page.locator('#vaultItems button[data-action="download"]').isEnabled(), false);
  } finally { await page.close(); }
});

test('a file removed from the PC becomes visibly missing after refreshing', async () => {
  const { page } = await open([file]);
  try {
    await page.locator('#vaultItems [data-id="phone-file"]').waitFor();
    await page.route('**/vault?*', route => route.fulfill({ json: { items: [{ ...file, available: false }], total: 1, hasMore: false } }));
    await page.getByRole('button', { name: 'Refresh vault' }).click();
    await page.getByText('File missing', { exact: true }).waitFor();
    assert.equal(await page.locator('#vaultItems button[data-action="download"]').isEnabled(), false);
  } finally { await page.close(); }
});

test('image previews use authenticated blobs and active image formats are not rendered', async () => {
  const image = { ...file, id: 'photo', name: 'photo.png', contentType: 'image/png', sizeBytes: 68 };
  const { page } = await open([image, { ...image, id: 'active', name: 'active.svg', contentType: 'image/svg+xml' }]);
  let previewToken;
  try {
    await page.route('**/vault/photo/preview', route => {
      previewToken = route.request().headers()['x-beam-token'];
      return route.fulfill({ contentType: 'image/png', body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1kAAAAASUVORK5CYII=', 'base64') });
    });
    await page.getByRole('button', { name: 'Refresh vault' }).click();
    await page.waitForFunction(() => document.querySelector('#vaultItems [data-id="photo"] img')?.naturalWidth > 0);
    assert.equal(previewToken, 'test-token');
    assert.match(await page.locator('#vaultItems [data-id="photo"] img').getAttribute('src'), /^blob:/);
    assert.equal(await page.locator('#vaultItems [data-id="active"] img').count(), 0);
  } finally { await page.close(); }
});

test('confirmed arrivals animate once rather than replaying on every metadata poll', async () => {
  const { page } = await open();
  try {
    await page.getByRole('tab', { name: 'Receive', exact: true }).click();
    await page.route('**/outbox', route => route.fulfill({ json: [{ ...note, id: 'new-message', read: false }] }));
    await page.evaluate(() => loadOutbox());
    await page.waitForFunction(() => document.getElementById('motionPanel').dataset.phase === 'arrived');
    assert.ok(await page.locator('.motion-packet').evaluate(el => el.getAnimations().some(animation => animation.playState === 'running')));
    await page.waitForTimeout(800);
    await page.evaluate(() => loadOutbox());
    assert.equal(await page.locator('.motion-packet').evaluate(el => el.getAnimations().filter(animation => animation.playState === 'running').length), 0);
  } finally { await page.close(); }
});

test('Vault failures retain existing items and give a retry action', async () => {
  const { page } = await open();
  try {
    await page.locator('#vaultItems [data-id="note"]').waitFor();
    await page.route('**/vault?*', route => route.abort());
    await page.getByRole('button', { name: 'Refresh vault' }).click();
    await page.waitForFunction(() => document.getElementById('vaultStatus').textContent.includes('unavailable'));
    assert.equal(await page.locator('#vaultItems [data-id="note"]').count(), 1);
    assert.equal(await page.getByRole('button', { name: 'Refresh vault' }).isEnabled(), true);
  } finally { await page.close(); }
});

test('silent Vault refresh preserves the manual-copy fallback selection', async () => {
  const { page } = await open();
  try {
    await page.evaluate(() => {
      Object.defineProperty(navigator, 'clipboard', { value: undefined });
      document.execCommand = () => false;
    });
    await page.locator('#vaultItems [data-id="note"] button[data-action="copy"]').click();
    const field = page.locator('#vaultItems textarea');
    await field.waitFor();
    assert.equal(await field.evaluate(el => document.activeElement === el), true);
    await page.evaluate(() => window.BeamVault.refresh(false, true));
    assert.equal(await field.evaluate(el => document.activeElement === el && el.selectionStart === 0 && el.selectionEnd === el.value.length), true);
  } finally { await page.close(); }
});

test('background refresh retains loaded pages and their manual-copy selection', async () => {
  const bank = Array.from({ length: 45 }, (_, index) => ({ ...note, id: 'note-' + index, textContent: 'Retained note ' + index }));
  const { page } = await open([], { vault: route => {
    const query = new URL(route.request().url()).searchParams;
    const offset = Number(query.get('offset')), limit = Number(query.get('limit'));
    return route.fulfill({ json: { items: bank.slice(offset, offset + limit), total: bank.length, hasMore: offset + limit < bank.length } });
  } });
  try {
    await page.getByRole('button', { name: 'Load more' }).click();
    await page.waitForFunction(() => document.querySelectorAll('#vaultItems > li').length === 45);
    await page.evaluate(() => { Object.defineProperty(navigator, 'clipboard', { value: undefined }); document.execCommand = () => false; });
    await page.locator('#vaultItems [data-id="note-44"] [data-action="copy"]').click();
    bank.unshift({ ...note, id: 'new-note', textContent: 'Newly arrived note' });
    await page.evaluate(() => window.BeamVault.refresh(false, true));
    assert.equal(await page.locator('#vaultItems > li').count(), 46);
    const field = page.locator('#vaultItems [data-id="note-44"] textarea');
    assert.equal(await field.evaluate(el => document.activeElement === el && el.selectionEnd === el.value.length), true);
  } finally { await page.close(); }
});

test('saved Vault downloads release their blobs after the native download grace period', async () => {
  const { page } = await open([file]);
  try {
    await page.clock.install();
    await page.locator('#vaultItems [data-action="download"]').click();
    const link = page.locator('#vaultItems a');
    await link.waitFor();
    const event = page.waitForEvent('download');
    await link.click();
    const download = await event;
    assert.equal(await fs.readFile(await download.path(), 'utf8'), 'phone file');
    await page.waitForFunction(() => document.getElementById('vaultStatus').textContent.includes('Download started'));
    await page.clock.fastForward(60001);
    assert.equal(await page.locator('#vaultItems a').count(), 0);
    assert.equal(await page.locator('#vaultItems [data-action="download"]').isEnabled(), true);
    assert.equal(await page.locator('#vaultItems > li').count(), 1);
  } finally { await page.close(); }
});

test('filtering away a preparation cannot announce a nonexistent Save action', async () => {
  let release, started;
  const pending = new Promise(resolve => { release = resolve; });
  const requested = new Promise(resolve => { started = resolve; });
  const { page } = await open([file], { vault: route => {
    const filtered = new URL(route.request().url()).searchParams.get('search');
    return route.fulfill({ json: { items: filtered ? [] : [file], total: filtered ? 0 : 1, hasMore: false } });
  } });
  try {
    await page.route('**/vault/*/content', async route => { started(); await pending; return route.fulfill({ body: 'phone file' }).catch(() => {}); });
    await page.locator('#vaultItems [data-action="download"]').click();
    await requested;
    await page.getByLabel('Search vault').fill('different');
    await page.waitForFunction(() => document.querySelectorAll('#vaultItems > li').length === 0);
    release();
    await page.waitForTimeout(150);
    assert.notEqual(await page.locator('#motionPanel').getAttribute('data-phase'), 'ready');
    assert.equal(await page.locator('#vaultItems a').count(), 0);
  } finally { release(); await page.close(); }
});

test('old Vault responses cannot reveal data after pairing changes', async () => {
  let release, started;
  const pending = new Promise(resolve => { release = resolve; });
  const requested = new Promise(resolve => { started = resolve; });
  const { page } = await open([], { vault: async route => { started(); await pending; return route.fulfill({ json: { items: [note], total: 1, hasMore: false } }).catch(() => {}); } });
  try {
    await requested;
    await page.evaluate(() => localStorage.setItem('beam-token', 'new-token'));
    release();
    await page.waitForTimeout(200);
    assert.equal(await page.locator('#vaultItems [data-id="note"]').count(), 0);
    assert.equal(await page.evaluate(() => localStorage.getItem('beam-token')), 'new-token');
  } finally { release(); await page.close(); }
});

test('real motion track bounds contain all trails in both directions and reduced motion', async () => {
  const { page } = await open();
  try {
    for (const width of [320, 390, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      for (const direction of ['incoming', 'outgoing']) for (const progress of [0, .01, .11, .5, 1]) {
        await page.evaluate(({ direction, progress }) => window.BeamMotion.update({ id: 'geometry', direction, item: { name: 'sample.png', contentType: 'image/png' }, phase: 'uploading', progress }), { direction, progress });
        const contained = await page.locator('.motion-track').evaluate(track => {
          const packet = track.querySelector('.motion-packet');
          const before = getComputedStyle(packet, '::before');
          const bounds = packet.getBoundingClientRect();
          const rail = track.getBoundingClientRect();
          const left = bounds.left + packet.clientLeft + parseFloat(before.left);
          const right = left + parseFloat(before.width);
          return parseFloat(before.width) === 0 || (left >= rail.left - 1 && right <= rail.right + 1);
        });
        assert.ok(contained, `${width} ${direction} ${progress}`);
      }
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    }
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.evaluate(() => window.BeamMotion.update({ id: 'unknown', direction: 'incoming', item: { isText: true, name: 'Note' }, phase: 'preparing', progress: null }));
    await page.waitForFunction(() => matchMedia('(prefers-reduced-motion: reduce)').matches &&
      document.querySelector('.motion-packet').getAnimations().every(animation => animation.playState !== 'running'));
    assert.equal(await page.locator('.motion-packet').evaluate(el => el.getAnimations().filter(a => a.playState === 'running').length), 0);
    assert.match(await page.locator('#motionName').innerText(), /Note/);
  } finally { await page.close(); }
});
