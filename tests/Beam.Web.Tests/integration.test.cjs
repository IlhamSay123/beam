const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const { chromium, webkit } = require('playwright');
let child, browser, origin, directory;
before(async () => {
  directory = await fs.mkdtemp(path.join(os.tmpdir(), 'beam-browser-'));
  const dll = path.resolve(__dirname, '../Beam.Web.Host/bin/Release/net8.0/Beam.Web.Host.dll');
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
