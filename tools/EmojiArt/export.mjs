// Node 22+ and a locally installed Chrome/Edge. The browser runs hidden and is closed afterwards.
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { readFile, writeFile, mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, dirname, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const [recipesPath, outputPath, browserArg] = process.argv.slice(2);
if (!recipesPath || !outputPath) { console.error('Usage: node tools/EmojiArt/export.mjs <recipes.json> <output-dir> [browser-path]'); process.exit(1); }
const browserPath = browserArg ?? [process.env.EOTA_EMOJI_BROWSER,
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/chromium', '/usr/bin/google-chrome', '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
].find(p => p && existsSync(p));
if (!browserPath) throw new Error('No local Chrome/Edge found. Pass its path as the third argument.');
const recipes = JSON.parse(await readFile(recipesPath, 'utf8'));
const entries = Object.entries(recipes).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0);
if (!entries.length || entries.some(([id]) => !/^[A-Za-z0-9_-]+$/.test(id))) throw new Error('Empty recipes or unsafe card ID.');
const output = resolve(outputPath); await mkdir(output, { recursive: true });
const profile = await mkdtemp(join(tmpdir(), 'eota-emoji-'));
const browser = spawn(browserPath, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  '--remote-debugging-pipe', `--user-data-dir=${profile}`, 'about:blank'], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe', 'pipe', 'pipe'] });
const pending = new Map(); let counter = 0, buffer = '';
function call(method, params = {}, sessionId) {
  return new Promise((resolveReply, reject) => {
    const id = ++counter;
    const timeout = setTimeout(() => { pending.delete(id); reject(new Error(`Browser timeout: ${method}`)); }, 15000);
    pending.set(id, { resolve: resolveReply, reject, timeout });
    browser.stdio[3].write(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }) + '\0');
  });
}
browser.stdio[4].setEncoding('utf8');
browser.stdio[4].on('data', chunk => {
  buffer += chunk;
  let end;
  while ((end = buffer.indexOf('\0')) >= 0) {
    const message = JSON.parse(buffer.slice(0, end)); buffer = buffer.slice(end + 1);
    const task = pending.get(message.id); if (!task) continue;
    clearTimeout(task.timeout); pending.delete(message.id);
    if (message.error) task.reject(new Error(message.error.message)); else task.resolve(message.result);
  }
});
function fail(error) { for (const task of pending.values()) { clearTimeout(task.timeout); task.reject(error); } pending.clear(); }
browser.on('error', fail); browser.on('exit', () => fail(new Error('Browser exited.')));
browser.stderr.on('data', () => {});
try {
  const { targetId } = await call('Target.createTarget', { url: pathToFileURL(join(dirname(fileURLToPath(import.meta.url)), 'index.html')).href });
  const { sessionId } = await call('Target.attachToTarget', { targetId, flatten: true });
  await call('Runtime.enable', {}, sessionId);
  const renderer = await readFile(new URL('emoji-art.js', import.meta.url), 'utf8');
  const palettes = JSON.parse(await readFile(new URL('../../Content/Source/Art/profession-palettes.json', import.meta.url), 'utf8'));
  let result = await call('Runtime.evaluate', { expression: `globalThis.professionPalettes = ${JSON.stringify(palettes)};\n` + renderer, awaitPromise: true }, sessionId);
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.text);
  for (const [id, recipe] of entries) {
    const prepared = { ...recipe };
    const fusionPath = prepared.fusion ?? prepared.Fusion;
    if (fusionPath) {
      if (typeof fusionPath !== 'string' || fusionPath !== `Fusions/${id}.png` || fusionPath.includes('\\') || fusionPath.includes('..')) throw new Error(`Unsafe fusion path for ${id}.`);
      const artRoot = resolve(dirname(recipesPath));
      const absolute = resolve(artRoot, fusionPath);
      if (!absolute.startsWith(artRoot + sep)) throw new Error(`Fusion path escapes art root for ${id}.`);
      prepared.fusionDataUrl = `data:image/png;base64,${(await readFile(absolute)).toString('base64')}`;
    }
    const expression = `(async () => { await document.fonts.ready; const c = document.createElement('canvas'); await renderEmoji(c, ${JSON.stringify(prepared)}, 512); return c.toDataURL('image/png'); })()`;
    result = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }, sessionId);
    if (result.exceptionDetails || !result.result.value?.startsWith('data:image/png;base64,')) throw new Error(`Cannot render ${id}: ${result.exceptionDetails?.text}`);
    await writeFile(join(output, id + '.png'), Buffer.from(result.result.value.split(',')[1], 'base64'));
  }
  console.log(`Exported ${entries.length} new emoji PNGs to ${output}`);
} finally {
  browser.kill();
  await new Promise(resolveExit => { if (browser.exitCode !== null) resolveExit(); else { browser.once('exit', resolveExit); setTimeout(resolveExit, 3000); } });
  // Only the unique temporary profile created above is removed, never an existing browser profile.
  if (resolve(profile).startsWith(resolve(tmpdir()) + (process.platform === 'win32' ? '\\' : '/')) && dirname(profile) === tmpdir()) {
    await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 150 });
  }
}
