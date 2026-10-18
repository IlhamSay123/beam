const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { once } = require('node:events');

const root = path.resolve(__dirname, '..');
const { webkit } = require(path.join(root, 'tests/Beam.Web.Tests/node_modules/playwright'));
const output = path.resolve(process.argv[2] || path.join(root, 'artifacts/screenshots/web'));

async function main() {
  await fs.mkdir(output, { recursive: true });
  const dll = path.join(root, 'tests/Beam.Web.Host/bin/Release/net10.0/Beam.Web.Host.dll');
  await fs.access(dll);
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'beam-docs-'));
  let child, browser;
  try {
    child = spawn('dotnet', [dll, directory], { stdio: ['pipe', 'pipe', 'pipe'] });
    const origin = await new Promise((resolve, reject) => {
      let logs = '';
      const timeout = setTimeout(() => reject(new Error('Demo server did not start.')), 20000);
      child.stdout.on('data', chunk => {
        logs += chunk;
        const match = logs.match(/BEAM_TEST_URL=(http:\/\/[^\s]+)/);
        if (match) { clearTimeout(timeout); resolve(match[1]); }
      });
      child.stderr.on('data', () => {});
      child.once('error', error => { clearTimeout(timeout); reject(error); });
      child.once('exit', code => { clearTimeout(timeout); reject(new Error('Demo server exited: ' + code)); });
    });
    browser = await webkit.launch();
    const page = await browser.newPage({
      viewport: { width: 390, height: 900 }, deviceScaleFactor: 2,
      isMobile: true, hasTouch: true, reducedMotion: 'reduce'
    });
    page.setDefaultTimeout(10000);
    await page.goto(origin + '/app/?token=browser-test-token');
    await page.getByRole('tab', { name: 'Send', exact: true }).click();
    await page.getByLabel('Choose files', { exact: true }).waitFor({ state: 'attached' });
    await page.waitForFunction(() => !document.querySelector('#fileInput').disabled);
    const image = await page.evaluate(() => {
      const canvas = document.createElement('canvas');
      canvas.width = 640; canvas.height = 400;
      const drawing = canvas.getContext('2d');
      drawing.fillStyle = '#e2ecee'; drawing.fillRect(0, 0, 640, 400);
      drawing.fillStyle = '#1d2c2e'; drawing.fillRect(60, 55, 520, 290);
      drawing.fillStyle = '#78ddd0'; drawing.fillRect(150, 95, 45, 210); drawing.fillRect(275, 130, 45, 140);
      drawing.fillStyle = '#f4ce6a'; drawing.fillRect(400, 165, 45, 70);
      return canvas.toDataURL('image/png').split(',')[1];
    });
    await page.locator('#fileInput').setInputFiles([
      { name: 'Beam study.png', mimeType: 'image/png', buffer: Buffer.from(image, 'base64') },
      { name: 'Weekend checklist.txt', mimeType: 'text/plain', buffer: Buffer.from('Camera\nCharger\nNotebook\n') }
    ]);
    await page.waitForFunction(() => document.querySelectorAll('#uploadList [data-state="sent"]').length === 2);
    await page.getByLabel('Note or link').fill('An idea from my iPhone.\nBack on the PC, ready for later.');
    await page.getByRole('button', { name: 'Send note', exact: true }).click();
    await page.waitForFunction(() => document.querySelectorAll('#uploadList [data-state="sent"]').length === 3);
    assert.equal(new URL(page.url()).search, '', 'The pairing token must be removed from the visible URL.');
    await page.screenshot({ path: path.join(output, 'beam-phone-send.png') });

    await page.getByRole('tab', { name: 'Receive', exact: true }).click();
    await page.getByRole('button', { name: 'Download from-pc.txt' }).click();
    await page.getByRole('link', { name: 'Save from-pc.txt' }).waitFor();
    await page.screenshot({ path: path.join(output, 'beam-phone-receive.png') });

    await page.getByRole('tab', { name: 'Vault', exact: true }).click();
    await page.locator('#vaultItems .vault-item').filter({ hasText: 'Beam study.png' }).waitFor();
    await page.waitForFunction(() => [...document.querySelectorAll('#vaultItems img')].some(image => image.naturalWidth > 0));
    await page.screenshot({ path: path.join(output, 'beam-phone-vault.png') });
    const history = JSON.parse(await fs.readFile(path.join(directory, 'history.json'), 'utf8'));
    assert.equal(history.length, 5, 'Expected only the five generated demo items.');
    await fs.writeFile(path.join(output, 'capture-info.json'), JSON.stringify({
      capturedUtc: new Date().toISOString(), engine: 'WebKit',
      viewport: { width: 390, height: 900 }, deviceScaleFactor: 2,
      data: 'Generated fixture files and notes; isolated real Beam server on loopback.',
      screenshots: ['beam-phone-send.png', 'beam-phone-receive.png', 'beam-phone-vault.png']
    }, null, 2));
    console.log('PASS: three current browser UI screenshots captured using WebKit and an isolated real Beam server.');
  } finally {
    await browser?.close();
    if (child && child.exitCode === null) {
      const exited = once(child, 'exit');
      child.stdin.end('stop\n');
      const timeout = setTimeout(() => child.kill(), 5000);
      try { await exited; } finally { clearTimeout(timeout); }
    }
    const resolved = path.resolve(directory);
    assert.equal(path.basename(resolved).startsWith('beam-docs-'), true);
    assert.equal(path.dirname(resolved), path.resolve(os.tmpdir()));
    await fs.rm(resolved, { recursive: true, force: true });
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });