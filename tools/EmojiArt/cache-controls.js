"use strict";
(() => {
  const $ = id => document.getElementById(id), storage = globalThis.fusionStorage;
  async function summary() {
    try { $('cache-summary').textContent = `仓库自带 ${emojiFusion.bundledCount} 张原图；本机已保存 ${await storage.count()} 张。尚未获取的组合仍需联网。`; }
    catch { $('cache-summary').textContent = `仓库自带 ${emojiFusion.bundledCount} 张原图。本机浏览器暂不允许持久缓存。`; }
  }
  $('export-cache').addEventListener('click', async () => {
    try {
      const bundle = await storage.exportBundle();
      if (!bundle.images.length) { $('cache-status').textContent = '本机还没有新增的融合缓存。仓库自带素材已经包含在工具文件中。'; return; }
      const blob = new Blob([JSON.stringify(bundle)], { type: 'application/json' });
      const url = URL.createObjectURL(blob), link = document.createElement('a');
      link.href = url; link.download = 'emoji-fusion-cache.json'; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
      $('cache-status').textContent = `已导出 ${bundle.images.length} 张融合原图，可备份或交给其他人导入。`;
    } catch (error) { $('cache-status').textContent = '导出失败：' + error.message; }
  });
  $('import-cache').addEventListener('change', async () => {
    try {
      const file = $('import-cache').files[0]; if (!file) return;
      if (file.size > 100 * 1024 * 1024) throw new Error('缓存文件超过 100 MiB，请拆分后导入。');
      const count = await storage.importBundle(JSON.parse(await file.text()));
      $('cache-status').textContent = `已导入 ${count} 张融合原图。选择对应组合后点击“合并 emoji”即可离线使用。`;
      await summary();
    } catch (error) { $('cache-status').textContent = '导入失败：' + error.message; }
  });
  document.addEventListener('fusion-cache-changed', summary);
  summary();
})();
