"use strict";
(() => {
  const $ = id => document.getElementById(id), fusion = globalThis.emojiFusion;
  let recipes = {}, activeFusion, revision = 0, request;
  const status = (text, state) => { $('status').textContent = text; $('status').dataset.state = state; };
  function candidates() {
    const values = fusion.compatible($('emoji').value);
    $('partner-options').replaceChildren(...values.map(value => new Option(value, value)));
    $('partners').replaceChildren(new Option(`选择可合并的 emoji（${values.length} 个）`, ''), ...values.map(value => new Option(value, value)));
    if (values.includes($('emoji2').value)) $('partners').value = $('emoji2').value;
  }
  function modeChanged() {
    const isFusion = $('mode').value === 'fusion';
    $('second-emoji').hidden = $('fusion-controls').hidden = !isFusion;
    candidates();
  }
  async function refresh() {
    const ticket = ++revision;
    request?.abort(); request = new AbortController();
    $('download').disabled = true; $('merge').disabled = false;
    $('preview').getContext('2d').clearRect(0, 0, $('preview').width, $('preview').height);
    try {
      const isFusion = $('mode').value === 'fusion';
      const first = fusion.single($('emoji').value);
      let result;
      if (isFusion) {
        fusion.single($('emoji2').value);
        if (!activeFusion) { status('选好两个 emoji 后，点击“合并 emoji”。', 'waiting'); return; }
        $('merge').disabled = true;
        status(activeFusion.dataUrl ? '正在绘制融合图…' : '正在获取融合图…', 'loading');
        result = await fusion.load(activeFusion, request.signal);
        if (ticket !== revision) return;
        activeFusion = result;
      }
      const canvas = document.createElement('canvas');
      await renderEmoji(canvas, {
        emoji: first + (isFusion ? $('emoji2').value.trim() : ''), top: $('top').value, bottom: $('bottom').value,
        style: $('style').value, fusionDataUrl: result?.dataUrl
      }, Number($('size').value));
      if (ticket !== revision) return;
      const preview = $('preview'); preview.width = preview.height = canvas.width;
      preview.getContext('2d').drawImage(canvas, 0, 0);
      $('download').disabled = false;
      status(result ? (result.cached ? '合并完成 · 使用仓库自带的离线缓存。'
        : result.storage === 'browser' ? '合并完成 · 已保存在本机浏览器，下次可离线复用。'
        : '合并完成 · 本机持久缓存不可用，请下载 PNG 保存。') : '预览已更新 · 可下载 PNG。', 'ready');
      if (result) document.dispatchEvent(new Event('fusion-cache-changed'));
    } catch (error) {
      if (ticket === revision) status(error.name === 'TypeError' || error.name === 'TimeoutError'
        ? '无法获取融合图，请检查网络后重试；项目缓存中的组合可离线使用。' : error.message, 'error');
    } finally { if (ticket === revision) $('merge').disabled = false; }
  }
  $('emoji-options').replaceChildren(...fusion.supported.map(value => new Option(value, value)));
  for (const id of ['emoji', 'emoji2']) $(id).addEventListener('input', () => {
    activeFusion = undefined; candidates(); refresh();
  });
  $('mode').addEventListener('change', () => { activeFusion = undefined; modeChanged(); refresh(); });
  for (const id of ['top', 'bottom', 'size', 'style']) $(id).addEventListener('input', refresh);
  $('partners').addEventListener('change', () => {
    if (!$('partners').value) return;
    $('emoji2').value = $('partners').value; activeFusion = undefined; refresh();
  });
  $('merge').addEventListener('click', () => {
    try { activeFusion = fusion.lookup($('emoji').value, $('emoji2').value); refresh(); }
    catch (error) { status(error.message, 'error'); $('download').disabled = true; }
  });
  $('download').addEventListener('click', () => {
    if ($('download').disabled) return;
    const ticket = revision, name = ($('name').value.replace(/[^\p{L}\p{N}_-]/gu, '_') || 'emoji-card') + '.png';
    try {
      $('preview').toBlob(blob => {
        if (ticket !== revision) return;
        if (!blob) { status('导出失败，请重试。', 'error'); return; }
        const url = URL.createObjectURL(blob), link = document.createElement('a');
        link.href = url; link.download = name; link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
        status('PNG 已生成。', 'ready');
      }, 'image/png');
    } catch (error) { status('无法导出 PNG：' + error.message, 'error'); }
  });
  $('recipes').addEventListener('change', async () => {
    try {
      const file = $('recipes').files[0]; if (!file) return;
      if (file.size > 1024 * 1024) throw new Error('卡图配置文件过大。');
      const parsed = JSON.parse(await file.text());
      if (!parsed || Array.isArray(parsed) || typeof parsed !== 'object') throw new Error('请选择按条目 ID 存放绘图设置的卡图配置 JSON。');
      const keys = Object.keys(parsed).sort(); if (!keys.length) throw new Error('卡图配置文件为空。');
      recipes = parsed;
      $('cards').replaceChildren(...keys.map(id => new Option(id, id)));
      $('cards').hidden = $('config-selection').hidden = false;
      $('config-status').textContent = `已读取 ${file.name}，共 ${keys.length} 个条目。选择条目会填入下方绘图设置。`;
      $('cards').dispatchEvent(new Event('change'));
    } catch (error) {
      ++revision; request?.abort(); $('download').disabled = true;
      $('config-status').textContent = '导入失败：' + error.message;
      status(error.message, 'error');
    }
  });
  $('cards').addEventListener('change', () => {
    try {
      const id = $('cards').value, recipe = recipes[id];
      if (!recipe || typeof recipe !== 'object') throw new Error('无效的卡图配置条目。');
      const emoji = recipe.emoji ?? recipe.Emoji;
      if (typeof emoji !== 'string') throw new Error('配置条目缺少 emoji；请选择卡图配置，而非游戏卡牌内容包。');
      const parts = fusion.split(emoji), result = fusion.fromRecipe(recipe);
      if (result && parts.length !== 2) throw new Error('融合配置需要两个源 emoji。');
      $('mode').value = result ? 'fusion' : 'single';
      $('emoji').value = result ? parts[0] : emoji;
      if (result) $('emoji2').value = parts[1];
      $('top').value = recipe.top ?? recipe.Top ?? '#000000';
      $('bottom').value = recipe.bottom ?? recipe.Bottom ?? '#000000';
      $('style').value = recipe.style ?? recipe.Style ?? 'card'; $('name').value = id;
      activeFusion = result; modeChanged(); refresh();
    } catch (error) {
      ++revision; request?.abort(); $('download').disabled = true;
      status(error.message, 'error');
    }
  });
  modeChanged(); refresh();
})();
