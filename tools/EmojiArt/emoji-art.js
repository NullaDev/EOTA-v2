/* The browser and headless exporter share this renderer. Fusions prefer cached PNG data. */
"use strict";
async function renderEmoji(canvas, recipe, size = 512) {
  const emoji = recipe.emoji ?? recipe.Emoji;
  const profession = recipe.profession ?? recipe.Profession;
  const palette = profession ? globalThis.professionPalettes?.[profession] : undefined;
  if (profession && !palette) throw new Error('未知职业配色。');
  const top = palette?.top ?? recipe.top ?? recipe.Top;
  const bottom = palette?.bottom ?? recipe.bottom ?? recipe.Bottom;
  const style = recipe.style ?? recipe.Style ?? "card";
  if (typeof emoji !== "string" || !emoji.trim() || emoji.length > 64 ||
      !/^#[0-9a-f]{6}$/i.test(top) || !/^#[0-9a-f]{6}$/i.test(bottom) || ![256, 512, 1024].includes(size)) {
    throw new Error("请输入 emoji、有效颜色和支持的尺寸。");
  }
  const fusion = recipe.fusionDataUrl ?? recipe.FusionDataUrl ?? recipe.fusionSource ?? recipe.FusionSource;
  if (!fusion && [...new Intl.Segmenter('zh', { granularity: 'grapheme' }).segment(emoji.trim())].length !== 1) {
    throw new Error("多个 emoji 请使用双 emoji 合并，不能并排绘制成融合图。");
  }
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext("2d");
  ctx.scale(size / 512, size / 512);
  if (style !== "card" && style !== "icon") throw new Error("未知图案样式。");
  const bg = ctx.createLinearGradient(0, 0, 0, 512);
  bg.addColorStop(0, top); bg.addColorStop(1, bottom);
  if (style === "card") {
  ctx.fillStyle = bg; ctx.fillRect(0, 0, 512, 512);
  ctx.fillStyle = "#ffffff12"; ctx.beginPath(); ctx.arc(256, 250, 177, 0, 2 * Math.PI); ctx.fill();
  ctx.strokeStyle = "#ffffff2e"; ctx.lineWidth = 2; ctx.beginPath(); ctx.arc(256, 250, 187, 0, 2 * Math.PI); ctx.stroke();
  }
  if (fusion) {
    if (typeof fusion !== "string" || (!fusion.startsWith("data:image/png;base64,") && !fusion.startsWith("https://"))) throw new Error("融合图必须是本地 PNG 数据或 HTTPS 来源。");
    const image = new Image(); image.crossOrigin = "anonymous";
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => { image.src = ''; reject(new Error("融合图载入超时，请重试。")); }, 15000);
      image.onload = () => { clearTimeout(timer); resolve(); };
      image.onerror = () => { clearTimeout(timer); reject(new Error("融合图载入失败。")); };
      image.src = fusion;
    });
    const edge = style === "icon" ? 350 : 310, offset = (512 - edge) / 2;
    ctx.drawImage(image, offset, offset, edge, edge);
  } else {
    ctx.font = '220px "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", sans-serif';
    ctx.textAlign = "center"; ctx.textBaseline = "middle";
    const width = ctx.measureText(emoji).width;
    if (width > 390) ctx.font = `${Math.floor(220 * 390 / width)}px "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", sans-serif`;
    ctx.fillStyle = "#fff"; ctx.fillText(emoji, 256, 258);
  }
  return canvas;
}
globalThis.renderEmoji = renderEmoji;
