/* Browser persistence and a portable, data-only cache format. */
"use strict";
globalThis.fusionStorage = (() => {
  const databaseName = 'eota-emoji-fusions-v1', storeName = 'images';
  let connection;
  function open() {
    if (!connection) connection = new Promise((resolve, reject) => {
      const request = indexedDB.open(databaseName, 1);
      request.onupgradeneeded = () => request.result.createObjectStore(storeName, { keyPath: 'source' });
      request.onsuccess = () => {
        const db = request.result;
        db.onversionchange = () => { db.close(); connection = undefined; };
        resolve(db);
      };
      request.onerror = () => reject(request.error);
      request.onblocked = () => reject(new Error('缓存数据库被其他页面占用，请关闭旧页面后重试。'));
    }).catch(error => { connection = undefined; throw error; });
    return connection;
  }
  async function transact(mode, action) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const transaction = db.transaction(storeName, mode);
      const request = action(transaction.objectStore(storeName));
      transaction.oncomplete = () => resolve(request?.result);
      transaction.onabort = () => reject(transaction.error ?? new Error('缓存写入失败。'));
      transaction.onerror = () => {};
    });
  }
  function validate(bundle) {
    if (bundle?.format !== 'eota-emoji-fusion-cache' || bundle.version !== 1 || !Array.isArray(bundle.images)) {
      throw new Error('请选择由“导出本机融合缓存”生成的 JSON；卡图配置请在页面顶部导入。');
    }
    const seen = new Set();
    return bundle.images.map(item => {
      if (typeof item?.source !== 'string' || !/^https:\/\/www\.gstatic\.com\/android\/keyboard\/emojikitchen\/\d{8}\/u[0-9a-f]+(?:-u[0-9a-f]+)*\/u[0-9a-f]+(?:-u[0-9a-f]+)*_u[0-9a-f]+(?:-u[0-9a-f]+)*\.png$/.test(item.source)
        || typeof item.emoji !== 'string' || item.emoji.length > 128
        || typeof item.dataUrl !== 'string' || item.dataUrl.length > 6 * 1024 * 1024
        || !/^data:image\/png;base64,iVBORw0KGgo[A-Za-z0-9+/]*={0,2}$/.test(item.dataUrl)) {
        throw new Error('缓存中包含无效的融合来源或 PNG 数据。');
      }
      const parts = [...new Intl.Segmenter('zh', { granularity: 'grapheme' }).segment(item.emoji.trim())];
      if (parts.length !== 2 || seen.has(item.source)) throw new Error('缓存条目需要两个源 emoji，且来源不能重复。');
      seen.add(item.source);
      return { source: item.source, emoji: item.emoji, dataUrl: item.dataUrl };
    });
  }
  async function importBundle(bundle) {
    const images = validate(bundle);
    // Decode all images before opening the transaction: no partially imported file.
    for (const item of images) {
      const image = new Image(); image.src = item.dataUrl;
      await image.decode();
      if (image.width > 4096 || image.height > 4096) throw new Error('缓存图片尺寸过大。');
    }
    await transact('readwrite', store => { for (const item of images) store.put(item); });
    return images.length;
  }
  return {
    databaseName, storeName, validate, importBundle,
    get: source => transact('readonly', store => store.get(source)),
    put: item => transact('readwrite', store => store.put(item)),
    count: () => transact('readonly', store => store.count()),
    exportBundle: async () => ({ format: 'eota-emoji-fusion-cache', version: 1, images: await transact('readonly', store => store.getAll()) })
  };
})();
