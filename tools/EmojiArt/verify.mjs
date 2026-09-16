// Actual Chromium file:// workflow, including downloads and offline failure paths.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { readFile, writeFile, mkdir, mkdtemp, cp } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = fileURLToPath(new URL('../../', import.meta.url));
const work = await mkdtemp(join(root, 'artifacts/emoji-art-check-'));
const downloads = join(work, 'downloads'); await mkdir(downloads);
const browserPath = process.env.EOTA_EMOJI_BROWSER ?? [
  'C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/chromium', '/usr/bin/google-chrome'
].find(existsSync);
if (!browserPath) throw new Error('Set EOTA_EMOJI_BROWSER to a local Chrome or Edge executable.');
const browser = spawn(browserPath, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  '--remote-debugging-pipe', `--user-data-dir=${join(work, 'profile')}`, 'about:blank'],
{ windowsHide: true, stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'] });
let sequence = 0, buffer = '', sessionId;
const pending = new Map(), exceptions = [];
function call(method, params = {}, session = sessionId) {
  return new Promise((resolveReply, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`Timeout: ${method}`)); }, 30000);
    pending.set(id, { resolve: resolveReply, reject, timer });
    browser.stdio[3].write(JSON.stringify({ id, method, params, ...(session ? { sessionId: session } : {}) }) + '\0');
  });
}
browser.stdio[4].setEncoding('utf8');
browser.stdio[4].on('data', chunk => {
  buffer += chunk;
  let end;
  while ((end = buffer.indexOf('\0')) >= 0) {
    const message = JSON.parse(buffer.slice(0, end)); buffer = buffer.slice(end + 1);
    if (message.method === 'Runtime.exceptionThrown') exceptions.push(message.params.exceptionDetails);
    const task = pending.get(message.id); if (!task) continue;
    pending.delete(message.id); clearTimeout(task.timer);
    if (message.error) task.reject(new Error(message.error.message)); else task.resolve(message.result);
  }
});
function fail(error) { for (const task of pending.values()) { clearTimeout(task.timer); task.reject(error); } pending.clear(); }
browser.on('error', fail); browser.on('exit', () => fail(new Error('Browser exited.')));
async function evaluate(expression) {
  const result = await call('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description ?? result.exceptionDetails.text);
  return result.result.value;
}
const waitFor = expression => evaluate(`(async () => { for (let i=0;i<250;i++) { if (${expression}) return; await new Promise(r=>setTimeout(r,80)); } throw new Error('UI wait timed out: ' + document.getElementById('status')?.textContent + ' / ' + document.getElementById('cache-status')?.textContent); })()`);
async function state(expected) { await waitFor(`document.getElementById('status')?.dataset.state === ${JSON.stringify(expected)}`); }
async function offline(enabled) { await call('Network.emulateNetworkConditions', { offline: enabled, latency: 0, downloadThroughput: -1, uploadThroughput: -1 }); }
async function set(id, value, event = 'input') { await evaluate(`document.getElementById(${JSON.stringify(id)}).value=${JSON.stringify(value)}; document.getElementById(${JSON.stringify(id)}).dispatchEvent(new Event(${JSON.stringify(event)}));`); }
const click = id => evaluate(`document.getElementById(${JSON.stringify(id)}).click()`);
try {
  const { targetId } = await call('Target.createTarget', { url: 'about:blank' });
  ({ sessionId } = await call('Target.attachToTarget', { targetId, flatten: true }));
  await call('Runtime.enable'); await call('Network.enable'); await call('Page.enable');
  await call('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: downloads });
  await call('Emulation.setDeviceMetricsOverride', { width: 1280, height: 1000, deviceScaleFactor: 1, mobile: false });
  await offline(true);
  await call('Page.navigate', { url: pathToFileURL(join(root, 'tools/EmojiArt/index.html')).href });
  await state('ready');
  assert.equal(await evaluate(`document.querySelector('.configuration').getBoundingClientRect().bottom < document.querySelector('main').getBoundingClientRect().top`), true, 'Configuration import belongs above the editor');
  assert.equal(await evaluate(`document.getElementById('download').disabled`), false);
  for (const [profession, rgb] of [['guardian',[52,75,115]],['arcanist',[97,68,147]],['artisan',[150,114,69]],['hunter',[66,116,81]],['neutral',[101,93,80]]]) {
    await set('profession', profession, 'change'); await state('ready');
    const pixel = await evaluate(`Array.from(document.getElementById('preview').getContext('2d').getImageData(0,0,1,1).data)`);
    assert.ok(rgb.every((value,index)=>Math.abs(pixel[index]-value)<=1), `${profession} background matches the profession palette`);
  }
  await set('profession', 'arcanist', 'change'); await state('ready');
  await set('mode', 'fusion', 'change'); await click('merge'); await state('ready');
  assert.match(await evaluate(`document.getElementById('status').textContent`), /离线缓存/);
  const forward = await evaluate(`document.getElementById('preview').toDataURL()`);
  await set('emoji', '🔋'); await set('emoji2', '🔮'); await click('merge'); await state('ready');
  assert.equal(await evaluate(`document.getElementById('preview').toDataURL()`), forward, 'Swapped pair uses the same cached fusion');
  assert.equal(await evaluate(`emojiFusion.lookup('❤️','🔥').source === emojiFusion.lookup('❤','🔥').source`), true);
  await set('style', 'icon'); await set('size', '1024'); await state('ready');
  assert.equal(await evaluate(`document.getElementById('preview').getContext('2d').getImageData(0,0,1,1).data[3]`), 0);
  await set('name', 'fusion-check'); await click('download');
  for (let i = 0; i < 100 && !existsSync(join(downloads, 'fusion-check.png')); i++) await new Promise(r => setTimeout(r, 50));
  const png = await readFile(join(downloads, 'fusion-check.png'));
  assert.equal(png.subarray(1, 4).toString(), 'PNG'); assert.equal(png.readUInt32BE(16), 1024); assert.equal(png.readUInt32BE(20), 1024);
  await set('emoji', '😀'); await set('emoji2', '🔥'); await click('merge'); await state('error');
  assert.match(await evaluate(`document.getElementById('status').textContent`), /网络/);
  assert.equal(await evaluate(`document.getElementById('download').disabled`), true);
  await set('emoji2', '🇨🇳'); await click('merge'); await state('error');
  assert.match(await evaluate(`document.getElementById('status').textContent`), /不支持/);
  const missing = await evaluate(`(() => { for (const a of emojiFusion.supported) for (const b of emojiFusion.supported) { try { emojiFusion.lookup(a,b); } catch(e) { if(e.message.includes('暂无')) return [a,b]; } } })()`);
  assert.ok(missing, 'Compatibility table has an unsupported pair');
  await set('emoji', missing[0]); await set('emoji2', missing[1]); await click('merge'); await state('error');
  assert.match(await evaluate(`document.getElementById('status').textContent`), /暂无/);
  await set('mode', 'single', 'change'); await set('emoji', '😀🔥'); await state('error');
  assert.equal(await evaluate(`document.getElementById('download').disabled`), true);
  await set('emoji', '🧙‍♂️'); await state('ready');
  // Real file selection: the original source recipes must still load while offline.
  const { root: documentRoot } = await call('DOM.getDocument');
  const { nodeId } = await call('DOM.querySelector', { nodeId: documentRoot.nodeId, selector: '#recipes' });
  await call('DOM.setFileInputFiles', { nodeId, files: [resolve(root, 'Content/Source/Art/emoji-recipes.json')] });
  await waitFor(`!document.getElementById('cards').hidden`);
  await set('cards', 'EOTA-CORE-ARC-MIN-003', 'change'); await state('ready');
  assert.equal(await evaluate(`document.getElementById('profession').value`), 'arcanist');
  assert.equal(await evaluate(`document.getElementById('mode').value`), 'fusion');
  assert.match(await evaluate(`document.getElementById('status').textContent`), /离线缓存/);
  await set('size', '256'); await state('ready');
  assert.equal(await evaluate(`document.getElementById('preview').width`), 256);
  console.log('Offline: cached merge, swapped pair, Unicode variants, PNG download, failures, and recipe import passed.');
  await offline(false);
  await set('emoji', '😀'); await set('emoji2', '🔥'); await click('merge'); await state('ready');
  assert.match(await evaluate(`document.getElementById('status').textContent`), /合并完成/);
  await set('name', 'online-fusion'); await click('download');
  for (let i = 0; i < 100 && !existsSync(join(downloads, 'online-fusion.png')); i++) await new Promise(r => setTimeout(r, 50));
  assert.ok((await readFile(join(downloads, 'online-fusion.png'))).length > 1000);
  assert.equal(await evaluate(`(async()=>!!(await fusionStorage.get(emojiFusion.lookup('😀','🔥').source)))()`), true, 'A new combination is persisted outside page memory');
  // A delayed network image must never overwrite a newer single-emoji preview.
  await evaluate(`globalThis.originalFusionLoad=emojiFusion.load; emojiFusion.load=async (...args)=>{await new Promise(r=>setTimeout(r,300)); return originalFusionLoad(...args);};`);
  await click('merge'); await set('mode', 'single', 'change'); await set('emoji', '🦏'); await state('ready');
  const latest = await evaluate(`document.getElementById('preview').toDataURL()`);
  await new Promise(r => setTimeout(r, 450));
  assert.equal(await evaluate(`document.getElementById('preview').toDataURL()`), latest, 'Stale fusion does not overwrite newer input');
  await evaluate(`emojiFusion.load=originalFusionLoad`);
  await set('mode', 'fusion', 'change'); await set('emoji', '😀'); await set('emoji2', '🔥'); await click('merge'); await state('ready');
  await set('size', '512'); await state('ready');
  const screenshot = await call('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  await writeFile(join(work, 'fusion-tool.png'), Buffer.from(screenshot.data, 'base64'));
  await offline(true);
  await call('Page.reload'); await state('ready');
  await set('mode', 'fusion', 'change'); await set('emoji', '😀'); await set('emoji2', '🔥'); await click('merge'); await state('ready');
  assert.match(await evaluate(`document.getElementById('status').textContent`), /本机浏览器/);
  await click('export-cache');
  const cacheFile = join(downloads, 'emoji-fusion-cache.json');
  for (let i = 0; i < 100 && !existsSync(cacheFile); i++) await new Promise(r => setTimeout(r, 50));
  const bundle = JSON.parse(await readFile(cacheFile, 'utf8'));
  assert.equal(bundle.format, 'eota-emoji-fusion-cache'); assert.equal(bundle.images.length, 1);
  async function freshPage(page) {
    const { browserContextId } = await call('Target.createBrowserContext', {}, null);
    const { targetId: freshTarget } = await call('Target.createTarget', { url: 'about:blank', browserContextId }, null);
    ({ sessionId } = await call('Target.attachToTarget', { targetId: freshTarget, flatten: true }, null));
    await call('Runtime.enable'); await call('Network.enable'); await call('Page.enable'); await offline(true);
    await call('Page.navigate', { url: pathToFileURL(page).href }); await state('ready');
  }
  // Fresh browser storage: only importing the portable image bundle enables offline use.
  await freshPage(join(root, 'tools/EmojiArt/index.html'));
  await set('mode', 'fusion', 'change'); await set('emoji', '😀'); await set('emoji2', '🔥'); await click('merge'); await state('error');
  assert.equal(await evaluate(`fusionStorage.count()`), 0);
  const { root: importDocument } = await call('DOM.getDocument');
  const { nodeId: cacheInput } = await call('DOM.querySelector', { nodeId: importDocument.nodeId, selector: '#import-cache' });
  await call('DOM.setFileInputFiles', { nodeId: cacheInput, files: [cacheFile] });
  await waitFor(`document.getElementById('cache-status').textContent.includes('已导入 1')`);
  await click('merge'); await state('ready');
  assert.equal(await evaluate(`document.getElementById('download').disabled`), false);
  assert.equal(await evaluate(`(async()=>{ try {await fusionStorage.importBundle({format:'eota-emoji-fusion-cache',version:1,images:[{emoji:'😀🔥',source:'http://invalid',dataUrl:'invalid'}]}); return false;} catch {return (await fusionStorage.count())===1;} })()`), true, 'Invalid import does not replace saved entries');
  // Rebuild a distributable copy, with no dependency on the original source tree at runtime.
  const copy = join(work, 'repository-download', 'EmojiArt'), shared = join(work, 'shared-cache');
  await cp(join(root, 'tools/EmojiArt'), copy, { recursive: true }); await mkdir(shared);
  await writeFile(join(shared, 'new-combination.json'), JSON.stringify(bundle));
  const build = spawnSync(process.execPath, [join(root, 'tools/EmojiArt/build-fusion-cache.mjs'), '--shared-dir', shared, '--output', join(copy, 'fusion-cache.js')], { windowsHide: true, encoding: 'utf8' });
  assert.equal(build.status, 0, build.stderr);
  await freshPage(join(copy, 'index.html'));
  assert.equal(await evaluate(`fusionStorage.count()`), 0);
  await set('mode', 'fusion', 'change'); await set('emoji', '😀'); await set('emoji2', '🔥'); await click('merge'); await state('ready');
  assert.match(await evaluate(`document.getElementById('status').textContent`), /仓库自带/);
  assert.equal(await evaluate(`document.getElementById('download').disabled`), false);
  assert.deepEqual(exceptions, []);
  console.log(`Online merge, PNG download, async switching, persistence, cache transfer and a fresh offline repository copy passed. Evidence: ${work}`);
} finally {
  browser.kill();
  await new Promise(done => { if (browser.exitCode !== null) done(); else { browser.once('exit', done); setTimeout(done, 3000); } });
}
