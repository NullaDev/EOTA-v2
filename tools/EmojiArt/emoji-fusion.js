/* Local lookup only. The compatibility snapshot and its license are in vendor/. */
"use strict";
globalThis.emojiFusion = (() => {
  const data = globalThis.emojiCompatibility;
  const segmenter = new Intl.Segmenter('zh', { granularity: 'grapheme' });
  const split = text => [...segmenter.segment(text.trim())].map(item => item.segment);
  const key = text => [...text].filter(char => !/[\ufe0e\ufe0f]/u.test(char)).map(char => char.codePointAt(0).toString(16)).join('-');
  const glyph = code => String.fromCodePoint(...code.split('-').map(part => parseInt(part, 16)));
  const code = value => typeof value === 'number' ? value.toString(16) : value;
  const pairKey = (left, right) => [key(left), key(right)].sort().join('|');
  const variants = new Map();
  for (const stored of Object.keys(data).filter(value => !value.startsWith('$'))) {
    const normalized = key(glyph(stored));
    if (!variants.has(normalized)) variants.set(normalized, []);
    variants.get(normalized).push(stored);
  }
  const cachedPairs = new Map();
  const cachedSources = globalThis.emojiFusionCache ?? {};
  for (const [source, cached] of Object.entries(cachedSources)) {
    const parts = split(cached.emoji);
    if (parts.length === 2) cachedPairs.set(pairKey(...parts), { source, dataUrl: cached.dataUrl, cached: true });
  }
  const neighbors = new Map();
  function connect(left, right) {
    if (!neighbors.has(left)) neighbors.set(left, new Set());
    neighbors.get(left).add(right);
  }
  for (const cached of Object.values(cachedSources)) {
    const parts = split(cached.emoji);
    if (parts.length !== 2) continue;
    const [left, right] = parts.map(key);
    connect(left, right); connect(right, left);
    for (const value of [left, right]) if (!variants.has(value)) variants.set(value, []);
  }
  for (const [left, rows] of Object.entries(data)) {
    if (left.startsWith('$')) continue;
    for (const [index] of rows) {
      const a = key(glyph(left)), b = key(glyph(code(data.$e[index])));
      connect(a, b); connect(b, a);
    }
  }
  function single(value) {
    if (typeof value !== 'string' || value.length > 64) throw new Error('每个输入框只填一个 emoji。');
    const parts = split(value);
    if (parts.length !== 1 || !/[\p{Extended_Pictographic}\p{Regional_Indicator}\u20e3]/u.test(parts[0])) {
      throw new Error('每个输入框只填一个 emoji；双 emoji 请使用融合模式。');
    }
    return parts[0];
  }
  function lookup(left, right) {
    left = single(left); right = single(right);
    const cached = cachedPairs.get(pairKey(left, right));
    if (cached) return { ...cached, emoji: left + right };
    const leftVariants = variants.get(key(left)), rightVariants = variants.get(key(right));
    if (!leftVariants || !rightVariants) throw new Error('当前融合表不支持这个 emoji，请从候选列表选择。');
    let newest;
    function search(anchors, wanted) {
      for (const anchor of anchors) {
        for (const [index, dateIndex] of data[anchor] ?? []) {
          const other = code(data.$e[index]), date = data.$d[dateIndex];
          if (key(glyph(other)) === key(wanted) && (!newest || date > newest.date)) newest = { left: anchor, right: other, date };
        }
      }
    }
    // The upstream image path has an order even though the user's pair does not.
    search(leftVariants, right); search(rightVariants, left);
    if (!newest) throw new Error('这两个 emoji 暂无融合图，请换一个组合。');
    const path = value => value.split('-').map(part => 'u' + part).join('-');
    const a = path(newest.left), b = path(newest.right);
    return { source: `https://www.gstatic.com/android/keyboard/emojikitchen/${newest.date}/${a}/${a}_${b}.png`, emoji: left + right, cached: false };
  }
  const downloaded = new Map();
  async function load(result, signal) {
    if (result.dataUrl) return result;
    if (downloaded.has(result.source)) return { ...result, ...downloaded.get(result.source) };
    if (typeof result.source !== 'string' || new URL(result.source).protocol !== 'https:') throw new Error('融合图来源必须使用 HTTPS。');
    try {
      const saved = await globalThis.fusionStorage.get(result.source);
      if (saved) return { ...result, dataUrl: saved.dataUrl, storage: 'browser' };
    } catch { /* Network loading remains usable when browser storage is disabled. */ }
    const timeout = AbortSignal.timeout(15000);
    const response = await fetch(result.source, { signal: signal ? AbortSignal.any([signal, timeout]) : timeout });
    if (!response.ok) throw new Error(`融合图下载失败（${response.status}），请换一个组合或重试。`);
    const blob = await response.blob();
    if (blob.type !== 'image/png' || blob.size > 4 * 1024 * 1024) throw new Error('融合来源未返回有效 PNG。');
    const dataUrl = await new Promise((resolve, reject) => {
      const reader = new FileReader(); reader.onload = () => resolve(reader.result); reader.onerror = () => reject(reader.error); reader.readAsDataURL(blob);
    });
    let storage = 'memory';
    try {
      await globalThis.fusionStorage.put({ source: result.source, emoji: result.emoji, dataUrl });
      storage = 'browser';
    } catch { /* Report the loss of persistence to the user, while allowing PNG export. */ }
    downloaded.set(result.source, { dataUrl, storage });
    return { ...result, dataUrl, storage };
  }
  function fromRecipe(recipe) {
    const source = recipe.fusionSource ?? recipe.FusionSource;
    const dataUrl = recipe.fusionDataUrl ?? recipe.FusionDataUrl ?? cachedSources[source]?.dataUrl;
    return source || dataUrl ? { source, dataUrl, emoji: recipe.emoji ?? recipe.Emoji, cached: !!cachedSources[source] } : undefined;
  }
  return {
    split, single, lookup, load, fromRecipe, bundledCount: Object.keys(cachedSources).length,
    supported: [...variants.keys()].sort().map(glyph),
    compatible: value => [...(neighbors.get(key(value.trim())) ?? [])].sort().map(glyph)
  };
})();
